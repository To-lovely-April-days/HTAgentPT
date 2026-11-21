// 管道仪表图导出：从页面里画好的那张 svg 直接生成，不经过服务器。
// · DXF（R12，AutoCAD 和各家 CAD 都能开）：矢量，按图层分（图框、设备、管线、阀门、仪表、文字），1 个单位 = 1 mm，图框宽 = A3 宽 420 mm
// · draw.io：设备、阀门、仪表圈是独立图块；管线、文字、标题栏都能改；图框锁住
// · PDF：A3 横版，整张按 250 dpi 的高清图片放进去，打印、发客户用

const K = '#1a1a1a';
type XY = [number, number];

export interface Poly { kind: 'poly'; pts: XY[]; closed: boolean; solid: boolean; dashed: boolean; layer: string; width: number }
export interface Circ { kind: 'circle'; cx: number; cy: number; r: number; layer: string }
export interface Txt { kind: 'text'; x: number; y: number; text: string; size: number; anchor: 'start' | 'middle' | 'end'; angle: number; cjk: boolean; bold: boolean; layer: string }
export type Prim = Poly | Circ | Txt;

export const LAYERS: Record<string, { name: string; color: number }> = {
  FRAME: { name: '图框', color: 7 }, EQUIP: { name: '设备', color: 7 }, PIPE: { name: '管线', color: 7 },
  VALVE: { name: '阀门', color: 3 }, INST: { name: '仪表', color: 4 }, TEXT: { name: '文字', color: 7 },
};

const isCjk = (s: string) => /[　-鿿＀-￯]/.test(s);

/** svg 里一个元素到 svg 自身坐标的变换（不管外面有没有缩放）。 */
function rel(svg: SVGSVGElement, el: SVGGraphicsElement): DOMMatrix {
  return svg.getScreenCTM()!.inverse().multiply(el.getScreenCTM()!);
}
const tf = (m: DOMMatrix, x: number, y: number): XY => [m.a * x + m.c * y + m.e, m.b * x + m.d * y + m.f];
const num = (el: Element, a: string) => Number(el.getAttribute(a) ?? 0);

/** 椭圆弧（SVG 端点表示）→ 折线点。 */
function arcPts(x1: number, y1: number, rx: number, ry: number, phiDeg: number, fa: number, fs: number, x2: number, y2: number, seg = 18): XY[] {
  if (!rx || !ry) return [[x2, y2]];
  const phi = (phiDeg * Math.PI) / 180, cos = Math.cos(phi), sin = Math.sin(phi);
  const dx = (x1 - x2) / 2, dy = (y1 - y2) / 2;
  const x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;
  let rxs = rx * rx, rys = ry * ry;
  const lam = (x1p * x1p) / rxs + (y1p * y1p) / rys;
  if (lam > 1) { rx *= Math.sqrt(lam); ry *= Math.sqrt(lam); rxs = rx * rx; rys = ry * ry; }
  const sign = fa === fs ? -1 : 1;
  const co = sign * Math.sqrt(Math.max(0, (rxs * rys - rxs * y1p * y1p - rys * x1p * x1p) / (rxs * y1p * y1p + rys * x1p * x1p)));
  const cxp = (co * rx * y1p) / ry, cyp = (-co * ry * x1p) / rx;
  const cx = cos * cxp - sin * cyp + (x1 + x2) / 2, cy = sin * cxp + cos * cyp + (y1 + y2) / 2;
  const ang = (ux: number, uy: number, vx: number, vy: number) => Math.atan2(ux * vy - uy * vx, ux * vx + uy * vy);
  const t1 = ang(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
  let dt = ang((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
  if (!fs && dt > 0) dt -= 2 * Math.PI;
  if (fs && dt < 0) dt += 2 * Math.PI;
  const out: XY[] = [];
  for (let i = 1; i <= seg; i++) {
    const t = t1 + (dt * i) / seg;
    out.push([cx + rx * Math.cos(t) * cos - ry * Math.sin(t) * sin, cy + rx * Math.cos(t) * sin + ry * Math.sin(t) * cos]);
  }
  return out;
}

/** path 的 d → 若干条折线（每个 M 起一条）。支持 M L H V A Z，大小写都认。 */
export function pathPolys(d: string): { pts: XY[]; closed: boolean }[] {
  const out: { pts: XY[]; closed: boolean }[] = [];
  let cur: { pts: XY[]; closed: boolean } | null = null;
  let x = 0, y = 0, sx = 0, sy = 0;
  const re = /([MLHVAZmlhvaz])([^MLHVAZmlhvaz]*)/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(d))) {
    const c = m[1], rel = c === c.toLowerCase() && c !== 'z' && c !== 'Z';
    const n = (m[2].match(/-?\d*\.?\d+(?:e-?\d+)?/gi) ?? []).map(Number);
    switch (c.toUpperCase()) {
      case 'M':
        for (let i = 0; i + 1 < n.length; i += 2) {
          x = rel ? x + n[i] : n[i]; y = rel ? y + n[i + 1] : n[i + 1];
          if (i === 0) { cur = { pts: [[x, y]], closed: false }; out.push(cur); sx = x; sy = y; } else cur!.pts.push([x, y]);
        }
        break;
      case 'L': for (let i = 0; i + 1 < n.length; i += 2) { x = rel ? x + n[i] : n[i]; y = rel ? y + n[i + 1] : n[i + 1]; cur?.pts.push([x, y]); } break;
      case 'H': for (const v of n) { x = rel ? x + v : v; cur?.pts.push([x, y]); } break;
      case 'V': for (const v of n) { y = rel ? y + v : v; cur?.pts.push([x, y]); } break;
      case 'A':
        for (let i = 0; i + 6 < n.length; i += 7) {
          const ex = rel ? x + n[i + 5] : n[i + 5], ey = rel ? y + n[i + 6] : n[i + 6];
          cur?.pts.push(...arcPts(x, y, n[i], n[i + 1], n[i + 2], n[i + 3], n[i + 4], ex, ey));
          x = ex; y = ey;
        }
        break;
      case 'Z': if (cur) { cur.closed = true; x = sx; y = sy; } break;
    }
  }
  return out.filter((p) => p.pts.length >= 2);
}

/** 把画好的 svg 拆成线、圆、字（都换到 svg 坐标）。 */
export function collect(svg: SVGSVGElement): { prims: Prim[]; W: number; H: number } {
  const vb = svg.viewBox.baseVal;
  const prims: Prim[] = [];
  const layerOf = (el: Element) => el.closest('[data-layer]')?.getAttribute('data-layer') ?? 'FRAME';
  for (const el of svg.querySelectorAll<SVGGraphicsElement>('line,polyline,polygon,path,rect,circle,text')) {
    if (el.hasAttribute('data-bg')) continue;
    const m = rel(svg, el);
    const layer = layerOf(el);
    const dashed = !!el.getAttribute('stroke-dasharray');
    const fill = el.getAttribute('fill');
    const dark = fill === K || fill === '#000' || fill === 'black';
    const width = Number(el.getAttribute('stroke-width') ?? 1);
    const push = (pts: XY[], closed: boolean, solidOk = false) =>
      prims.push({ kind: 'poly', pts: pts.map(([px, py]) => tf(m, px, py)), closed, solid: solidOk && dark && closed && pts.length === 3, dashed, layer, width });
    switch (el.tagName) {
      case 'line': push([[num(el, 'x1'), num(el, 'y1')], [num(el, 'x2'), num(el, 'y2')]], false); break;
      case 'polyline': case 'polygon': {
        const p = (el.getAttribute('points') ?? '').trim().split(/[\s,]+/).map(Number);
        const pts: XY[] = [];
        for (let i = 0; i + 1 < p.length; i += 2) pts.push([p[i], p[i + 1]]);
        push(pts, el.tagName === 'polygon', true);
        break;
      }
      case 'rect': {
        const x = num(el, 'x'), y = num(el, 'y'), w = num(el, 'width'), h = num(el, 'height');
        if (!el.getAttribute('stroke') && !dark) break;   // 没有边线的白底
        push([[x, y], [x + w, y], [x + w, y + h], [x, y + h]], true);
        break;
      }
      case 'circle': {
        const [cx, cy] = tf(m, num(el, 'cx'), num(el, 'cy'));
        prims.push({ kind: 'circle', cx, cy, r: num(el, 'r') * Math.hypot(m.a, m.b), layer });
        break;
      }
      case 'path':
        for (const p of pathPolys(el.getAttribute('d') ?? '')) {
          // 闭合的三角形（止回阀、箭头）去掉回到起点的那个重复点
          const pts = p.closed && p.pts.length > 1 && p.pts[0][0] === p.pts[p.pts.length - 1][0] && p.pts[0][1] === p.pts[p.pts.length - 1][1] ? p.pts.slice(0, -1) : p.pts;
          push(pts, p.closed, true);
        }
        break;
      case 'text': {
        const text = el.textContent ?? '';
        if (!text.trim()) break;
        const [x, y] = tf(m, num(el, 'x'), num(el, 'y'));
        const ff = el.getAttribute('font-family') ?? '';
        prims.push({
          kind: 'text', x, y, text, size: Number(el.getAttribute('font-size') ?? 11) * Math.hypot(m.a, m.b),
          anchor: (el.getAttribute('text-anchor') as Txt['anchor']) ?? 'start', angle: (Math.atan2(m.b, m.a) * 180) / Math.PI,
          cjk: isCjk(text) || /FangSong|仿宋/.test(ff), bold: el.getAttribute('font-weight') === '700', layer: 'TEXT',
        });
        break;
      }
    }
  }
  return { prims, W: vb.width, H: vb.height };
}

// ── DXF（R12 ASCII）────────────────────────────────────
/** DXF 里的中文写成 \U+XXXX，文件本身保持纯 ASCII，各家 CAD 都认。 */
const dxfStr = (s: string) => [...s].map((c) => (c.charCodeAt(0) < 128 ? c : `\\U+${c.codePointAt(0)!.toString(16).toUpperCase().padStart(4, '0')}`)).join('');

export function toDxf(svg: SVGSVGElement): string {
  const { prims, W, H } = collect(svg);
  const f = 420 / W;                       // 图框宽 = A3 宽 420 mm
  const X = (x: number) => (x * f).toFixed(3), Y = (y: number) => ((H - y) * f).toFixed(3);
  const o: string[] = [];
  const g = (code: number, v: string | number) => { o.push(String(code), String(v)); };
  g(0, 'SECTION'); g(2, 'HEADER');
  g(9, '$ACADVER'); g(1, 'AC1009');
  g(9, '$DWGCODEPAGE'); g(3, 'ANSI_936');
  g(9, '$INSUNITS'); g(70, 4);
  g(9, '$EXTMIN'); g(10, 0); g(20, 0);
  g(9, '$EXTMAX'); g(10, X(W)); g(20, Y(0));
  g(0, 'ENDSEC');
  g(0, 'SECTION'); g(2, 'TABLES');
  g(0, 'TABLE'); g(2, 'LTYPE'); g(70, 2);
  g(0, 'LTYPE'); g(2, 'CONTINUOUS'); g(70, 0); g(3, 'Solid line'); g(72, 65); g(73, 0); g(40, 0);
  g(0, 'LTYPE'); g(2, 'DASHED'); g(70, 0); g(3, '__ __ __'); g(72, 65); g(73, 2); g(40, 2.2); g(49, 1.4); g(49, -0.8);
  g(0, 'ENDTAB');
  g(0, 'TABLE'); g(2, 'LAYER'); g(70, Object.keys(LAYERS).length + 1);
  g(0, 'LAYER'); g(2, '0'); g(70, 0); g(62, 7); g(6, 'CONTINUOUS');
  for (const [k, v] of Object.entries(LAYERS)) { g(0, 'LAYER'); g(2, 'PID-' + k); g(70, 0); g(62, v.color); g(6, 'CONTINUOUS'); }
  g(0, 'ENDTAB');
  g(0, 'TABLE'); g(2, 'STYLE'); g(70, 3);
  for (const [name, font, wf] of [['STANDARD', 'txt', 1], ['FS', 'simfang.ttf', 0.8], ['TG', 'arialn.ttf', 1]] as const) {
    g(0, 'STYLE'); g(2, name); g(70, 0); g(40, 0); g(41, wf); g(50, 0); g(71, 0); g(42, 2.5); g(3, font); g(4, '');
  }
  g(0, 'ENDTAB');
  g(0, 'ENDSEC');
  g(0, 'SECTION'); g(2, 'ENTITIES');
  for (const p of prims) {
    const layer = 'PID-' + p.layer;
    if (p.kind === 'circle') { g(0, 'CIRCLE'); g(8, layer); g(10, X(p.cx)); g(20, Y(p.cy)); g(30, 0); g(40, (p.r * f).toFixed(3)); continue; }
    if (p.kind === 'text') {
      const h = (p.size * 0.72 * f).toFixed(3);
      g(0, 'TEXT'); g(8, layer); g(10, X(p.x)); g(20, Y(p.y)); g(30, 0); g(40, h); g(1, dxfStr(p.text));
      if (p.angle) g(50, (-p.angle).toFixed(2));
      g(7, p.cjk ? 'FS' : 'TG');
      const al = p.anchor === 'middle' ? 1 : p.anchor === 'end' ? 2 : 0;
      if (al) { g(72, al); g(11, X(p.x)); g(21, Y(p.y)); g(31, 0); }
      continue;
    }
    if (p.solid) {   // 涂黑的小三角：SOLID（第 4 点同第 3 点）
      const [a, b, c] = p.pts;
      g(0, 'SOLID'); g(8, layer); g(10, X(a[0])); g(20, Y(a[1])); g(11, X(b[0])); g(21, Y(b[1])); g(12, X(c[0])); g(22, Y(c[1])); g(13, X(c[0])); g(23, Y(c[1]));
      continue;
    }
    if (p.pts.length === 2 && !p.closed) {
      g(0, 'LINE'); g(8, layer); if (p.dashed) g(6, 'DASHED');
      g(10, X(p.pts[0][0])); g(20, Y(p.pts[0][1])); g(30, 0); g(11, X(p.pts[1][0])); g(21, Y(p.pts[1][1])); g(31, 0);
      continue;
    }
    g(0, 'POLYLINE'); g(8, layer); if (p.dashed) g(6, 'DASHED'); g(66, 1); g(10, 0); g(20, 0); g(30, 0); g(70, p.closed ? 1 : 0);
    if (p.width >= 1.6) { g(40, (0.5).toFixed(2)); g(41, (0.5).toFixed(2)); }   // 主物料管线、设备外形画粗一点（0.5 mm）
    for (const [x, y] of p.pts) { g(0, 'VERTEX'); g(8, layer); g(10, X(x)); g(20, Y(y)); g(30, 0); }
    g(0, 'SEQEND'); g(8, layer);
  }
  g(0, 'ENDSEC'); g(0, 'EOF');
  return o.join('\r\n') + '\r\n';
}

// ── draw.io ─────────────────────────────────────────
const xml = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const b64 = (s: string) => btoa(unescape(encodeURIComponent(s)));

export function toDrawio(svg: SVGSVGElement, title: string): string {
  const vb = svg.viewBox.baseVal;
  const W = vb.width, H = vb.height;
  const box = svg.getBoundingClientRect();
  const k = box.width / W;
  const bboxOf = (el: Element) => {
    const r = el.getBoundingClientRect();
    return { x: (r.left - box.left) / k, y: (r.top - box.top) / k, w: r.width / k, h: r.height / k };
  };
  const cells: string[] = [];
  let id = 10;
  const layerIds: Record<string, string> = {};
  for (const [key, v] of Object.entries(LAYERS)) {
    layerIds[key] = 'L_' + key;
    cells.push(`<mxCell id="L_${key}" value="${xml(v.name)}" style="${key === 'FRAME' ? 'locked=1;' : ''}" parent="0"/>`);
  }
  const image = (el: Element, layer: string, extra = '') => {
    const c = el.cloneNode(true) as Element;
    c.querySelectorAll('text').forEach((t) => t.remove());
    const b = bboxOf(el), pad = 3;
    const x = b.x - pad, y = b.y - pad, w = b.w + pad * 2, h = b.h + pad * 2;
    const s = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${x} ${y} ${w} ${h}" width="${w}" height="${h}">${new XMLSerializer().serializeToString(c)}</svg>`;
    cells.push(`<mxCell id="c${id++}" value="" style="shape=image;imageAspect=0;aspect=fixed;verticalLabelPosition=bottom;image=data:image/svg+xml,${b64(s)};${extra}" vertex="1" parent="${layerIds[layer]}">`
      + `<mxGeometry x="${x.toFixed(1)}" y="${y.toFixed(1)}" width="${w.toFixed(1)}" height="${h.toFixed(1)}" as="geometry"/></mxCell>`);
  };
  const edge = (pts: XY[], layer: string, width: number, dashed: boolean) => {
    if (pts.length < 2) return;
    const [a, ...rest] = pts, z = rest[rest.length - 1], mid = rest.slice(0, -1);
    cells.push(`<mxCell id="c${id++}" value="" style="endArrow=none;startArrow=none;html=1;rounded=0;strokeColor=${K};strokeWidth=${width};${dashed ? 'dashed=1;dashPattern=7 4;' : ''}" edge="1" parent="${layerIds[layer]}">`
      + `<mxGeometry relative="1" as="geometry"><mxPoint x="${a[0]}" y="${a[1]}" as="sourcePoint"/><mxPoint x="${z[0]}" y="${z[1]}" as="targetPoint"/>`
      + (mid.length ? `<Array as="points">${mid.map((p) => `<mxPoint x="${p[0]}" y="${p[1]}"/>`).join('')}</Array>` : '') + '</mxGeometry></mxCell>');
  };
  // 图框（线和格子）整块锁住；标题栏里的字单独做成可改的文字
  const frame = svg.querySelector('[data-layer="FRAME"]');
  if (frame) image(frame, 'FRAME', 'locked=1;');
  // 管线、仪表连线：可改的线
  for (const pl of svg.querySelectorAll('[data-layer="PIPE"] > polyline')) {
    const p = (pl.getAttribute('points') ?? '').trim().split(/[\s,]+/).map(Number);
    const pts: XY[] = [];
    for (let i = 0; i + 1 < p.length; i += 2) pts.push([p[i], p[i + 1]]);
    edge(pts, 'PIPE', 2, false);
  }
  for (const w of svg.querySelectorAll('[data-layer="INST"] > path')) {
    for (const p of pathPolys(w.getAttribute('d') ?? '')) edge(p.pts, 'INST', Number(w.getAttribute('stroke-width') ?? 1), !!w.getAttribute('stroke-dasharray'));
  }
  // 设备、管口、阀门、箭头、接续标记、仪表圈：独立图块
  for (const it of svg.querySelectorAll('[data-item]')) image(it, it.closest('[data-layer]')?.getAttribute('data-layer') ?? 'EQUIP');
  // 所有文字：可改
  for (const t of svg.querySelectorAll<SVGTextElement>('text')) {
    const text = t.textContent ?? '';
    if (!text.trim()) continue;
    const b = bboxOf(t);
    const rotated = /rotate/.test(t.getAttribute('transform') ?? '');
    const w = (rotated ? b.h : b.w) + 6, h = (rotated ? b.w : b.h) + 2;
    const cx = b.x + b.w / 2, cy = b.y + b.h / 2;
    const ff = t.getAttribute('font-family') ?? '';
    const font = isCjk(text) || /FangSong/.test(ff) ? 'FangSong' : 'Arial Narrow';
    cells.push(`<mxCell id="c${id++}" value="${xml(text)}" style="text;html=0;align=center;verticalAlign=middle;spacing=0;resizable=1;fontSize=${t.getAttribute('font-size') ?? 11};fontFamily=${font};fontColor=${t.getAttribute('fill') && t.getAttribute('fill') !== K ? t.getAttribute('fill') : K};${t.getAttribute('font-weight') === '700' ? 'fontStyle=1;' : ''}${rotated ? 'rotation=-90;' : ''}" vertex="1" parent="${layerIds.TEXT}">`
      + `<mxGeometry x="${(cx - w / 2).toFixed(1)}" y="${(cy - h / 2).toFixed(1)}" width="${w.toFixed(1)}" height="${h.toFixed(1)}" as="geometry"/></mxCell>`);
  }
  return `<mxfile host="HTLAB" type="device"><diagram id="pid" name="${xml(title || '管道仪表图')}">`
    + `<mxGraphModel grid="0" gridSize="10" guides="1" tooltips="1" connect="1" arrows="1" fold="1" page="1" pageScale="1" pageWidth="${Math.ceil(W)}" pageHeight="${Math.ceil(H)}" math="0" shadow="0">`
    + `<root><mxCell id="0"/>${cells.join('')}</root></mxGraphModel></diagram></mxfile>`;
}

// ── PDF（A3 横版，整张高清图片）────────────────────────
const A3: [number, number] = [1190.55, 841.89];   // pt

async function rasterJpeg(svgText: string, W: number, H: number): Promise<{ jpeg: Uint8Array; w: number; h: number }> {
  const img = new Image();
  const url = URL.createObjectURL(new Blob([svgText], { type: 'image/svg+xml;charset=utf-8' }));
  try {
    await new Promise<void>((ok, fail) => { img.onload = () => ok(); img.onerror = () => fail(new Error('图转换失败')); img.src = url; });
    // A3 横版 250 dpi 约 4134 × 2923 像素
    const s = Math.min(4134 / W, 2923 / H);
    const w = Math.round(W * s), h = Math.round(H * s);
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    const g = c.getContext('2d')!;
    g.fillStyle = '#fff'; g.fillRect(0, 0, w, h);
    g.drawImage(img, 0, 0, w, h);
    const blob = await new Promise<Blob>((ok, fail) => c.toBlob((b) => (b ? ok(b) : fail(new Error('图转换失败'))), 'image/jpeg', 0.92));
    return { jpeg: new Uint8Array(await blob.arrayBuffer()), w, h };
  } finally { URL.revokeObjectURL(url); }
}

/** 一页 PDF，里面一张 JPEG，按比例放满 A3 横版（四边留 14 pt）。 */
export function pdfWithJpeg(jpeg: Uint8Array, iw: number, ih: number, title: string): Uint8Array<ArrayBuffer> {
  const [PW, PH] = A3, m = 14;
  const s = Math.min((PW - 2 * m) / iw, (PH - 2 * m) / ih);
  const dw = iw * s, dh = ih * s, x0 = (PW - dw) / 2, y0 = (PH - dh) / 2;
  const enc = new TextEncoder();
  const parts: Uint8Array[] = [];
  const offsets: number[] = [];
  let len = 0;
  const add = (b: Uint8Array | string) => { const u = typeof b === 'string' ? enc.encode(b) : b; parts.push(u); len += u.length; };
  const obj = (n: number, body: string | Uint8Array[]) => {
    offsets[n] = len;
    add(`${n} 0 obj\n`);
    if (typeof body === 'string') add(body); else body.forEach(add);
    add('\nendobj\n');
  };
  // 标题用 UTF-16BE 十六进制写，中文不乱码
  const hex = '<FEFF' + [...(title || '管道仪表图')].map((c) => c.charCodeAt(0).toString(16).padStart(4, '0')).join('').toUpperCase() + '>';
  const content = `q ${dw.toFixed(2)} 0 0 ${dh.toFixed(2)} ${x0.toFixed(2)} ${y0.toFixed(2)} cm /Im0 Do Q`;
  add('%PDF-1.4\n'); add(new Uint8Array([0x25, 0xe2, 0xe3, 0xcf, 0xd3, 0x0a]));
  obj(1, '<< /Type /Catalog /Pages 2 0 R >>');
  obj(2, '<< /Type /Pages /Kids [3 0 R] /Count 1 >>');
  obj(3, `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${PW} ${PH}] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>`);
  obj(4, `<< /Length ${content.length} >>\nstream\n${content}\nendstream`);
  obj(5, [enc.encode(`<< /Type /XObject /Subtype /Image /Width ${iw} /Height ${ih} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ${jpeg.length} >>\nstream\n`), jpeg, enc.encode('\nendstream')]);
  obj(6, `<< /Title ${hex} /Producer (HTLAB) >>`);
  const xref = len;
  add(`xref\n0 7\n0000000000 65535 f \n${offsets.slice(1).map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('')}`);
  add(`trailer\n<< /Size 7 /Root 1 0 R /Info 6 0 R >>\nstartxref\n${xref}\n%%EOF\n`);
  const out = new Uint8Array(len);
  let p = 0;
  for (const u of parts) { out.set(u, p); p += u.length; }
  return out;
}

export async function toPdf(svgText: string, W: number, H: number, title: string): Promise<Uint8Array<ArrayBuffer>> {
  const { jpeg, w, h } = await rasterJpeg(svgText, W, H);
  return pdfWithJpeg(jpeg, w, h, title);
}

export function saveBlob(name: string, data: BlobPart, type: string) {
  const url = URL.createObjectURL(new Blob([data], { type }));
  const a = document.createElement('a');
  a.href = url; a.download = name; a.click();
  setTimeout(() => URL.revokeObjectURL(url), 2000);
}
