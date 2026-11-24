"""Word → PDF 转换服务。POST /convert（multipart 字段 file）→ 返回 PDF；GET /health。
POST /check（multipart 字段 file）→ 检查助手做的文件能不能用：能不能打开、Excel 公式重算后有没有错、Word 修订批注各几处、几页。
POST /render?width=480&max=40（multipart 字段 file，PPT / Word / PDF）→ 每页转一张 JPEG：{"pages": [base64...], "total": 总页数}。
PPT 模板库的缩略图、助手做的 PPT 在文件卡上的每页预览都用它。

每次转换用独立的临时目录和 LibreOffice 用户配置，跑完删掉；同一时间只转一份（LibreOffice 不适合并发）。
只在容器网络里可见，不对外开端口。
"""
import base64
import cgi
import json
import os
import re
import shutil
import subprocess
import tempfile
import threading
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MAX_IN = 50 * 1024 * 1024
LOCK = threading.Lock()
ERRORS = ("#REF!", "#DIV/0!", "#VALUE!", "#NAME?", "#N/A", "#NUM!", "#NULL!")
# 打开 Excel 时一律重算公式（openpyxl 写的公式没有缓存值，不重算就查不出 #REF! 这类错）
XCU = """<?xml version="1.0" encoding="UTF-8"?>
<oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
<item oor:path="/org.openoffice.Office.Calc/Formula/Load"><prop oor:name="OOXMLRecalcMode" oor:op="fuse"><value>0</value></prop></item>
<item oor:path="/org.openoffice.Office.Calc/Formula/Load"><prop oor:name="ODFRecalcMode" oor:op="fuse"><value>0</value></prop></item>
</oor:items>
"""


def soffice(src, work, fmt, outdir):
    """LibreOffice 转一遍；转不出来返回 None。"""
    profile = os.path.join(work, "profile")
    os.makedirs(os.path.join(profile, "user"), exist_ok=True)
    with open(os.path.join(profile, "user", "registrymodifications.xcu"), "w", encoding="utf-8") as f:
        f.write(XCU)
    os.makedirs(outdir, exist_ok=True)
    with LOCK:
        r = subprocess.run(["soffice", f"-env:UserInstallation=file://{profile}", "--headless", "--norestore",
                            "--convert-to", fmt, "--outdir", outdir, src], capture_output=True, timeout=120)
    out = os.path.join(outdir, os.path.splitext(os.path.basename(src))[0] + "." + fmt.split(":")[0])
    return out if r.returncode == 0 and os.path.exists(out) else None


def pdf_pages(path):
    from pypdf import PdfReader
    return len(PdfReader(path).pages)


def _disp(text):
    """一行字大约占几个「列宽单位」（Excel 列宽按默认字体一个数字的宽度算）：中文、全角算 2，其余算 1。"""
    import unicodedata
    return sum(2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1 for ch in text)


def xlsx_layout(wb, limit=8):
    """排版问题：字比格子宽、旁边格子又有内容——打开后被截断、或者和旁边的字挤在一起（看着像串行）。
    自动换行、缩小填充的格子不算；合并单元格按合并后的总宽算；字号大于 11 按比例放大。"""
    from openpyxl.utils import get_column_letter
    issues = []
    for ws in wb.worksheets:
        merged = {}
        for rng in ws.merged_cells.ranges:
            for r in range(rng.min_row, rng.max_row + 1):
                for c in range(rng.min_col, rng.max_col + 1):
                    merged[(r, c)] = rng
        default_w = ws.sheet_format.defaultColWidth or 8.43

        def width(c):
            d = ws.column_dimensions.get(get_column_letter(c))
            return d.width if d is not None and d.width else default_w

        def filled(r, c):
            if c < 1:
                return False
            rng = merged.get((r, c))
            cell = ws._cells.get((rng.min_row, rng.min_col) if rng else (r, c))
            return cell is not None and cell.value is not None

        for row in ws.iter_rows():
            for cell in row:
                v = cell.value
                if not isinstance(v, str) or not v.strip() or v.startswith("="):
                    continue
                al = cell.alignment
                if al is not None and (al.wrap_text or al.shrink_to_fit):
                    continue
                size = (cell.font.sz or 11) if cell.font is not None else 11
                need = max(_disp(line) for line in v.split("\n")) * max(1.0, size / 11) * (1.08 if cell.font is not None and cell.font.b else 1.0)
                r, c = cell.row, cell.column
                rng = merged.get((r, c))
                c1, c2 = (rng.min_col, rng.max_col) if rng else (c, c)
                have = sum(width(x) for x in range(c1, c2 + 1))
                if need <= have + 1:
                    continue
                horizontal = al.horizontal if al is not None else None
                sides = [c2 + 1] + ([c1 - 1] if horizontal in ("center", "centerContinuous", "right") else [])
                if any(filled(r, x) for x in sides):
                    issues.append(f"{ws.title}!{cell.coordinate}「{v[:14]}」比格子宽（约 {need:.0f}，格宽 {have:.0f}），旁边格子有内容，打开后会被截断或和旁边挤在一起")
                    if len(issues) >= limit:
                        return issues
    return issues


def check(src, ext, work, convert=soffice):
    """返回 (问题列表, 信息, 重算后的文件)。问题为空就算过。
    Excel 交付重算后的那一版：openpyxl 写的公式没有计算结果，手机、微信里预览会是空白；重算后公式和结果都在。"""
    problems, info, fixed = [], {}, None
    if ext == ".pdf":
        try:
            from pypdf import PdfReader
            r = PdfReader(src)
            if r.is_encrypted:
                problems.append("PDF 加了密码，员工打不开")
            info["pages"] = len(r.pages)
        except Exception as e:  # noqa: BLE001 - 什么错都算打不开
            problems.append(f"PDF 打不开：{type(e).__name__}")
        return problems, info, None
    try:
        with zipfile.ZipFile(src) as z:
            names = z.namelist()
            if z.testzip() is not None:
                problems.append("文件内容有损坏")
            doc_xml = z.read("word/document.xml").decode("utf-8", "replace") if "word/document.xml" in names else ""
            cmt_xml = z.read("word/comments.xml").decode("utf-8", "replace") if "word/comments.xml" in names else ""
    except zipfile.BadZipFile:
        return ["不是合法的 Office 文件（打不开）"], info, None
    if ext == ".xlsx":
        import openpyxl
        try:
            wb = openpyxl.load_workbook(src)
        except Exception as e:  # noqa: BLE001
            return [f"Excel 打不开：{type(e).__name__}"], info, None
        info["sheets"] = len(wb.worksheets)
        info["formulas"] = sum(1 for ws in wb.worksheets for row in ws.iter_rows() for c in row if c.data_type == "f")
        # 排版：只报给助手改，不算「打不开」（后端决定是让它重做还是照样交付）
        try:
            info["layout"] = xlsx_layout(wb)
        except Exception:  # noqa: BLE001 - 排版检查出错不影响交付
            info["layout"] = []
        out = convert(src, work, "xlsx:Calc MS Excel 2007 XML", os.path.join(work, "recalc"))
        if out is None:
            problems.append("LibreOffice 打不开这个 Excel")
        else:
            wb2 = openpyxl.load_workbook(out, data_only=True)
            errs = [f"{ws.title}!{c.coordinate} {c.value}" for ws in wb2.worksheets for row in ws.iter_rows() for c in row
                    if isinstance(c.value, str) and c.value.strip() in ERRORS]
            info["errors"] = len(errs)
            with open(out, "rb") as f:
                fixed = f.read()
            if errs:
                problems.append(f"公式重算后有 {len(errs)} 处出错：" + "、".join(errs[:8]) + ("……" if len(errs) > 8 else ""))
    elif ext in (".docx", ".pptx"):
        if ext == ".docx":
            if not doc_xml:
                return ["不是 Word 文件（缺 word/document.xml）"], info, None
            info["revisions"] = len(re.findall(r"<w:(?:ins|del)\b", doc_xml))
            info["comments"] = len(re.findall(r"<w:comment\b", cmt_xml))
        else:
            info["slides"] = len([n for n in names if re.match(r"ppt/slides/slide\d+\.xml$", n)])
            if info["slides"] == 0:
                problems.append("PPT 里一页都没有")
        pdf = convert(src, work, "pdf", os.path.join(work, "pdf"))
        if pdf is None:
            problems.append("LibreOffice 打不开这个文件")
        else:
            try:
                info["pages"] = pdf_pages(pdf)
            except Exception:  # noqa: BLE001
                problems.append("转出来的 PDF 坏了")
    return problems, info, fixed


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def _send(self, code, body, ctype="application/json"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._send(200, b'{"ok":true}')
        else:
            self._send(404, b'{"error":"not found"}')

    def do_POST(self):
        if self.path == "/check":
            return self._check()
        if self.path.startswith("/render"):
            return self._render()
        if self.path != "/convert":
            return self._send(404, b'{"error":"not found"}')
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0 or length > MAX_IN:
            return self._send(413, b'{"error":"file too large"}')
        form = cgi.FieldStorage(fp=self.rfile, headers=self.headers,
                                environ={"REQUEST_METHOD": "POST", "CONTENT_TYPE": self.headers.get("Content-Type", "")})
        if "file" not in form or not getattr(form["file"], "file", None):
            return self._send(400, b'{"error":"missing file"}')
        name = os.path.basename(form["file"].filename or "input.docx")
        ext = os.path.splitext(name)[1].lower()
        if ext not in (".docx", ".doc", ".xlsx", ".pptx", ".odt", ".rtf"):
            ext = ".docx"
        work = tempfile.mkdtemp(prefix="conv-")
        try:
            src = os.path.join(work, "input" + ext)
            with open(src, "wb") as f:
                shutil.copyfileobj(form["file"].file, f)
            with LOCK:
                r = subprocess.run(
                    ["soffice", f"-env:UserInstallation=file://{work}/profile", "--headless", "--norestore",
                     "--convert-to", "pdf", "--outdir", work, src],
                    capture_output=True, timeout=120)
            out = os.path.join(work, "input.pdf")
            if r.returncode != 0 or not os.path.exists(out):
                return self._send(500, b'{"error":"convert failed"}')
            with open(out, "rb") as f:
                self._send(200, f.read(), "application/pdf")
        except subprocess.TimeoutExpired:
            self._send(504, b'{"error":"timeout"}')
        finally:
            shutil.rmtree(work, ignore_errors=True)


    def _check(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0 or length > MAX_IN:
            return self._send(413, b'{"error":"file too large"}')
        form = cgi.FieldStorage(fp=self.rfile, headers=self.headers,
                                environ={"REQUEST_METHOD": "POST", "CONTENT_TYPE": self.headers.get("Content-Type", "")})
        if "file" not in form or not getattr(form["file"], "file", None):
            return self._send(400, b'{"error":"missing file"}')
        ext = os.path.splitext(form["file"].filename or "")[1].lower()
        if ext not in (".xlsx", ".docx", ".pptx", ".pdf"):
            body = {"ok": True, "problems": [], "info": {}, "note": "这种文件不检查"}
            return self._send(200, json.dumps(body, ensure_ascii=False).encode(), "application/json; charset=utf-8")
        work = tempfile.mkdtemp(prefix="chk-")
        try:
            src = os.path.join(work, "input" + ext)
            with open(src, "wb") as f:
                shutil.copyfileobj(form["file"].file, f)
            problems, info, fixed = check(src, ext, work)
            body = {"ok": not problems, "problems": problems, "info": info}
            if fixed and not problems:
                body["recalculated"] = base64.b64encode(fixed).decode()
            self._send(200, json.dumps(body, ensure_ascii=False).encode(), "application/json; charset=utf-8")
        except subprocess.TimeoutExpired:
            self._send(504, b'{"error":"timeout"}')
        finally:
            shutil.rmtree(work, ignore_errors=True)


    def _render(self):
        from urllib.parse import parse_qs, urlparse
        q = parse_qs(urlparse(self.path).query)
        width = max(120, min(int((q.get("width") or ["480"])[0]), 1600))
        most = max(1, min(int((q.get("max") or ["40"])[0]), 80))
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0 or length > MAX_IN:
            return self._send(413, b'{"error":"file too large"}')
        form = cgi.FieldStorage(fp=self.rfile, headers=self.headers,
                                environ={"REQUEST_METHOD": "POST", "CONTENT_TYPE": self.headers.get("Content-Type", "")})
        if "file" not in form or not getattr(form["file"], "file", None):
            return self._send(400, b'{"error":"missing file"}')
        ext = os.path.splitext(form["file"].filename or "")[1].lower()
        if ext not in (".pptx", ".docx", ".pdf"):
            return self._send(400, b'{"error":"only pptx/docx/pdf"}')
        work = tempfile.mkdtemp(prefix="ren-")
        try:
            src = os.path.join(work, "input" + ext)
            with open(src, "wb") as f:
                shutil.copyfileobj(form["file"].file, f)
            pdf = src if ext == ".pdf" else soffice(src, work, "pdf", os.path.join(work, "pdf"))
            if pdf is None:
                return self._send(500, b'{"error":"convert failed"}')
            total = pdf_pages(pdf)
            outdir = os.path.join(work, "img")
            os.makedirs(outdir)
            subprocess.run(["pdftoppm", "-jpeg", "-jpegopt", "quality=80", "-scale-to-x", str(width), "-scale-to-y", "-1",
                            "-f", "1", "-l", str(min(total, most)), pdf, os.path.join(outdir, "p")], capture_output=True, timeout=120)
            pages = []
            for name in sorted(os.listdir(outdir), key=lambda n: int(re.sub(r"\D", "", n) or 0)):
                with open(os.path.join(outdir, name), "rb") as f:
                    pages.append(base64.b64encode(f.read()).decode())
            body = {"pages": pages, "total": total}
            self._send(200, json.dumps(body).encode(), "application/json; charset=utf-8")
        except subprocess.TimeoutExpired:
            self._send(504, b'{"error":"timeout"}')
        finally:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", 3000), Handler).serve_forever()
