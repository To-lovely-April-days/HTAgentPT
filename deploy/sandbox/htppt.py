"""按模板做 PPT（代码解释器和「做 PPT」工具里用）。

    import htppt
    info = htppt.analyze('in/模板.pptx')            # 每页是什么类型、有几个要点框、能放多少字
    rep = htppt.build('in/模板.pptx', pages, 'out/方案.pptx', credit='Presentation template by SlidesCarnival')

网上的模板大多不用版式占位符，是在页面上自由摆文本框，所以不能只套母版。做法是：
1. analyze：把每页的文本框按字号、位置、内容分成 标题 / 副标题 / 小标题 / 正文 / 数字 / 序号，判断页面类型
   （cover 封面、section 过渡页、toc 目录、list 要点、items 几栏小标题加说明、num 大数字、table 表格、img 图文、end 结尾）；
2. build：每一页按类型从模板里挑最合适的样页复制过来，把字换成我们的，没用上的模板原文删掉，
   中文配东亚字体（Microsoft YaHei，服务器上转图时退到思源黑体），字放不下先缩字号、还不行报出来；
3. 模板原来的页全部删掉，最后一页加一行署名（CC BY 4.0 要求）；用了模板自带照片的，附上模板的致谢页。

pages 的写法（每页一个 dict，type 必填）：
    {"type": "cover", "title": "...", "subtitle": "..."}
    {"type": "section", "title": "...", "subtitle": "..."}
    {"type": "toc", "title": "目录", "items": ["客户需求", "方案要点", ...]}
    {"type": "list", "title": "...", "bullets": ["...", "..."]}
    {"type": "items", "title": "...", "items": [{"title": "...", "text": "..."}, ...]}      # 2–6 栏
    {"type": "num", "title": "...", "numbers": [{"value": "10 MPa", "label": "设计压力"}, ...]}
    {"type": "table", "title": "...", "rows": [["项目", "方案 A", "方案 B"], ["材质", "316L", "C276"]]}
    {"type": "img", "title": "...", "bullets": ["..."], "image": "in/现场照片.jpg"}          # image 可不给，用模板的图
    {"type": "end", "title": "谢谢", "subtitle": "上海霍桐实验仪器有限公司"}
"""
import copy
import io
import math
import os
import re

from lxml import etree
from pptx import Presentation
from pptx.oxml.ns import qn
from pptx.util import Emu, Pt

EA_FONT = "Microsoft YaHei"
TYPES = ("cover", "section", "toc", "list", "items", "num", "table", "img", "end")
# 没有合适样页时退而求其次
FALLBACK = {
    "cover": ["section", "end"], "section": ["cover", "end"], "end": ["section", "cover"],
    "toc": ["items", "list"], "items": ["list", "img"], "num": ["items", "list"],
    "table": ["list", "img", "items"], "img": ["list", "items"], "list": ["img", "items"],
}
NUM_RE = re.compile(r"^[\s$€¥£+\-~≈<>]*[\d][\d.,\s]*(%|[kKmMbB]|x|×|\+|[a-zA-Z/²³°℃]{0,6})?$")
PAGE_RE = re.compile(r"^\s*\d{1,3}\s*/\s*\d{1,3}\s*$")     # 页码「02/20」
IDX_RE = re.compile(r"^(0?\d|[IVX]+)[.)]?$")
SKIP_WORDS = ("slidescarnival", "instructions for use", "edit in google slides", "edit in powerpoint", "free templates",
              "this presentation template", "credits", "special thanks")
END_WORDS = ("thank", "thanks", "questions", "谢谢", "致谢", "the end", "bye", "contact us")
TOC_WORDS = ("contents", "agenda", "overview", "table of contents", "目录", "outline", "index")


# ───────────────────────── 读模板 ─────────────────────────

def _cjk(s):
    return sum(1 for ch in s if "⺀" <= ch <= "鿿" or "＀" <= ch <= "￯")


def _size(shape):
    """这个文本框里第一个写明的字号（pt）；都没写返回 None。"""
    for p in shape.text_frame.paragraphs:
        for r in p.runs:
            if r.font.size:
                return r.font.size.pt
        rpr = p._p.find(qn("a:pPr"))
        if rpr is not None:
            d = rpr.find(qn("a:defRPr"))
            if d is not None and d.get("sz"):
                return int(d.get("sz")) / 100
    end = shape.text_frame._txBody.find(".//" + qn("a:endParaRPr"))
    if end is not None and end.get("sz"):
        return int(end.get("sz")) / 100
    lst = shape.text_frame._txBody.find(qn("a:lstStyle"))
    if lst is not None:
        d = lst.find(".//" + qn("a:defRPr"))
        if d is not None and d.get("sz"):
            return int(d.get("sz")) / 100
    return None


def _inherited_size(shape):
    """占位符没写字号：去版式、母版里同一个占位符找。"""
    try:
        idx = shape.placeholder_format.idx
        slide = shape.part.slide
        lay = slide.slide_layout
        for ph in lay.placeholders:
            if ph.placeholder_format.idx == idx:
                s = _size(ph)
                if s:
                    return s
        kind = _ph_kind(shape)
        tx = lay.slide_master._element.find(qn("p:txStyles"))
        if tx is not None:
            el = tx.find(qn("p:titleStyle") if kind in ("TITLE", "CENTER_TITLE") else qn("p:bodyStyle"))
            d = el.find(".//" + qn("a:defRPr")) if el is not None else None
            if d is not None and d.get("sz"):
                return int(d.get("sz")) / 100
    except Exception:  # noqa: BLE001 - 找不到就算了
        return None
    return None


def _ph_kind(shape):
    if not shape.is_placeholder:
        return None
    t = str(shape.placeholder_format.type)
    for k in ("CENTER_TITLE", "SUBTITLE", "TITLE", "BODY", "SLIDE_NUMBER", "FOOTER", "DATE", "OBJECT"):
        if k in t:
            return k
    return "OTHER"


def _walk(shapes, W, H, xf=None, path=()):
    """展开组合：每个形状给出在整页上的位置（0–1）。xf：组合的坐标换算 (ox, oy, sx, sy)。"""
    xf = xf or (0, 0, 1, 1)
    for i, sh in enumerate(shapes):
        try:
            x, y, w, h = sh.left or 0, sh.top or 0, sh.width or 0, sh.height or 0
        except Exception:  # noqa: BLE001 - 个别形状没有位置
            x = y = w = h = 0
        ax, ay = xf[0] + x * xf[2], xf[1] + y * xf[3]
        aw, ah = w * xf[2], h * xf[3]
        box = (ax / W, ay / H, aw / W, ah / H)
        if sh.shape_type == 6:  # 组合
            g = sh._element.find(qn("p:grpSpPr")).find(qn("a:xfrm"))
            if g is not None and g.find(qn("a:chExt")) is not None:
                off, ext = g.find(qn("a:off")), g.find(qn("a:ext"))
                choff, chext = g.find(qn("a:chOff")), g.find(qn("a:chExt"))
                cw, ch = int(chext.get("cx")) or 1, int(chext.get("cy")) or 1
                sx, sy = int(ext.get("cx")) / cw * xf[2], int(ext.get("cy")) / ch * xf[3]
                ox = xf[0] + int(off.get("x")) * xf[2] - int(choff.get("x")) * sx
                oy = xf[1] + int(off.get("y")) * xf[3] - int(choff.get("y")) * sy
                yield from _walk(sh.shapes, W, H, (ox, oy, sx, sy), path + (i,))
            continue
        yield sh, box, path + (i,)


def _slots(slide, W, H):
    """页面上的文本框，带角色。"""
    out = []
    for sh, box, path in _walk(slide.shapes, W, H):
        if sh.has_text_frame:
            txt = sh.text_frame.text.strip()
            ph = _ph_kind(sh)
            if ph in ("SLIDE_NUMBER", "FOOTER", "DATE") or txt in ("‹#›", "<#>"):
                continue
            if not txt and not ph:
                continue
            size = _size(sh) or (_inherited_size(sh) if ph else None) or 18.0
            out.append({"path": path, "box": box, "text": txt, "size": size, "ph": ph, "shape": sh})
        elif sh.has_table if hasattr(sh, "has_table") else False:
            out.append({"path": path, "box": box, "text": "", "size": 0, "ph": "TABLE", "shape": sh})
    return out


def _pictures(slide, W, H):
    return [(sh, box) for sh, box, _ in _walk(slide.shapes, W, H) if sh.shape_type == 13]


def _reading(s):
    """阅读顺序：先按行（差不到 5% 页高算同一行），行内从左到右。"""
    return (int(s["box"][1] * 20 + 0.5), s["box"][0])


def _classify(slide, idx, total, W, H):
    slots = [s for s in _slots(slide, W, H) if s["ph"] != "TABLE"]
    tables = [s for s in _slots(slide, W, H) if s["ph"] == "TABLE"]
    alltext = " ".join(s["text"] for s in slots).lower()
    pics = _pictures(slide, W, H)
    pic_area = sum(b[2] * b[3] for _, b in pics if b[2] * b[3] > 0.02)
    has_chart = any(getattr(sh, "has_chart", False) and sh.has_chart for sh, _, _ in _walk(slide.shapes, W, H))
    info = {"index": idx, "type": "other", "usable": True, "note": ""}
    # 页眉角上的「SlidesCarnival」这种短标签不算；说明页、致谢页是长句或大标题里有这些词
    big_txt = " ".join(s["text"] for s in slots if len(s["text"]) > 25 or s["size"] >= max(x["size"] for x in slots)).lower() if slots else ""
    if any(w in big_txt for w in SKIP_WORDS):
        info.update(type="credits" if ("credit" in alltext or "special thanks" in alltext) else "other", usable=False, note="说明页 / 致谢页")
        return info, {}
    if has_chart:
        info.update(usable=False, note="有图表（不复用）")
        return info, {}
    if not slots and not tables:
        info.update(usable=False, note="没有文字")
        return info, {}

    top_size = max((x["size"] for x in slots), default=18)
    # 页眉、页脚、角上的小字（「YOUR LOGO」「ABOUT」、日期）：不当内容框，最后当模板原文清掉
    deco = [s for s in slots if s["ph"] not in ("TITLE", "CENTER_TITLE") and (
        s["size"] <= 10 or (s["size"] <= 16 and (s["box"][1] + s["box"][3] < 0.13 or s["box"][1] > 0.9))
        or PAGE_RE.match(s["text"])
        or (len(s["text"]) <= 30 and s["box"][3] <= 0.1 and s["size"] < 0.6 * top_size and (s["box"][1] + s["box"][3] <= 0.16 or s["box"][1] >= 0.85)))]
    slots = [s for s in slots if s not in deco]
    if not slots and not tables:
        info.update(usable=False, note="只有页眉页脚")
        return info, {}
    # 角色：最大字号的是标题；大号的短数字是数字；比正文大一号的短字是小标题；其余是正文
    titles = [s for s in slots if s["ph"] in ("TITLE", "CENTER_TITLE")]
    if titles:
        title = max(titles, key=lambda s: (s["size"], -s["box"][1]))
    else:
        top = max(s["size"] for s in slots)
        ties = sorted([s for s in slots if s["size"] >= top - 1 and not NUM_RE.match(s["text"])] or slots, key=lambda s: (s["box"][1], s["box"][0]))
        title = ties[0]
        # 几个一样大的字排在同一行：是几栏的小标题，这页没有单独的标题，不用
        if any(abs(t["box"][1] - title["box"][1]) < 0.05 for t in ties[1:]) and idx != 0:
            info.update(usable=False, note="没有单独的标题")
            return info, {}
    rest = [s for s in slots if s is not title]
    idxs = [s for s in rest if IDX_RE.match(s["text"])]
    nums = [s for s in rest if s not in idxs and NUM_RE.match(s["text"]) and s["size"] >= 20]
    others = [s for s in rest if s not in idxs and s not in nums]
    big = max((s["size"] for s in others), default=0)
    small = min((s["size"] for s in others), default=0)
    heads = [s for s in others if big > small + 2 and s["size"] >= small + 3 and len(s["text"]) <= 40 and s["text"].count("\n") == 0]
    bodies = [s for s in others if s not in heads]
    roles = {"title": title, "heads": heads, "bodies": bodies, "nums": nums, "idxs": idxs, "tables": tables, "pics": pics, "deco": deco}

    # 小标题配正文（正文在小标题下面、左右对得上）
    items = []
    used = set()
    for h in sorted(heads, key=_reading):
        hx, hy, hw, hh = h["box"]
        cand = [b for b in bodies if id(b) not in used and b["box"][1] >= hy + hh * 0.5 and b["box"][1] - (hy + hh) < 0.22
                and min(hx + hw, b["box"][0] + b["box"][2]) - max(hx, b["box"][0]) > 0.3 * min(hw, b["box"][2])]
        b = min(cand, key=lambda b: b["box"][1], default=None)
        if b is not None:
            used.add(id(b))
        items.append({"head": h, "body": b})
    if items and sum(1 for it in items if it["body"] is not None) * 2 < len(items):
        items = []      # 小标题大多没配上正文：不是几栏的页
        used = set()
    if not items and len(bodies) >= 2:   # 没有小标题、几块并排的正文：一块一栏
        row = sorted(bodies, key=_reading)
        ys = [b["box"][1] for b in row]
        if max(ys) - min(ys) < 0.12 and all(b["box"][2] < 0.5 for b in row):
            items = [{"head": None, "body": b} for b in row]
            used = {id(b) for b in row}
    free_bodies = [b for b in bodies if id(b) not in used]
    roles["items"] = items
    roles["free"] = free_bodies
    body_area = sum(b["box"][2] * b["box"][3] for b in free_bodies)
    t = title["text"].lower()

    if idx == 0:
        typ = "cover"
    elif any(w in t for w in END_WORDS) and idx >= total - 4:
        typ = "end"
    elif tables:
        typ = "table"
    elif any(w in t for w in TOC_WORDS) and (items or idxs or free_bodies):
        typ = "toc"
    elif len(nums) >= 2:
        typ = "num"
    elif len(items) >= 2:
        typ = "items"
    elif pic_area >= 0.18 and free_bodies:
        typ = "img"
    elif any(b["box"][2] >= 0.25 and b["box"][2] * b["box"][3] >= 0.03 for b in free_bodies) and body_area >= 0.05:
        typ = "list"
    elif len(slots) <= 3 and title["size"] >= 28:
        typ = "section"
    else:
        typ = "other"
        info["usable"] = False
        info["note"] = "认不出来"
    info["type"] = typ
    cap_items = len(items) if typ in ("items", "toc") else len(nums) if typ == "num" else 0
    if typ == "toc" and not items:
        cap_items = max(len(free_bodies), len(idxs))
    info["count"] = cap_items
    info["body_ok"] = any(b["box"][2] >= 0.2 and b["box"][2] * b["box"][3] >= 0.025 for b in free_bodies)
    info["title_chars"] = _capacity(title)
    info["title_w"] = round(max(1, (title["shape"].width or 0) / 12700 - 14))
    info["title_size"] = title["size"]
    info["pic_area"] = round(pic_area, 3)
    info["body_chars"] = sum(_capacity(b) for b in free_bodies)
    info["pictures"] = len([p for p in pics if p[1][2] * p[1][3] > 0.02])
    return info, roles


def _capacity(slot, size=None):
    """这个框按现在字号大概能放多少个汉字。"""
    if slot is None:
        return 0
    sh = slot["shape"]
    s = size or slot["size"] or 18
    w_pt = max(1, (sh.width or 0) / 12700 - 14)
    h_pt = max(s * 1.3, (sh.height or 0) / 12700 - 8)
    per_line = max(1, int(w_pt / s))
    lines = max(1, int(h_pt / (s * 1.3)))
    return per_line * lines


def analyze(path):
    """每页的类型和容量（给入库和挑样页用）。"""
    prs = Presentation(path)
    W, H = prs.slide_width, prs.slide_height
    total = len(prs.slides)
    pages = []
    for i, s in enumerate(prs.slides):
        info, _ = _classify(s, i, total, W, H)
        pages.append(info)
    types = sorted({p["type"] for p in pages if p["usable"]})
    return {"slides": total, "width": W, "height": H, "pages": pages, "types": types,
            "credits": [p["index"] for p in pages if p["type"] == "credits"]}


# ───────────────────────── 复制样页 ─────────────────────────

def _dup(prs, src):
    """把 src 页复制一份追加到最后（形状、背景、图片关系都带上）。"""
    dst = prs.slides.add_slide(src.slide_layout)
    for sh in list(dst.shapes):
        sh._element.getparent().remove(sh._element)
    rid_map = {}
    for rid, rel in src.part.rels.items():
        if "notesSlide" in rel.reltype or "slideLayout" in rel.reltype or rel.reltype.endswith("/slide"):
            continue
        if rel.is_external:
            rid_map[rid] = dst.part.relate_to(rel.target_ref, rel.reltype, is_external=True)
        else:
            rid_map[rid] = dst.part.relate_to(rel.target_part, rel.reltype)
    tree = dst.shapes._spTree
    for el in src.shapes._spTree.iterchildren():
        if el.tag in (qn("p:nvGrpSpPr"), qn("p:grpSpPr")):
            continue
        tree.append(copy.deepcopy(el))
    bg = src._element.cSld.find(qn("p:bg"))
    if bg is not None:
        dst._element.cSld.insert(0, copy.deepcopy(bg))
    # 改 r:embed / r:link / r:id 指向新页自己的关系；指向别的页的链接去掉（那些页最后都删了）
    for el in list(dst._element.iter()):
        for attr in (qn("r:embed"), qn("r:link"), qn("r:id")):
            v = el.get(attr)
            if v and v in rid_map:
                el.set(attr, rid_map[v])
            elif v and el.tag in (qn("a:hlinkClick"), qn("a:hlinkHover"), qn("a:hlinkMouseOver")):
                el.getparent().remove(el)
                break
    return dst


def _drop(prs, slide):
    lst = prs.slides._sldIdLst
    for sid in list(lst):
        if prs.part.related_part(sid.rId) is slide.part:
            prs.part.drop_rel(sid.rId)
            lst.remove(sid)
            return


# ───────────────────────── 换字 ─────────────────────────

_AFTER_EA = ("a:cs", "a:sym", "a:hlinkClick", "a:hlinkMouseOver", "a:rtl", "a:extLst")


def _ea(rp):
    """给字的格式加上东亚字体（a:ea 在 a:latin 之后、a:cs 之前，顺序错了 PowerPoint 会报文件损坏）。"""
    ea = rp.find(qn("a:ea"))
    if ea is None:
        ea = etree.Element(qn("a:ea"))
        nxt = next((rp.find(qn(t)) for t in _AFTER_EA if rp.find(qn(t)) is not None), None)
        if nxt is not None:
            nxt.addprevious(ea)
        else:
            rp.append(ea)
    ea.set("typeface", EA_FONT)


def _set_text(shape, lines, size_cap=None):
    """换成 lines（每行一段），沿用第一段的段落格式和第一个字的格式；中文配东亚字体。"""
    tf = shape.text_frame
    body = tf._txBody
    ps = body.findall(qn("a:p"))
    p0 = ps[0] if ps else etree.SubElement(body, qn("a:p"))
    ppr = p0.find(qn("a:pPr"))
    r0 = p0.find(qn("a:r"))
    rpr = r0.find(qn("a:rPr")) if r0 is not None else p0.find(qn("a:endParaRPr"))
    for p in ps:
        body.remove(p)
    for line in lines or [""]:
        p = etree.SubElement(body, qn("a:p"))
        if ppr is not None:
            p.append(copy.deepcopy(ppr))
        r = etree.SubElement(p, qn("a:r"))
        rp = copy.deepcopy(rpr) if rpr is not None else etree.Element(qn("a:rPr"))
        rp.tag = qn("a:rPr")
        for k in ("dirty", "err"):
            rp.attrib.pop(k, None)
        if _cjk(line):
            rp.set("lang", "zh-CN")
            rp.attrib.pop("cap", None)       # 全大写对中文没意义，还会让 PowerPoint 换字体
            _ea(rp)
        if size_cap:
            rp.set("sz", str(int(size_cap * 100)))
        r.append(rp)
        t = etree.SubElement(r, qn("a:t"))
        t.text = str(line)


def _need(lines, slot, size):
    """按字号 size 放 lines 要几行、框里能放几行。"""
    sh = slot["shape"]
    w_pt = max(1, (sh.width or 0) / 12700 - 14)
    need = 0
    for ln in lines:
        width = sum(size if ("⺀" <= ch <= "鿿" or "＀" <= ch <= "￯") else size * 0.55 for ch in str(ln))
        need += max(1, math.ceil(width / w_pt))
    room = max(1, int(slot["room"] / 12700 / (size * 1.25)))
    return need, room


def _fit(slot, lines, report, where):
    """放不下先缩字号（最小到原来的 60%、不小于 10 号），还不行记下来。"""
    s0 = slot["size"] or 18
    s = s0
    while True:
        need, room = _need(lines, slot, s)
        if need <= room or s <= max(10, s0 * 0.6):
            break
        s = round(s - max(1, s0 * 0.08), 1)
    if need > room:
        report["overflow"].append(f"{where}：字太多，缩到 {s:g} 号还放不下（大约要 {need} 行、只有 {room} 行），拆成两页或删减")
    elif s < s0:
        report["shrunk"].append(f"{where}：字偏多，从 {s0:g} 号缩到 {s:g} 号")
    _set_text(slot["shape"], lines, s if s < s0 else None)


def _room(slots, slot, H):
    """这个框往下能用到多高：框本身，或者到下面最近一个框 / 页底。"""
    x, y, w, h = slot["box"]
    below = [s["box"][1] for s in slots if s is not slot and s["box"][1] > y + h * 0.5
             and min(x + w, s["box"][0] + s["box"][2]) - max(x, s["box"][0]) > 0]
    limit = min(below + [0.94])
    return max(h, limit - y - 0.01) * H


def _clear(slot):
    """没用上的模板原文：空文本框删掉；有底色的（卡片、按钮）只清字。"""
    sh = slot["shape"]
    sp = sh._element
    fill = sp.find(qn("p:spPr"))
    has_fill = fill is not None and (fill.find(qn("a:solidFill")) is not None or fill.find(qn("a:gradFill")) is not None
                                     or fill.find(qn("a:blipFill")) is not None)
    has_line = fill is not None and fill.find(qn("a:ln")) is not None and fill.find(qn("a:ln")).find(qn("a:noFill")) is None
    if has_fill or has_line:
        _set_text(sh, [""])
    else:
        sp.getparent().remove(sp)


def _replace_picture(slide, pic, image_path):
    from PIL import Image
    im = Image.open(image_path).convert("RGB")
    w, h = pic.width, pic.height
    r_box, r_img = w / h, im.width / im.height
    if r_img > r_box:   # 图更宽：裁左右
        nw = int(im.height * r_box)
        im = im.crop(((im.width - nw) // 2, 0, (im.width - nw) // 2 + nw, im.height))
    else:
        nh = int(im.width / r_box)
        im = im.crop((0, (im.height - nh) // 2, im.width, (im.height - nh) // 2 + nh))
    im.thumbnail((1600, 1600))
    b = io.BytesIO()
    im.save(b, "JPEG", quality=85)
    b.seek(0)
    new = slide.shapes.add_picture(b, pic.left, pic.top, w, h)
    pic._element.addnext(new._element)          # 放在原图的层次上
    pic._element.getparent().remove(pic._element)


def _add_table(slide, rows, box, W, H, size=12):
    x, y, w, h = box
    n, m = len(rows), max(len(r) for r in rows)
    gf = slide.shapes.add_table(n, m, Emu(int(x * W)), Emu(int(y * H)), Emu(int(w * W)), Emu(int(min(h, 0.06 * n) * H)))
    _fill_table(gf.table, rows, size)


def _fill_table(table, rows, size=None):
    for i, row in enumerate(rows):
        for j in range(len(table.columns)):
            if i < len(table.rows):
                cell = table.cell(i, j)
                txt = str(row[j]) if j < len(row) else ""
                tf = cell.text_frame
                if tf.paragraphs and tf.paragraphs[0].runs:
                    _set_text(cell, [txt])   # 沿用模板单元格的格式
                else:
                    tf.text = txt
                    for p in tf.paragraphs:
                        for r in p.runs:
                            if size:
                                r.font.size = Pt(size)
                            if _cjk(txt):
                                _ea(r._r.get_or_add_rPr())


def _resize_table(gf, nrows, ncols):
    tbl = gf.table._tbl
    trs = tbl.findall(qn("a:tr"))
    while len(trs) < nrows:
        tbl.append(copy.deepcopy(trs[-1]))
        trs = tbl.findall(qn("a:tr"))
    for tr in trs[nrows:]:
        tbl.remove(tr)
    grid = tbl.find(qn("a:tblGrid"))
    cols = grid.findall(qn("a:gridCol"))
    total = sum(int(c.get("w")) for c in cols)
    while len(cols) < ncols:
        grid.append(copy.deepcopy(cols[-1]))
        for tr in tbl.findall(qn("a:tr")):
            tr.append(copy.deepcopy(tr.findall(qn("a:tc"))[-1]))
        cols = grid.findall(qn("a:gridCol"))
    for c in cols[ncols:]:
        grid.remove(c)
    for tr in tbl.findall(qn("a:tr")):
        for tc in tr.findall(qn("a:tc"))[ncols:]:
            tr.remove(tc)
    cols = grid.findall(qn("a:gridCol"))
    for c in cols:
        c.set("w", str(total // len(cols)))


# ───────────────────────── 做 ─────────────────────────

def _lines_of(page):
    if page.get("bullets"):
        return [str(b) for b in page["bullets"]]
    if page.get("items"):
        return [(f"{it.get('title', '')}：{it.get('text', '')}" if isinstance(it, dict) and it.get("text") else
                 (it.get("title", "") if isinstance(it, dict) else str(it))) for it in page["items"]]
    if page.get("numbers"):
        return [f"{n.get('value', '')}  {n.get('label', '')}" for n in page["numbers"]]
    if page.get("rows"):
        return [" · ".join(str(c) for c in r) for r in page["rows"]]
    if page.get("text"):
        return [str(page["text"])]
    return []


def _want_count(page):
    t = page.get("type")
    if t in ("items", "toc"):
        return len(page.get("items") or [])
    if t == "num":
        return len(page.get("numbers") or [])
    return 0


def build(template, pages, out, credit=None, keep_credits=True):
    """按 pages 做一份 PPT，存到 out。返回报告：每页用了模板第几页、缩了字号的、放不下的、删掉的模板原文。"""
    prs = Presentation(template)
    W, H = prs.slide_width, prs.slide_height
    originals = list(prs.slides)
    total = len(originals)
    infos = []
    for i, s in enumerate(originals):
        info, roles = _classify(s, i, total, W, H)
        infos.append(info)
    usable = [i for i in infos if i["usable"]]
    report = {"pages": [], "shrunk": [], "overflow": [], "cleared": 0, "fallback": [], "photos": False, "slides": 0}
    used_count = {}
    last_src = None
    pics_kept = False

    for n, page in enumerate(pages, 1):
        typ = page.get("type") if page.get("type") in TYPES else "list"
        if typ == "list" and page.get("rows"):
            typ = "table"
        want = _want_count(page)
        chain = [typ] + FALLBACK.get(typ, [])
        pick, picked_type = None, None
        ttl = page.get("title") or ""
        needs_body = typ in ("list", "img") or (typ == "table" and not any(i["type"] == "table" for i in usable))

        def fits(c):
            # 标题按原字号的 75% 能在两行内放下
            if not ttl or not c.get("title_w"):
                return True
            size = (c.get("title_size") or 18) * 0.75
            width = sum(size if _cjk(ch) else size * 0.55 for ch in ttl)
            return math.ceil(width / c["title_w"]) <= 2

        def count_fit(c, t):
            if t not in ("items", "toc", "num") or not want:
                return 0
            k = c.get("count", 0)
            return 0 if k == want else 1 if k > want else 3 if k == 0 else 2

        ranked = []
        for k, t in enumerate(chain):
            for c in (i for i in usable if i["type"] == t):
                body_bad = needs_body and t in ("list", "img", "section", "items", "toc") and not c.get("body_ok") and typ != "table"
                tbl_bad = typ == "table" and t != "table" and c.get("pic_area", 0) > 0.15
                # 标题放得下、有放正文的地方、栏数对得上的先用；同一页别连着用，用得少的先用
                short = t in ("items", "toc", "num") and want and 0 < c.get("count", 0) < want
                ranked.append(((not fits(c)) + body_bad * 2 + tbl_bad + short, k, count_fit(c, t), c["index"] == last_src,
                               used_count.get(c["index"], 0), c["index"], t, c))
        if typ == "cover":     # 封面就用模板的封面，标题长就缩字号
            ranked = [r for r in ranked if r[6] == "cover"] or ranked
        if ranked:
            ranked.sort(key=lambda r: r[:6])
            pick, picked_type = ranked[0][7], ranked[0][6]
        if pick is None:
            pick, picked_type = usable[0] if usable else infos[0], "list"
        if picked_type != typ:
            report["fallback"].append(f"第 {n} 页：模板里没有「{typ}」这种页，用了「{picked_type}」页")
        used_count[pick["index"]] = used_count.get(pick["index"], 0) + 1
        last_src = pick["index"]
        slide = _dup(prs, originals[pick["index"]])
        _, roles = _classify(slide, pick["index"], total, W, H)
        slots_all = [roles["title"]] + roles.get("heads", []) + roles.get("bodies", []) + roles.get("nums", []) + roles.get("idxs", []) if roles else []
        for s in slots_all:
            s["room"] = _room(slots_all, s, H)
        filled = set()      # 用上了的文本框（元素）

        def put(slot, lines, where):
            if slot is None:
                return
            _fit(slot, [str(x) for x in lines], report, f"第 {n} 页{where}")
            filled.add(slot["shape"]._element)

        title = page.get("title") or ("谢谢" if typ == "end" else "")
        put(roles.get("title"), [title], "标题")
        sub = page.get("subtitle")
        items_in = page.get("items") or []
        if picked_type in ("cover", "section", "end"):
            # 封面、过渡页：副标题放最大的那块正文
            others = sorted(roles.get("bodies", []) + [h for h in roles.get("heads", [])], key=lambda s: -s["box"][2] * s["box"][3])
            if sub and others:
                put(others[0], [sub], "副标题")
            elif typ not in ("cover", "section", "end") and others:
                put(others[0], _lines_of(page), "正文")
        elif picked_type in ("items", "toc") and not roles.get("items"):
            free = sorted(roles.get("free", []), key=lambda s: -s["box"][2] * s["box"][3])
            if free:
                put(free[0], _lines_of(page), "正文")
        elif picked_type in ("items", "toc"):
            slots = roles.get("items", [])
            src_items = items_in if typ in ("items", "toc") else [{"title": x} for x in _lines_of(page)]
            if typ == "num":
                src_items = [{"title": x.get("value", ""), "text": x.get("label", "")} for x in page.get("numbers") or []]
            for k, it in enumerate(src_items[: len(slots)]):
                head, body = slots[k]["head"], slots[k]["body"]
                it = it if isinstance(it, dict) else {"title": str(it)}
                if head is not None:
                    put(head, [it.get("title", "")], f"第 {k + 1} 栏小标题")
                    if body is not None and it.get("text"):
                        put(body, [it["text"]], f"第 {k + 1} 栏说明")
                elif body is not None:
                    put(body, [it.get("title", "")] + ([it["text"]] if it.get("text") else []), f"第 {k + 1} 栏")
            if len(src_items) > len(slots):
                report["overflow"].append(f"第 {n} 页：给了 {len(src_items)} 栏，这页只有 {len(slots)} 栏，多出来的没放上，拆页或换模板")
            # 序号：按顺序编号，多的清掉
            for k, s in enumerate(sorted(roles.get("idxs", []), key=_reading)):
                if k < min(len(src_items), len(slots)):
                    put(s, [f"{k + 1:02d}"], "序号")
            if roles.get("free") and sub:
                put(roles["free"][0], [sub], "说明")
        elif picked_type == "num":
            nums = sorted(roles.get("nums", []), key=_reading)
            labels = roles.get("bodies", []) + roles.get("heads", [])
            src = page.get("numbers") or [{"value": (x.get("title") if isinstance(x, dict) else str(x)), "label": (x.get("text", "") if isinstance(x, dict) else "")} for x in items_in]
            for k, num in enumerate(nums[: len(src)]):
                put(num, [src[k].get("value", "")], f"第 {k + 1} 个数字")
                nx, ny, nw, nh = num["box"]
                near = [b for b in labels if b["shape"]._element not in filled and abs((b["box"][0] + b["box"][2] / 2) - (nx + nw / 2)) < 0.15
                        and -0.05 < b["box"][1] - ny < 0.3]
                lab = min(near, key=lambda b: abs(b["box"][1] - ny), default=None)
                if lab is not None and src[k].get("label"):
                    put(lab, [src[k]["label"]], f"第 {k + 1} 个数字的说明")
            if len(src) > len(nums):
                report["overflow"].append(f"第 {n} 页：给了 {len(src)} 个数字，这页只有 {len(nums)} 个位置")
        elif picked_type == "table":
            rows = page.get("rows") or [[x] for x in _lines_of(page)]
            gfs = roles.get("tables", [])
            if gfs:
                gf = gfs[0]["shape"]
                _resize_table(gf, len(rows), max(len(r) for r in rows))
                _fill_table(gf.table, rows)
            free = roles.get("free", [])
            if free and sub:
                put(free[0], [sub], "说明")
        else:   # list / img
            free = sorted(roles.get("free", []) + [b for it in roles.get("items", []) for b in (it["head"], it["body"]) if b is not None],
                          key=lambda s: -s["box"][2] * s["box"][3])
            if typ == "table" and page.get("rows"):
                # 要表格却没有表格页：表放在标题下面，占满页宽，别的正文清掉
                tx, ty, tw, th = roles["title"]["box"]
                left = min(max(tx, 0.05), 0.4)
                top = min(ty + th + 0.04, 0.6)
                tb = (left, top, max(0.5, 0.95 - left), 0.88 - top)
                _add_table(slide, page["rows"], tb, W, H)
                for pic, (px, py, pw, ph) in _pictures(slide, W, H):
                    ix = max(0, min(px + pw, tb[0] + tb[2]) - max(px, tb[0]))
                    iy = max(0, min(py + ph, tb[1] + tb[3]) - max(py, tb[1]))
                    if pw * ph > 0 and ix * iy > 0.2 * pw * ph:
                        pic._element.getparent().remove(pic._element)
            elif free and free[0]["box"][2] >= 0.15:
                put(free[0], _lines_of(page) or ([sub] if sub else []), "正文")
            elif _lines_of(page):
                report["overflow"].append(f"第 {n} 页：这页没有放正文的地方，正文没放上，换个模板或换页面类型")
            if page.get("image") and os.path.exists(page["image"]):
                pics = sorted(roles.get("pics", []), key=lambda p: -p[1][2] * p[1][3])
                if pics:
                    _replace_picture(slide, pics[0][0], page["image"])
                else:
                    report["fallback"].append(f"第 {n} 页：这页没有放图的位置，图没放上")

        # 没用上的模板原文（包括组合里的）：删掉或清空
        for s in _slots(slide, W, H):
            if s["ph"] == "TABLE" or s["shape"]._element in filled:
                continue
            if s["text"]:
                _clear(s)
                report["cleared"] += 1
        if any(b[2] * b[3] > 0.02 for _, b in _pictures(slide, W, H)) and not page.get("image"):
            pics_kept = True
        report["pages"].append({"n": n, "type": typ, "used": picked_type, "from": pick["index"] + 1})

    # 署名：最后一页右下角一行小字
    if credit and len(prs.slides) > total:
        last = prs.slides[len(prs.slides) - 1]
        tb = last.shapes.add_textbox(Emu(int(W * 0.55)), Emu(int(H * 0.94)), Emu(int(W * 0.43)), Emu(int(H * 0.045)))
        tb.text_frame.text = credit
        p = tb.text_frame.paragraphs[0]
        from pptx.enum.text import PP_ALIGN
        p.alignment = PP_ALIGN.RIGHT
        p.runs[0].font.size = Pt(8 if W < 12_000_000 else 12)
        from pptx.dml.color import RGBColor
        p.runs[0].font.color.rgb = RGBColor(0x99, 0x99, 0x99)
    # 用了模板自带的照片：附上模板的致谢页（照片有各自的许可）
    if keep_credits and pics_kept:
        cred = [i for i in infos if i["type"] == "credits"]
        if cred:
            _dup(prs, originals[cred[-1]["index"]])
            report["photos"] = True
    for s in originals:
        _drop(prs, s)
    # 版式和母版上的示例字（「YOUR LOGO」、导航词、日期）：不是占位符的文本框清空
    for master in prs.slide_masters:
        for holder in [master] + list(master.slide_layouts):
            for sp in holder._element.iter(qn("p:sp")):
                if sp.find(".//" + qn("p:ph")) is not None:
                    continue
                tb = sp.find(qn("p:txBody"))
                if tb is not None and "".join(tb.itertext()).strip():
                    for t in tb.iter(qn("a:t")):
                        t.text = ""
                    report["cleared"] += 1
    report["slides"] = len(prs.slides)
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    prs.save(out)
    return report
