"""设备外形图和三维模型（代码解释器里用）。

    import htcad
    r = htcad.vessel('30L加氢釜', D=300, L=420, jacket=(30, 60, 420),
                     nozzles=[('N1', 0, 0, 60, '搅拌轴'), ('N2', 45, 90, 22, '进气口'), ('N6', None, 0, 32, '底部放料')])
    print(r)   # {'step': 'out/30L加氢釜三维模型.step', 'dxf': 'out/30L加氢釜外形图.dxf', 'outer': (Ø, 高)}

立式容器：筒体 + 上下 2:1 椭圆封头，可选夹套，顶部接管（方位角 0–360°、离中心距离），方位角写 None 的接管画在底部中心，支腿 3 条。
单位 mm。三维模型用 cadquery 建、导出 STEP；二维外形图（主视图 + 管口方位图 + 管口表 + 主要尺寸）用 ezdxf 画、导出 DXF。
这是外形示意，不做强度计算；壁厚、法兰、支座按设计另算。
"""
import math
import os

import cadquery as cq
import ezdxf


def vessel(name, D, L, t=6, jacket=None, nozzles=(), legs=3, leg_h=180, noz_h=70, out='out'):
    os.makedirs(out, exist_ok=True)
    R = D / 2 + t
    hh = R / 2                                   # 2:1 椭圆封头深度
    jz = (jacket[1], jacket[2]) if jacket else None
    J = jacket[0] if jacket else 0
    top_n = [n for n in nozzles if n[1] is not None]
    bot_n = [n for n in nozzles if n[1] is None]

    # ── 三维模型 ──
    lo = [(R * math.sin(math.pi / 2 * i / 12), -hh * math.cos(math.pi / 2 * i / 12)) for i in range(1, 13)]
    up = [(R * math.cos(math.pi / 2 * i / 12), L + hh * math.sin(math.pi / 2 * i / 12)) for i in range(1, 13)]
    m = (cq.Workplane('XZ').moveTo(0, -hh).spline(lo, includeCurrent=True).lineTo(R, L)
         .spline(up, includeCurrent=True).close().revolve(360, (0, 0, 0), (0, 1, 0)))
    if jacket:
        m = m.union(cq.Workplane('XY').workplane(offset=jz[0]).circle(R + J).circle(R).extrude(jz[1] - jz[0]))
    for tag, ang, r, od, *_ in top_n:
        a = math.radians(ang)
        m = m.union(cq.Workplane('XY').workplane(offset=L).center(r * math.cos(a), r * math.sin(a)).circle(od / 2).extrude(hh + noz_h))
    for tag, ang, r, od, *_ in bot_n:
        m = m.union(cq.Workplane('XY').workplane(offset=-hh - noz_h).circle(od / 2).extrude(noz_h + hh * 0.5))
    lr = R + J + 15 if jacket else R + 15
    z_leg_top = jz[0] if jacket else L * 0.3
    for i in range(legs):
        a = 2 * math.pi * i / legs
        m = m.union(cq.Workplane('XY').workplane(offset=-hh - leg_h).center(lr * math.cos(a), lr * math.sin(a))
                    .rect(30, 30).extrude(leg_h + hh + z_leg_top))
    step = os.path.join(out, f'{name}三维模型.step')
    cq.exporters.export(m, step)
    bb = m.val().BoundingBox()

    # ── 二维外形图 ──
    doc = ezdxf.new(setup=True)
    doc.units = ezdxf.units.MM
    msp = doc.modelspace()
    doc.styles.new('CN', dxfattribs={'font': 'simhei.ttf'})   # 中文字：CAD 里用黑体，不然显示成问号
    C = ezdxf.enums.TextEntityAlignment

    def txt(s, x, y, h=14, al=C.BOTTOM_CENTER):
        msp.add_text(s, height=h, dxfattribs={'style': 'CN'}).set_placement((x, y), align=al)

    def ell(cy, upper):
        msp.add_ellipse((0, cy), major_axis=(R, 0), ratio=0.5,
                        start_param=0 if upper else math.pi, end_param=math.pi if upper else 2 * math.pi)

    msp.add_line((-R, 0), (-R, L)); msp.add_line((R, 0), (R, L)); ell(L, True); ell(0, False)
    if jacket:
        for x in (-R - J, R + J):
            msp.add_line((x, jz[0]), (x, jz[1]))
        for y in jz:
            msp.add_line((-R - J, y), (-R, y)); msp.add_line((R, y), (R + J, y))
    top = L + hh + noz_h
    bot = -hh - leg_h
    seen = {}
    for tag, ang, r, od, *_ in top_n:
        x = round(r * math.cos(math.radians(ang)))
        msp.add_lwpolyline([(x - od / 2, L + hh * 0.6), (x - od / 2, top), (x + od / 2, top), (x + od / 2, L + hh * 0.6)])
        seen[x] = seen.get(x, []) + [tag]
    for x, tags in seen.items():
        txt('/'.join(tags), x, top + 10)
    for tag, ang, r, od, *_ in bot_n:
        msp.add_lwpolyline([(-od / 2, -hh * 0.6), (-od / 2, -hh - noz_h), (od / 2, -hh - noz_h), (od / 2, -hh * 0.6)])
        txt(tag, od / 2 + 20, -hh - noz_h, 12, C.BOTTOM_LEFT)
    for x in (-lr, lr):
        msp.add_lwpolyline([(x - 15, z_leg_top), (x - 15, bot), (x + 15, bot), (x + 15, z_leg_top)])
    W = R + J
    msp.add_linear_dim(base=(0, bot - 50), p1=(-W, bot), p2=(W, bot), dimstyle='EZDXF').render()
    msp.add_linear_dim(base=(W + 60, 0), p1=(W, 0), p2=(W, L), angle=90, dimstyle='EZDXF').render()
    msp.add_linear_dim(base=(-W - 60, 0), p1=(-W, bot), p2=(-W, top), angle=90, dimstyle='EZDXF').render()
    txt('主视图', 0, bot - 120, 18)
    ox = 2.2 * W + 200
    msp.add_circle((ox, L / 2), R)
    if jacket:
        msp.add_circle((ox, L / 2), R + J, dxfattribs={'linetype': 'DASHED'})
    for tag, ang, r, od, *_ in top_n:
        cx, cy = ox + r * math.cos(math.radians(ang)), L / 2 + r * math.sin(math.radians(ang))
        msp.add_circle((cx, cy), od / 2)
        txt(tag, cx, cy + od / 2 + 4, 12)
    txt('管口方位图', ox, L / 2 - W - 60, 18)
    # 管口表
    ty = L / 2 - W - 140
    txt('管口表', ox - W, ty, 16, C.BOTTOM_LEFT)
    for i, n in enumerate(nozzles):
        tag, ang, r, od = n[:4]
        svc = n[4] if len(n) > 4 else ''
        where = '底部' if ang is None else f'顶部 {ang}°'
        txt(f'{tag}  外径 {od}  {where}  {svc}', ox - W, ty - 26 * (i + 1), 13, C.BOTTOM_LEFT)
    txt(f'{name} 外形示意  内径 {D}  直边 {L}  2:1 椭圆封头  单位 mm', 0, bot - 170, 16)
    dxf = os.path.join(out, f'{name}外形图.dxf')
    doc.saveas(dxf)
    return {'step': step, 'dxf': dxf, 'outer': (round(bb.xlen), round(bb.zlen))}
