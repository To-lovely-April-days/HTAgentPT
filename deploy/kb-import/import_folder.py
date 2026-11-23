#!/usr/bin/env python3
"""把一个整理好的文件夹批量导进知识库：一个子文件夹对应一个知识库和文档类别。

文件夹结构（子文件夹名决定导到哪里，见下面 FOLDERS；没列出的子文件夹按「库名 = 文件夹名、类别 = 资料」导）：
    kb-import/
    ├─ 标准规范/      → 知识库「标准规范」，类别「标准规范」
    ├─ 已作废标准/    → 知识库「标准规范」，类别「标准规范」，标题前加【已作废】，版本写「已作废」
    ├─ 设计手册/      → 知识库「设计手册」，类别「手册」
    └─ 公司资料/      → 知识库「公司资料」，类别「资料」
密级一律「内部」（员工能查，客户门户看不到）；客户名称写「通用资料」；设备类型「通用」（词表里没有会自动加）。
标准的标题规整成「GB/T 150.1-2024 压力容器 第一部分：通用要求」这种写法，年份从文件名里取。

用法（在服务器上，项目目录里）：
    export HT_URL=http://localhost:8080 HT_USER=admin HT_PASS='……'   # 要有资料管理、知识库管理、词表维护权限
    python3 deploy/kb-import/import_folder.py ~/kb-import --dry-run   # 先看会怎么导（不登录、不上传）
    python3 deploy/kb-import/import_folder.py ~/kb-import             # 导入
重跑会跳过库里已有的同名文件。上传完按文件夹顺序提交解析（标准规范先解析），进度在后台「资料管理」里看。
登录会把这个账号在浏览器上的登录挤掉，跑完重新登录即可。只用 Python 标准库。
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import sys
import urllib.error
import urllib.request
import uuid
from pathlib import Path

# 子文件夹 → 导到哪里。顺序就是上传和解析的顺序。
FOLDERS = {
    "标准规范": {"kb": "标准规范", "category": "标准规范"},
    "公司资料": {"kb": "公司资料", "category": "资料"},
    "已作废标准": {"kb": "标准规范", "category": "标准规范", "obsolete": True},
    "设计手册": {"kb": "设计手册", "category": "手册"},
}
KB_DESC = {
    "标准规范": "国家标准、行业标准、安全技术规范和国外标准（ASME、AD 2000 等）。已作废的标题前标【已作废】，引用时用现行版。",
    "设计手册": "设计手册、教材、产品样本、选型手册和软件用户手册。",
    "公司资料": "公司内部流程、标准目录、作业指导等资料。",
}
PARSE_BATCH = 50
SUPPORTED = {".pdf", ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".pptx", ".pptm", ".txt", ".md"}
CUSTOMER = "通用资料"

# 文件名里标准代号的写法 → 规范写法
CODE_FIX = [
    (r"^GB\s*[／∕/]\s*T", "GB/T"), (r"^GBZ\s*[／∕/]?\s*T(?=\s|\d)", "GBZ/T"), (r"^GBT(?=\s|\d)", "GB/T"),
    (r"^HG\s*[／∕/]\s*T", "HG/T"), (r"^HGT(?=\s|\d)", "HG/T"),
    (r"^NB\s*[／∕/]\s*T", "NB/T"), (r"^NBT(?=\s|\d)", "NB/T"),
    (r"^SH\s*[／∕/]\s*T", "SH/T"), (r"^SHT(?=\s|\d)", "SH/T"),
    (r"^JB_T(?=\s|\d)", "JB/T"), (r"^JB\s*[／∕/]\s*T", "JB/T"), (r"^TCATSI(?=\s|\d)", "T/CATSI"),
]
CODE_RE = re.compile(r"^((?:GB/T|GBZ/T|GB|HG/T|NB/T|SH/T|JB/T|T/CATSI|TSG|ASME(?: [A-Z]+[\d.]*)?(?: Sec\.[IVX]+)?|AD 2000 [A-Z]+\d*|DIN(?: V)?)"
                     r"\s?[\d.~～]*)-((?:19|20)\d{2})")


def nice_title(stem: str) -> tuple[str, str | None, int | None]:
    """文件名 → (标题, 标准号带年份, 年份)。认不出标准号的原样返回（去掉网站水印类字样）。"""
    t = stem
    t = re.sub(r"\[[^\]]*(高清|书签|水印)[^\]]*\]", "", t)              # [高清无水印,带详细书签]
    t = re.sub(r"_?茶豆文库|\s可复制$|\(\d\)(\(\d\))*$", "", t)          # 网站名、「可复制」、(1)(1)
    m = re.match(r"^[\(（]\s*([^\)）]+)\s*[\)）]\s*(.*)$", t)             # (GBT 713.1-2023) 标题
    if m:
        t = f"{m.group(1)} {m.group(2)}"
    for pat, rep in CODE_FIX:
        t = re.sub(pat, rep, t)
    t = re.sub(r"^((?:GB/T|GBZ/T|HG/T|NB/T|SH/T|JB/T|T/CATSI))(?=\d)", r"\1 ", t)   # HG/T20553 → HG/T 20553
    t = re.sub(r"^((?:GB|GBT)(?=\d))", "GB ", t) if re.match(r"^GB\d", t) else t   # GB50341 → GB 50341
    code = year = None
    m = CODE_RE.match(t)
    if m:
        code = f"{m.group(1).strip()}-{m.group(2)}"
        year = int(m.group(2))
        rest = t[m.end():]
        rest = re.sub(r"^[\s\-—]+", "", rest)                             # 「-2023-热交换器」「-2023 热交换器」
        t = f"{code} {rest}".strip()
    else:
        t = re.sub(r"^((?:GB/T|GBZ/T|GB|HG/T|NB/T|SH/T|JB/T|T/CATSI|TSG) [\d.~～]+)(?=[\u4e00-\u9fff])", r"\1 ", t)  # 4732.1~6压力 → 4732.1~6 压力
    t = re.sub(r"-(?=第\d+部分)", " ", t)                                 # 「基本参数-第4部分」
    t = t.replace("《", "").replace("》", "")
    t = re.sub(r"\s+", " ", t).strip()
    if year is None:
        y = re.findall(r"(?<!\d)((?:19|20)\d{2})", stem)                  # 也认「2024072」「20240409」这种日期
        year = int(y[-1]) if y else None
    return t, code, year


def api(base, path, token=None, body=None, method=None, raw=None, ctype=None, timeout=120):
    data = raw if raw is not None else (json.dumps(body).encode("utf-8") if body is not None else None)
    req = urllib.request.Request(base + path, data=data, method=method or ("POST" if data is not None else "GET"))
    req.add_header("Content-Type", ctype or "application/json")
    req.add_header("X-Terminal-Id", "kb-import")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            text = r.read().decode("utf-8")
            return json.loads(text) if text else None
    except urllib.error.HTTPError as e:
        msg = e.read().decode("utf-8", "ignore")
        try:
            msg = json.loads(msg).get("message", msg)
        except (ValueError, AttributeError):
            pass
        raise ApiError(e.code, msg) from None


class ApiError(Exception):
    def __init__(self, code, msg):
        super().__init__(f"（{code}）{msg}")
        self.code = code


def multipart(fields, file_name, file_bytes):
    boundary = uuid.uuid4().hex
    out = bytearray()
    for k, v in fields.items():
        out += f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n".encode("utf-8")
    out += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{file_name}\"\r\n"
            f"Content-Type: application/octet-stream\r\n\r\n").encode("utf-8")
    out += file_bytes + f"\r\n--{boundary}--\r\n".encode("utf-8")
    return bytes(out), f"multipart/form-data; boundary={boundary}"


def plan(root: Path, default_year: int):
    """扫一遍文件夹，列出每个文件导到哪里、标题是什么；不支持的格式单独列出。"""
    items, skipped = [], []
    folders = [d for d in sorted(root.iterdir()) if d.is_dir() and not d.name.startswith(".")]
    order = {name: i for i, name in enumerate(FOLDERS)}
    folders.sort(key=lambda d: (order.get(d.name, len(order)), d.name))
    for f in sorted(root.iterdir()):
        if f.is_file() and not f.name.startswith("."):
            skipped.append((f.relative_to(root), "放在最外层，不知道导到哪个库；请放进子文件夹"))
    for d in folders:
        rule = FOLDERS.get(d.name, {"kb": d.name, "category": "资料"})
        for f in sorted(p for p in d.rglob("*") if p.is_file() and not p.name.startswith((".", "~$"))):
            if f.suffix.lower() not in SUPPORTED:
                why = "老格式，请用 Office 另存为新格式（.docx / .xlsx / .pptx）" if f.suffix.lower() in (".doc", ".xls", ".ppt") else "这种格式读不了"
                skipped.append((f.relative_to(root), why))
                continue
            title, code, year = nice_title(f.stem)
            version = code
            if rule.get("obsolete"):
                title = "【已作废】" + title
                version = f"{code}（已作废）" if code else "已作废"
            items.append({"path": f, "folder": d.name, "kb": rule["kb"], "category": rule["category"],
                          "title": title, "version": version, "year": year or default_year, "size": f.stat().st_size})
    return items, skipped


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("root", help="整理好的文件夹，如 ~/kb-import")
    ap.add_argument("--device-type", default="通用", help="设备类型（词表里没有会自动加）")
    ap.add_argument("--classification", default="Internal", choices=["Internal", "Confidential", "Public"], help="密级，默认内部")
    ap.add_argument("--default-year", type=int, default=datetime.date.today().year, help="文件名里没有年份时用的年份")
    ap.add_argument("--only", help="只导这几个子文件夹（逗号隔开）")
    ap.add_argument("--dry-run", action="store_true", help="只列出会怎么导，不登录、不上传")
    a = ap.parse_args()

    root = Path(a.root).expanduser()
    if not root.is_dir():
        raise SystemExit(f"找不到文件夹 {root}")
    items, skipped = plan(root, a.default_year)
    if a.only:
        keep = {x.strip() for x in a.only.split(",")}
        items = [x for x in items if x["folder"] in keep]

    by_folder: dict[str, list] = {}
    for x in items:
        by_folder.setdefault(x["folder"], []).append(x)
    print(f"共 {len(items)} 份，{sum(x['size'] for x in items) / 1048576:.0f} MB：")
    for name, xs in by_folder.items():
        print(f"  {name:<8} → 库「{xs[0]['kb']}」类别「{xs[0]['category']}」 {len(xs)} 份 {sum(x['size'] for x in xs) / 1048576:.0f} MB")
    if skipped:
        print(f"跳过 {len(skipped)} 份：")
        for p, why in skipped:
            print(f"  {p}：{why}")
    if a.dry_run:
        print("\n标题预览（文件名 → 标题 · 版本 · 年份）：")
        for x in items:
            print(f"  [{x['folder']}] {x['path'].name}\n      → {x['title']} · {x['version'] or '-'} · {x['year']}")
        return

    base = os.environ.get("HT_URL", "http://localhost:8080").rstrip("/")
    user, pwd = os.environ.get("HT_USER"), os.environ.get("HT_PASS")
    if not user or not pwd:
        raise SystemExit("请先设置 HT_USER 和 HT_PASS（管理员账号）")
    try:
        token = api(base, "/api/auth/login", body={"username": user, "password": pwd, "terminalId": "kb-import", "terminalName": "批量导入资料"})["token"]
    except ApiError as e:
        raise SystemExit(f"登录失败{e}")

    # 词表：设备类型没有就加；文档类别必须已有
    terms = api(base, "/api/vocab/device_type", token)
    if not any(t["value"] == a.device_type and t.get("isActive", True) for t in terms):
        try:
            api(base, "/api/vocab/device_type", token, body={"value": a.device_type, "aliases": None})
            print(f"词表「设备类型」加了「{a.device_type}」")
        except ApiError as e:
            raise SystemExit(f"词表「设备类型」里没有「{a.device_type}」，自动添加失败{e}。请在后台词表里加上再跑")
    cats = {t["value"] for t in api(base, "/api/vocab/doc_category", token) if t.get("isActive", True)}
    missing = {x["category"] for x in items} - cats
    if missing:
        raise SystemExit(f"词表「文档类别」里没有：{'、'.join(sorted(missing))}。请在后台词表里加上再跑")

    # 知识库：没有就建
    kbs = {k["name"]: k["id"] for k in api(base, "/api/kbs", token)}
    for name in dict.fromkeys(x["kb"] for x in items):
        if name not in kbs:
            kbs[name] = api(base, "/api/kbs", token, body={"name": name, "tier": "Private", "defaultChunkStrategy": "ByHeading",
                                                           "description": KB_DESC.get(name, "")})["id"]
            print(f"新建知识库「{name}」")
    # 库里已有的：文件名 → (id, 解析状态)。上传了还没提交解析的（上次中途出错）这次补交
    have = {name: {d["fileName"]: (d["id"], d.get("parseStatus")) for d in api(base, f"/api/documents?kbId={kb_id}", token)}
            for name, kb_id in kbs.items() if name in {x["kb"] for x in items}}

    total, done, skipped_n, failed, queued, parse_failed = len(items), 0, 0, [], 0, []
    for name, xs in by_folder.items():
        uploaded = []
        for x in xs:
            done += 1
            tag = f"[{done}/{total}] {x['path'].name}"
            if x["path"].name in have[x["kb"]]:
                skipped_n += 1
                doc_id, status = have[x["kb"]][x["path"].name]
                if status == "NotParsed":
                    uploaded.append(doc_id)
                    print(f"{tag} 库里已有，还没解析，补交解析")
                else:
                    print(f"{tag} 库里已有，跳过")
                continue
            body, ctype = multipart({
                "kbId": kbs[x["kb"]], "classification": a.classification, "customerName": CUSTOMER, "year": str(x["year"]),
                "deviceType": a.device_type, "docCategory": x["category"], "title": x["title"],
                "docVersion": x["version"] or "", "registerProject": "false",
            }, x["path"].name, x["path"].read_bytes())
            try:
                r = api(base, "/api/documents", token, raw=body, ctype=ctype, timeout=1800)
            except (ApiError, urllib.error.URLError, TimeoutError) as e:
                failed.append((x["path"].relative_to(root), str(e)))
                print(f"{tag} 上传失败：{e}")
                continue
            uploaded.append(r["docId"])
            have[x["kb"]][x["path"].name] = (r["docId"], "NotParsed")
            print(f"{tag} 已上传（{x['size'] / 1048576:.1f} MB）→ {x['title']}")
        n_ok = 0
        for i in range(0, len(uploaded), PARSE_BATCH):      # 服务器一次最多收 50 份
            batch = uploaded[i:i + PARSE_BATCH]
            try:
                q = api(base, "/api/documents/parse", token, body={"docIds": batch})
                n_ok += q.get("queued", len(batch)) if isinstance(q, dict) else len(batch)
            except ApiError as e:
                parse_failed.append((name, len(batch), str(e)))
        if uploaded:
            queued += n_ok
            print(f"—— 「{name}」{n_ok} 份已提交解析")

    print(f"\n上传 {done - skipped_n - len(failed)} 份，跳过 {skipped_n} 份（库里已有），失败 {len(failed)} 份；已提交解析 {queued} 份。")
    for name, n, why in parse_failed:
        print(f"  「{name}」有 {n} 份提交解析失败{why}；再跑一次会补交，或在后台「资料管理」里勾选后点「解析」")
    print("解析进度在后台「资料管理」里看。扫描件要逐页识别，大文件可能要几十分钟一份。")
    for p, why in failed:
        print(f"  失败 {p}：{why}")
    if failed or parse_failed:
        print("失败的可以再跑一次（已导入的会跳过）。")
        sys.exit(1)


if __name__ == "__main__":
    main()
