"""助手做文件用的小工具（装在沙箱里，代码里 `from htfiles import ...` 就能用）。

Word 修订和批注：python-docx 不会写修订（Word 里的「修订」：删除线 + 下划线，客户可以逐条接受），这里补上。
    from docx import Document
    from htfiles import track_replace, track_delete, track_insert, comment, revisions
    doc = Document("in/技术协议.docx")
    track_replace(doc, "12 MPa", "10 MPa", comment="12 MPa 超出标准釜范围，按 10 MPa 设计")
    comment(doc, "316L 不锈钢", "介质含氯离子时有点蚀风险，请确认 Cl⁻ 浓度")
    doc.save("out/技术协议_霍桐修订.docx")
    print(revisions("out/技术协议_霍桐修订.docx"))

PDF 水印：watermark("in/资料.pdf", "out/资料_水印.pdf", "霍桐仪器 仅供某研究院使用")
"""
import copy
import datetime
import re
import zipfile

from docx.oxml.ns import qn
from docx.text.paragraph import Paragraph
from docx.text.run import Run

AUTHOR = "霍桐仪器"
_ids = {}


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _next_id(doc):
    """修订编号要在整份文件里唯一：从文件里已有的最大编号往后数。"""
    key = id(doc)
    if key not in _ids:
        used = [int(v) for v in doc.element.body.xpath("//@w:id") if str(v).lstrip("-").isdigit()]
        _ids[key] = max(used + [0]) + 1000
    _ids[key] += 1
    return str(_ids[key])


def iter_paragraphs(doc):
    """正文和表格里（含嵌套表格）的所有段落。"""
    def walk(container):
        for p in container.paragraphs:
            yield p
        for t in container.tables:
            for row in t.rows:
                for cell in row.cells:
                    yield from walk(cell)
    yield from walk(doc)


def _runs_text(p):
    return "".join(r.text for r in p.runs)


def _split(run, k):
    """把一个 run 在第 k 个字处切成两个（格式照抄），返回后一半。"""
    text = run.text
    tail = copy.deepcopy(run._r)
    run._r.addnext(tail)
    run.text = text[:k]
    t = Run(tail, run._parent)
    t.text = text[k:]
    return t


def _isolate(p, start, end):
    """让 [start, end) 这段字正好由几个完整的 run 组成，返回这些 run。"""
    pos = 0
    for r in list(p.runs):
        n = len(r.text)
        if pos < start < pos + n:
            _split(r, start - pos)
            return _isolate(p, start, end)
        if pos < end < pos + n:
            _split(r, end - pos)
            return _isolate(p, start, end)
        pos += n
    out, pos = [], 0
    for r in p.runs:
        n = len(r.text)
        if pos >= start and pos + n <= end and n > 0:
            out.append(r)
        pos += n
    return out


def _find(doc, text, nth=1):
    if not text:
        raise ValueError("要找的文字是空的")
    seen = 0
    for p in iter_paragraphs(doc):
        s = _runs_text(p)
        i = s.find(text)
        while i >= 0:
            seen += 1
            if seen == nth:
                return p, i
            i = s.find(text, i + 1)
    sample = [(_runs_text(p)[:40]) for p in iter_paragraphs(doc) if text[:4] and text[:4] in _runs_text(p)][:3]
    hint = f"；含「{text[:4]}」的段落：{sample}" if sample else "；先 print 几段原文核对写法（空格、全角半角、换行）"
    raise ValueError(f"文件里没找到「{text}」{hint}")


def _w(tag, **attrs):
    from docx.oxml import OxmlElement
    el = OxmlElement(tag)
    for k, v in attrs.items():
        el.set(qn(k), v)
    return el


def _del_wrap(doc, runs, author):
    d = _w("w:del", **{"w:id": _next_id(doc), "w:author": author, "w:date": _now()})
    runs[0]._r.addprevious(d)
    for r in runs:
        for t in r._r.findall(qn("w:t")):
            t.tag = qn("w:delText")
            t.set("{http://www.w3.org/XML/1998/namespace}space", "preserve")
        d.append(r._r)
    return d


def _ins_el(doc, like_run, text, author):
    ins = _w("w:ins", **{"w:id": _next_id(doc), "w:author": author, "w:date": _now()})
    r = _w("w:r")
    if like_run is not None:
        rpr = like_run._r.find(qn("w:rPr"))
        if rpr is not None:
            r.append(copy.deepcopy(rpr))
    t = _w("w:t")
    t.set("{http://www.w3.org/XML/1998/namespace}space", "preserve")
    t.text = text
    r.append(t)
    ins.append(r)
    return ins, r


def comment(doc, anchor, text, author=AUTHOR, nth=1):
    """在原文里的一段字上加批注（Word 右边的批注框）。"""
    p, i = _find(doc, anchor, nth)
    runs = _isolate(p, i, i + len(anchor))
    initials = author[:2]
    return doc.add_comment(runs, text=text, author=author, initials=initials)


def track_replace(doc, old, new, comment=None, author=AUTHOR, nth=1):
    """把原文里的 old 改成 new，用修订标出来（old 划掉，new 加下划线）。comment 给了就在新字上加批注。"""
    p, i = _find(doc, old, nth)
    runs = _isolate(p, i, i + len(old))
    d = _del_wrap(doc, runs, author)
    ins, r = _ins_el(doc, runs[0], new, author)
    d.addnext(ins)
    if comment:
        doc.add_comment([Run(r, p)], text=comment, author=author, initials=author[:2])
    return 1


def track_delete(doc, old, comment=None, author=AUTHOR, nth=1):
    """删掉原文里的一段字，用修订标出来。"""
    p, i = _find(doc, old, nth)
    runs = _isolate(p, i, i + len(old))
    if comment:
        doc.add_comment(runs, text=comment, author=author, initials=author[:2])
    _del_wrap(doc, runs, author)
    return 1


def track_insert(doc, after, text, comment=None, author=AUTHOR, nth=1):
    """在原文 after 这段字后面插入 text，用修订标出来。"""
    p, i = _find(doc, after, nth)
    runs = _isolate(p, i, i + len(after))
    ins, r = _ins_el(doc, runs[-1], text, author)
    runs[-1]._r.addnext(ins)
    if comment:
        doc.add_comment([Run(r, p)], text=comment, author=author, initials=author[:2])
    return 1


def track_add_paragraph(doc, after, text, author=AUTHOR, nth=1):
    """在含 after 的那一段后面加一整段新内容（修订标出来）。"""
    p, _ = _find(doc, after, nth)
    new_p = _w("w:p")
    ppr = p._p.find(qn("w:pPr"))
    if ppr is not None:
        new_p.append(copy.deepcopy(ppr))
    p._p.addnext(new_p)
    np_ = Paragraph(new_p, p._parent)
    ins, _ = _ins_el(doc, p.runs[0] if p.runs else None, text, author)
    new_p.append(ins)
    return np_


def revisions(path):
    """数一下文件里有几处修订、几条批注（交付前打印出来核对）。"""
    with zipfile.ZipFile(path) as z:
        xml = z.read("word/document.xml").decode("utf-8")
        comments = z.read("word/comments.xml").decode("utf-8") if "word/comments.xml" in z.namelist() else ""
    return {"插入": len(re.findall(r"<w:ins\b", xml)), "删除": len(re.findall(r"<w:del\b", xml)),
            "批注": len(re.findall(r"<w:comment\b", comments))}


def watermark(src, dst, text, size=28, opacity=0.12):
    """给 PDF 每一页加斜着的文字水印（中文可用）。"""
    import io
    from pypdf import PdfReader, PdfWriter
    from reportlab.pdfbase import pdfmetrics
    from reportlab.pdfbase.cidfonts import UnicodeCIDFont
    from reportlab.pdfgen import canvas
    pdfmetrics.registerFont(UnicodeCIDFont("STSong-Light"))
    reader = PdfReader(src)
    writer = PdfWriter()
    for page in reader.pages:
        w, h = float(page.mediabox.width), float(page.mediabox.height)
        buf = io.BytesIO()
        c = canvas.Canvas(buf, pagesize=(w, h))
        c.setFont("STSong-Light", size)
        c.setFillAlpha(opacity)
        c.translate(w / 2, h / 2)
        c.rotate(35)
        for dy in range(-int(h), int(h), 180):
            c.drawCentredString(0, dy, text)
        c.save()
        buf.seek(0)
        page.merge_page(PdfReader(buf).pages[0])
        writer.add_page(page)
    with open(dst, "wb") as f:
        writer.write(f)
    return len(reader.pages)
