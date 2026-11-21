// 管道仪表图（PID）的排版：模型只给设备、管线、阀门、仪表，这里算出每样东西画在哪。
// 规矩（按设计院的画法）：设备排成一排，位号名称写在图上方；管线只走横竖，从设备管口接出；
// 跨设备、回流的管线走设备下方各自的通道，安全阀出口走上方通道去火炬；没接到别处的一头画接续标记；
// 仪表按位号字母定画法（带 C 的进 DCS，只有 I、T 的就地），模型给了「装在哪」就按它给的。
// 图小（三五台到十来台设备），用确定的规则排，不引排版库：内网离线部署，少一个依赖。
import type { D } from '../util';
import { list, s } from '../util';

export type EqKind = 'reactor' | 'vessel' | 'column' | 'tank' | 'exchanger' | 'pump' | 'compressor' | 'filter' | 'mixer' | 'valve';
export type Dir = 'l' | 'r' | 'u' | 'd';
export interface Pt { x: number; y: number }
export interface Rect { x1: number; y1: number; x2: number; y2: number }

export interface Eq {
  id: string; label: string; kind: EqKind; sub: string;
  x: number; y: number; w: number; h: number; ry: number;   // 中心、本体宽高、封头高
  top: number; bottom: number; left: number; right: number;  // 本体外缘（含封头）
  crown: number; foot: number;                               // 最高点（电机）、最低点（支腿）
}
export interface Nozzle { x: number; y: number; dir: Dir; name?: string; utility?: string }
export interface ValvePlace { type: string; tag: string; x: number; y: number; vertical: boolean; flow: Dir; actuated: boolean }
export interface Relief { tag: string; x: number; y: number }
export interface Arrow { x: number; y: number; d: Dir }
export interface Text { x: number; y: number; text: string; anchor: 'start' | 'middle' | 'end'; rotate?: boolean; size?: number; gray?: boolean }
export interface OffPage { x: number; y: number; text: string; ref: string; out: boolean }
export interface Bubble { tag: string; fn: string; num: string; loc: 'field' | 'panel' | 'dcs'; x: number; y: number }
export interface Wire { d: string; signal: boolean }
export interface Run { id: string; path: Pt[]; thin: boolean }

export interface PidLayout {
  width: number; height: number;
  eqs: Eq[]; runs: Run[]; nozzles: Nozzle[]; valves: ValvePlace[]; reliefs: Relief[]; arrows: Arrow[];
  texts: Text[]; offpages: OffPage[]; bubbles: Bubble[]; wires: Wire[];
  area: Rect;   // 图框里画图的区域（标题栏、说明另算）
}

// ── 设备 ────────────────────────────────────────────
export function kindOf(t: string): EqKind {
  const x = t.toLowerCase();
  if (/react|釜|反应/.test(x)) return 'reactor';
  if (/column|tower|塔/.test(x)) return 'column';
  if (/exchang|heater|cooler|condens|reboil|换热|加热|冷却|冷凝|再沸/.test(x)) return 'exchanger';
  if (/compress|压缩|风机|fan|blower/.test(x)) return 'compressor';
  if (/pump|泵/.test(x)) return 'pump';
  if (/tank|储罐|储槽|贮/.test(x)) return 'tank';
  if (/filter|过滤/.test(x)) return 'filter';
  if (/mix|混合/.test(x)) return 'mixer';
  if (/^valve|阀$/.test(x)) return 'valve';
  return 'vessel';
}
/** 换热器的壳程介质：按类型写在管口旁。 */
function shellSide(t: string): [string, string] {
  const x = t.toLowerCase();
  if (/heater|加热|预热|reboil|再沸/.test(x)) return ['热媒进', '热媒出'];
  if (/cooler|condens|冷却|冷凝/.test(x)) return ['冷却水进', '冷却水出'];
  return ['壳程进', '壳程出'];
}

const DIM: Record<EqKind, [number, number, number]> = {   // 本体宽、高、封头高
  reactor: [150, 240, 30], vessel: [120, 200, 24], column: [96, 380, 24], tank: [170, 170, 0], exchanger: [244, 64, 0],
  pump: [44, 44, 0], compressor: [84, 64, 0], filter: [84, 130, 16], mixer: [90, 34, 0], valve: [40, 26, 0],
};
const vertical = (k: EqKind) => k === 'reactor' || k === 'vessel' || k === 'column' || k === 'filter' || k === 'tank';

function makeEq(id: string, label: string, type: string, x: number, y: number): Eq {
  const kind = kindOf(type || label || id);
  const [w, h, ry] = DIM[kind];
  const top = y - h / 2 - ry, bottom = y + h / 2 + ry;
  const crown = kind === 'reactor' ? top - 64 : kind === 'exchanger' ? top - 42 : top - 16;
  const foot = kind === 'vessel' || kind === 'filter' ? bottom + 28 : kind === 'exchanger' ? bottom + 46 : kind === 'pump' ? y + h / 2 + 12 : bottom + 16;
  return { id, label, kind, sub: type, x, y, w, h, ry, top, bottom, left: x - w / 2, right: x + w / 2, crown, foot };
}

// ── 小工具 ──────────────────────────────────────────
const hit = (a: Rect, b: Rect) => a.x1 < b.x2 && a.x2 > b.x1 && a.y1 < b.y2 && a.y2 > b.y1;
/** 窄体字的宽度估算：中文 1 个字宽，字母数字约 0.56。 */
export const textW = (t: string, size = 11) => [...t].reduce((w, c) => w + (/[　-鿿＀-￯]/.test(c) ? size : size * 0.56), 0);

const RELIEF = /safety|relief|psv|prv|安全|泄放|爆破/i;
const ACTUATED = /control|调节|on.?off|switch|切断|开关|actuat|电动|气动/i;

/** 仪表位号拆成字母和编号；按字母定装在哪（模型给了 location 就用它的）。 */
export function parseTag(tag: string): { fn: string; num: string } {
  const m = /^([A-Za-z]+)[-_\s]?(.*)$/.exec(tag.trim());
  return m ? { fn: m[1].toUpperCase(), num: m[2] || '' } : { fn: tag, num: '' };
}
export function locOf(fn: string, given: string): Bubble['loc'] {
  const g = given.toLowerCase();
  if (/dcs|控制系统/.test(g)) return 'dcs';
  if (/panel|盘|控制室/.test(g)) return 'panel';
  if (/field|local|就地|现场/.test(g)) return 'field';
  const rest = fn.slice(1);
  return /[CAY]/.test(rest) ? 'dcs' : 'field';
}

// ── 排版 ────────────────────────────────────────────
interface LineIn { id: string; from: string; to: string; size: string; label: string; valves: D[] }

export function layoutPid(d: D): PidLayout {
  const lines: LineIn[] = list(d.lines).map((l) => ({
    id: s(l.id), from: s(l.from), to: s(l.to), size: s(l.size), label: s(l.label) || s(l.to_label), valves: list(l.valves),
  }));
  const eqIn = list(d.equipment).map((e) => ({ id: s(e.tag) || s(e.id), label: s(e.label), type: s(e.type) })).filter((e) => e.id);
  for (const l of lines) for (const v of [l.from, l.to]) if (v && !eqIn.some((e) => e.id === v)) eqIn.push({ id: v, label: '', type: '' });

  // 1. 设备顺序：按管线方向分层（回流边不算），同层按出现顺序，排成一排
  const ids = eqIn.map((e) => e.id);
  const out = new Map(ids.map((i) => [i, [] as string[]]));
  for (const l of lines) if (l.from && l.to && l.from !== l.to) out.get(l.from)?.push(l.to);
  const back = new Set<string>(), state = new Map<string, number>();
  const dfs = (v: string) => {
    state.set(v, 1);
    for (const w of out.get(v) ?? []) { if (state.get(w) === 1) back.add(`${v}>${w}`); else if (!state.get(w)) dfs(w); }
    state.set(v, 2);
  };
  const indeg = new Map(ids.map((i) => [i, 0]));
  for (const l of lines) if (l.from && l.to) indeg.set(l.to, (indeg.get(l.to) ?? 0) + 1);
  for (const i of ids) if (!indeg.get(i)) dfs(i);
  for (const i of ids) if (!state.get(i)) dfs(i);
  const layer = new Map(ids.map((i) => [i, 0]));
  for (let k = 0; k < ids.length; k++)
    for (const l of lines)
      if (l.from && l.to && l.from !== l.to && !back.has(`${l.from}>${l.to}`) && layer.has(l.from) && layer.has(l.to))
        layer.set(l.to, Math.max(layer.get(l.to)!, layer.get(l.from)! + 1));
  const order = [...ids].sort((a, b) => layer.get(a)! - layer.get(b)! || ids.indexOf(a) - ids.indexOf(b));

  // 2. 竖向分带：上方设备名 → 安全阀出口通道 → 仪表一排 → 设备 → 下方通道
  const reliefLines = lines.filter((l) => l.from && !l.to && l.valves.some((v) => RELIEF.test(s(v.type)) || /^PSV|^PRV/i.test(s(v.tag))));
  const reliefTop = 200;
  const reliefY = (k: number) => reliefTop + k * 34;
  const instY = reliefY(Math.max(0, reliefLines.length - 1)) + 76;
  const probe = eqIn.map((e) => makeEq(e.id, e.label, e.type, 0, 0));
  const above = Math.max(140, ...probe.map((e) => -e.crown));
  const CY = instY + 40 + above;

  // 3. 横向：一台一列，列间留出放阀门、标注的空
  const valvesBetween = (a: string, b: string) => Math.max(0, ...lines.filter((l) => l.from === a && l.to === b).map((l) => l.valves.length));
  const eqs: Eq[] = [];
  const hasFeed = lines.some((l) => !l.from && l.to);
  let cursor = hasFeed ? 300 : 190;
  order.forEach((id, i) => {
    const e = eqIn.find((x) => x.id === id)!;
    const kind = kindOf(e.type || e.label || id);
    const [w] = DIM[kind];
    const y = kind === 'exchanger' || kind === 'mixer' || kind === 'valve' || kind === 'compressor' ? CY - 80 : kind === 'pump' ? CY + 70 : CY;
    eqs.push(makeEq(id, e.label, e.type, cursor + w / 2, y));
    const next = order[i + 1];
    const nv = next ? valvesBetween(id, next) : 0;
    cursor += w + 170 + (nv + 1) * 100;
  });
  const byId = new Map(eqs.map((e) => [e.id, e]));
  const col = new Map(order.map((id, i) => [id, i]));

  const nozzles: Nozzle[] = [];
  const runs: Run[] = [];
  const outRun = new Map<string, number>();   // 设备 → 它出口的第一条管线（泵出口压力表挂在这条线上）
  const valves: ValvePlace[] = [];
  const reliefs: Relief[] = [];
  const arrows: Arrow[] = [];
  const texts: Text[] = [];
  const offpages: OffPage[] = [];
  const occupied: Rect[] = eqs.map((e) => ({ x1: e.left - 6, y1: e.crown, x2: e.right + 6, y2: e.foot }));
  const free = (r: Rect) => !occupied.some((o) => hit(o, r));

  // 管口：每台设备的进口从上往下排，出口按类型
  const inCount = new Map<string, number>(), outCount = new Map<string, number>(), topCount = new Map<string, number>();
  const nozzleNo = new Map<string, number>();
  const nameNozzle = (eq: Eq) => { const k = (nozzleNo.get(eq.id) ?? 0) + 1; nozzleNo.set(eq.id, k); return `N${k}`; };
  const STUB = 16;
  const inlet = (eq: Eq): Nozzle => {
    const k = inCount.get(eq.id) ?? 0; inCount.set(eq.id, k + 1);
    if (vertical(eq.kind)) {
      const y = Math.min(eq.top + eq.ry + 40 + k * 44, eq.bottom - eq.ry - 20);
      return { x: eq.left - STUB, y, dir: 'l', name: nameNozzle(eq) };
    }
    if (eq.kind === 'pump') return { x: eq.left - STUB, y: eq.y, dir: 'l' };
    return { x: eq.left - STUB, y: eq.y + k * 20, dir: 'l' };
  };
  const outlet = (eq: Eq): Nozzle => {
    const k = outCount.get(eq.id) ?? 0; outCount.set(eq.id, k + 1);
    if (vertical(eq.kind)) {
      if (k === 0) return { x: eq.x, y: eq.bottom + STUB, dir: 'd', name: nameNozzle(eq) };
      return { x: eq.right + STUB, y: Math.min(eq.y + 30 + (k - 1) * 40, eq.bottom - eq.ry - 10), dir: 'r', name: nameNozzle(eq) };
    }
    if (eq.kind === 'pump') return { x: eq.x + 12, y: eq.top - STUB, dir: 'u' };
    return { x: eq.right + STUB, y: eq.y - k * 20, dir: 'r' };
  };
  const topNozzle = (eq: Eq): Nozzle => {
    const k = topCount.get(eq.id) ?? 0; topCount.set(eq.id, k + 1);
    const dx = vertical(eq.kind) ? eq.w * 0.27 + k * 22 : eq.w * 0.3;
    const x = eq.x + dx;
    const y = vertical(eq.kind) && eq.ry ? headY(eq, x) - STUB - 6 : eq.top - STUB;
    return { x, y, dir: 'u', name: vertical(eq.kind) ? nameNozzle(eq) : undefined };
  };

  // 下方通道
  const laneBase = Math.max(...eqs.map((e) => e.foot)) + 60;
  let lanes = 0;
  const nextLane = () => laneBase + 36 * lanes++;
  const gapUse = new Map<number, number>();
  /** 进目标设备前的那根竖线：放在目标左边的空里，同一个空里多根错开。 */
  const approachX = (eq: Eq) => {
    const c = col.get(eq.id) ?? 0;
    const k = gapUse.get(c) ?? 0; gapUse.set(c, k + 1);
    return eq.left - 46 - k * 18;
  };
  const rightX = Math.max(...eqs.map((e) => e.right)) + 150;

  for (const l of lines) {
    const a = l.from ? byId.get(l.from) : undefined;
    const b = l.to ? byId.get(l.to) : undefined;
    const isRelief = reliefLines.includes(l);
    const pts: Pt[] = [];
    if (a && isRelief) {
      // 安全阀：从设备顶上去，阀在立管顶上，出口横着去火炬
      const nz = topNozzle(a); nozzles.push(nz);
      const k = reliefLines.indexOf(l);
      const y = reliefY(k);
      const rv = l.valves.find((v) => RELIEF.test(s(v.type)) || /^PSV|^PRV/i.test(s(v.tag)))!;
      reliefs.push({ tag: s(rv.tag), x: nz.x, y });
      occupied.push({ x1: nz.x - 10, y1: y - 30, x2: nz.x + 16, y2: y + 16 });
      runs.push({ id: '', path: [{ x: nz.x, y: nz.y }, { x: nz.x, y: y + 14 }], thin: false });
      pts.push({ x: nz.x + 14, y }, { x: rightX, y });
      offpages.push({ x: rightX, y, text: l.label || '去火炬总管', ref: '', out: true });
      occupied.push({ x1: rightX, y1: y - 16, x2: rightX + 150, y2: y + 34 });
      texts.push({ x: nz.x - 16, y: y - 12, text: s(rv.tag), anchor: 'end' });
      occupied.push({ x1: nz.x - 16 - textW(s(rv.tag)), y1: y - 24, x2: nz.x - 14, y2: y - 8 });
      const rest = l.valves.filter((v) => v !== rv);
      finish(l, pts, rest);
      continue;
    }
    if (a && b) {
      const src = outlet(a); nozzles.push(src);
      const dst = inlet(b); nozzles.push(dst);
      const adjacent = (col.get(b.id) ?? 0) === (col.get(a.id) ?? 0) + 1;
      pts.push({ x: src.x, y: src.y });
      if (src.dir === 'r' && adjacent) {
        if (Math.abs(src.y - dst.y) < 1) pts.push({ x: dst.x, y: dst.y });
        else { const mx = approachX(b); pts.push({ x: mx, y: src.y }, { x: mx, y: dst.y }, { x: dst.x, y: dst.y }); }
      } else if (src.dir === 'u' && adjacent && dst.y < src.y) {
        pts.push({ x: src.x, y: dst.y }, { x: dst.x, y: dst.y });
      } else {
        const lane = nextLane();
        if (src.dir === 'r') pts.push({ x: src.x + 24, y: src.y }, { x: src.x + 24, y: lane });
        else if (src.dir === 'u') pts.push({ x: src.x, y: src.y - 20 }, { x: src.x + 40, y: src.y - 20 }, { x: src.x + 40, y: lane });
        else pts.push({ x: src.x, y: lane });
        const ax = approachX(b);
        pts.push({ x: ax, y: lane }, { x: ax, y: dst.y }, { x: dst.x, y: dst.y });
      }
    } else if (b) {
      const dst = inlet(b); nozzles.push(dst);
      const first = (col.get(b.id) ?? 0) === 0;
      const x0 = 66 + 140;   // 接续标记右端
      if (first) {
        pts.push({ x: x0, y: dst.y }, { x: dst.x, y: dst.y });
        offpages.push({ x: 66, y: dst.y, text: l.label || '来自上游', ref: '', out: false });
      } else {
        const lane = nextLane(), ax = approachX(b);
        pts.push({ x: x0, y: lane }, { x: ax, y: lane }, { x: ax, y: dst.y }, { x: dst.x, y: dst.y });
        offpages.push({ x: 66, y: lane, text: l.label || '来自上游', ref: '', out: false });
      }
    } else if (a) {
      const src = outlet(a); nozzles.push(src);
      const lane = nextLane();
      pts.push({ x: src.x, y: src.y });
      if (src.dir === 'r') pts.push({ x: src.x + 24, y: src.y }, { x: src.x + 24, y: lane });
      else if (src.dir === 'u') pts.push({ x: src.x, y: src.y - 20 }, { x: src.x + 40, y: src.y - 20 }, { x: src.x + 40, y: lane });
      else pts.push({ x: src.x, y: lane });
      pts.push({ x: rightX, y: lane });
      offpages.push({ x: rightX, y: lane, text: l.label || '去下游', ref: '', out: true });
    } else continue;
    for (const o of offpages.slice(-1)) occupied.push({ x1: o.x, y1: o.y - 16, x2: o.x + 150, y2: o.y + 34 });
    finish(l, pts, l.valves);
  }

  /** 一条管线：记下走线，摆阀门、流向箭头、管线号。 */
  function finish(l: LineIn, pts: Pt[], vs: D[]) {
    const path = simplify(pts);
    runs.push({ id: l.id, path, thin: false });
    if (l.from && !outRun.has(l.from)) outRun.set(l.from, runs.length - 1);
    const segs = path.slice(1).map((p, i) => ({ a: path[i], b: p }));
    const horiz = segs.filter((g) => g.a.y === g.b.y).sort((g1, g2) => Math.abs(g2.b.x - g2.a.x) - Math.abs(g1.b.x - g1.a.x));
    const vert = segs.filter((g) => g.a.x === g.b.x).sort((g1, g2) => Math.abs(g2.b.y - g2.a.y) - Math.abs(g1.b.y - g1.a.y));
    const marks: number[] = [];   // 这一段上已经占了的位置
    // 阀门：放在最长的一段横线上，平均分开，离拐角留出余量
    const seg = horiz[0] && Math.abs(horiz[0].b.x - horiz[0].a.x) >= 60 + vs.length * 50 ? horiz[0] : (vert[0] ?? horiz[0]);
    if (seg && vs.length) {
      const isV = seg.a.x === seg.b.x;
      const [p0, p1] = isV ? [seg.a.y, seg.b.y] : [seg.a.x, seg.b.x];
      const lo = Math.min(p0, p1) + 34, hi = Math.max(p0, p1) - 34;
      const flow: Dir = isV ? (p1 > p0 ? 'd' : 'u') : (p1 > p0 ? 'r' : 'l');
      vs.forEach((v, i) => {
        const t = lo + ((hi - lo) * (i + 1)) / (vs.length + 1);
        const x = isV ? seg.a.x : t, y = isV ? t : seg.a.y;
        const type = s(v.type), tag = s(v.tag);
        const actuated = ACTUATED.test(type) || /^(TV|FV|LV|PV|XV|SDV|ESDV|TCV|FCV|LCV|PCV)/i.test(tag);
        valves.push({ type, tag, x, y, vertical: isV, flow, actuated });
        marks.push(t);
        occupied.push(isV ? { x1: x - 8, y1: y - 12, x2: x + 8, y2: y + 12 } : { x1: x - 12, y1: y - (actuated ? 36 : 8), x2: x + 12, y2: y + 8 });
        if (tag) {
          const tw = textW(tag);
          const tx = isV ? x + 14 : x, ty = isV ? y + 4 : y + 26;
          texts.push({ x: tx, y: ty, text: tag, anchor: isV ? 'start' : 'middle' });
          occupied.push(isV ? { x1: tx, y1: ty - 11, x2: tx + tw, y2: ty + 2 } : { x1: tx - tw / 2, y1: ty - 11, x2: tx + tw / 2, y2: ty + 2 });
        }
      });
    }
    // 流向箭头：最长横线上找个空位；进设备前的竖线够长也画一个
    const placeArrow = (g: { a: Pt; b: Pt }, prefer: number[]) => {
      const isV = g.a.x === g.b.x;
      const [p0, p1] = isV ? [g.a.y, g.b.y] : [g.a.x, g.b.x];
      const len = Math.abs(p1 - p0);
      if (len < 80) return;
      for (const f of prefer) {
        const t = p0 + (p1 - p0) * f;
        if (marks.some((m) => Math.abs(m - t) < 30)) continue;
        const x = isV ? g.a.x : t, y = isV ? t : g.a.y;
        if (!free({ x1: x - 7, y1: y - 7, x2: x + 7, y2: y + 7 })) continue;
        arrows.push({ x, y, d: isV ? (p1 > p0 ? 'd' : 'u') : (p1 > p0 ? 'r' : 'l') });
        marks.push(t);
        return;
      }
    };
    const arrowsLater = () => {
      if (horiz[0]) placeArrow(horiz[0], [0.5, 0.3, 0.7, 0.15, 0.85]);
      const last = segs[segs.length - 1];
      if (last && last !== horiz[0] && last.a.x === last.b.x) placeArrow(last, [0.5, 0.35, 0.65]);
      else if (segs.length > 2) { const g = segs.find((x) => x.a.x === x.b.x && Math.abs(x.b.y - x.a.y) >= 80); if (g) placeArrow(g, [0.5, 0.35, 0.65]); }
    };
    // 管线号：在横线上找最宽的空当，放上面；放不下就放下面，再不行竖着写
    const label = [l.id, l.size].filter(Boolean).join(' ');
    if (!label) { arrowsLater(); return; }
    placeLabelOn(horiz, vert, marks, label);
    arrowsLater();
  }

  function placeLabelOn(horiz: { a: Pt; b: Pt }[], vert: { a: Pt; b: Pt }[], marks: number[], label: string) {
    const tw = textW(label);
    for (const g of horiz) {
      const x1 = Math.min(g.a.x, g.b.x) + 20, x2 = Math.max(g.a.x, g.b.x) - 20;
      const cuts = [x1, ...marks.filter((m) => m > x1 && m < x2).sort((p, q) => p - q), x2];
      const gaps = cuts.slice(1).map((c, i) => ({ a: cuts[i] + 16, b: c - 16 })).filter((x) => x.b - x.a >= tw).sort((p, q) => (q.b - q.a) - (p.b - p.a));
      for (const gp of gaps) {
        const cx = (gp.a + gp.b) / 2;
        for (const dy of [-10, 22]) {
          const r = { x1: cx - tw / 2, y1: g.a.y + dy - 11, x2: cx + tw / 2, y2: g.a.y + dy + 2 };
          if (free(r)) { texts.push({ x: cx, y: g.a.y + dy, text: label, anchor: 'middle' }); occupied.push(r); return; }
        }
      }
    }
    const g = vert[0];
    if (g) {
      const cy = (g.a.y + g.b.y) / 2;
      texts.push({ x: g.a.x - 8, y: cy, text: label, anchor: 'middle', rotate: true });
      occupied.push({ x1: g.a.x - 20, y1: cy - tw / 2, x2: g.a.x - 6, y2: cy + tw / 2 });
    }
  }

  // 没有任何管线接进来的设备：画出进料口（灰字），换热器画壳程两个口
  for (const e of eqs) {
    if (e.kind === 'exchanger') {
      const [i, o] = shellSide(e.sub || e.label);
      nozzles.push({ x: e.x - 72, y: e.top - 22, dir: 'u', utility: i }, { x: e.x + 72, y: e.bottom + 22, dir: 'd', utility: o });
    }
    if (!(inCount.get(e.id)) && e.kind !== 'pump') { const nz = inlet(e); nozzles.push({ ...nz, utility: '进料' }); }
  }

  // 4. 仪表
  const bubbles: Bubble[] = [];
  const wires: Wire[] = [];
  const valveByTag = new Map(valves.filter((v) => v.tag).map((v) => [v.tag.toUpperCase(), v]));
  const placeBubble = (x0: number, y: number, step = 50): number => {
    for (let k = 0; k < 30; k++) {
      for (const sgn of [1, -1]) {
        const x = x0 + sgn * k * step;
        if (free({ x1: x - 24, y1: y - 24, x2: x + 24, y2: y + 24 })) return x;
      }
    }
    return x0;
  };
  const tapUsed = new Map<string, number>();
  for (const ins of list(d.instruments)) {
    const tag = s(ins.tag);
    if (!tag) continue;
    const { fn, num } = parseTag(tag);
    const loc = locOf(fn, s(ins.location));
    const on = s(ins.on);
    const eq = byId.get(on);
    const run = runs.find((r) => r.id && r.id === on);
    const target = valveByTag.get(s(ins.signal_to).toUpperCase());
    let bx: number, by: number;
    if (eq && fn.startsWith('L') && vertical(eq.kind)) {
      // 液位：容器右侧，上下两个取压口
      by = eq.y;
      bx = placeBubble(eq.right + 64, by, 54);
      const t2 = eq.bottom - eq.ry - 20, t1 = eq.kind === 'column' ? t2 - 110 : eq.top + eq.ry + 30;
      if (eq.kind === 'column') by = (t1 + t2) / 2;
      wires.push({ d: `M${eq.right} ${t1} H${bx} V${by - 18} M${eq.right} ${t2} H${bx} V${by + 18}`, signal: false });
      occupied.push({ x1: eq.right, y1: t1 - 4, x2: bx + 4, y2: t2 + 4 });
    } else if (eq && (eq.kind === 'pump' || eq.kind === 'compressor') && outRun.has(eq.id)
      && riserOf(runs[outRun.get(eq.id)!])) {
      // 泵、压缩机上的仪表：挂在出口立管上，放在立管左边
      const [p0, p1] = riserOf(runs[outRun.get(eq.id)!])!;
      by = Math.round((p0.y + p1.y) / 2);
      bx = placeBubble(p0.x - 64, by, 44);
      wires.push({ d: `M${p0.x} ${by} H${bx + (bx < p0.x ? 18 : -18)}`, signal: false });
    } else if (eq) {
      // 设备上的仪表：有信号去调节阀的放在阀正上方，从封头外侧取压/测温；
      // 只是指示的放在设备正上方那一排，从封头内侧取。外侧、内侧分开，两根线不交叉。
      const head = vertical(eq.kind) && eq.ry > 0;
      let tx: number;
      if (target && !target.vertical) {
        by = target.y - 118;
        bx = placeBubble(target.x, by);
        const side = bx < eq.x ? -1 : 1;
        const k = tapUsed.get(eq.id + side) ?? 0; tapUsed.set(eq.id + side, k + 1);
        tx = head ? eq.x + side * Math.min(eq.w / 2 - 6, eq.w * 0.27 + 30 + k * 16) : eq.x + side * eq.w * 0.3;
      } else {
        const k = tapUsed.get(eq.id + 'row') ?? 0; tapUsed.set(eq.id + 'row', k + 1);
        const f = [-0.27, 0.44, -0.4, 0.15][k % 4];
        tx = head ? eq.x + f * eq.w : eq.x + [-24, 24, -44, 44][k % 4];
        by = instY;
        bx = placeBubble(tx, by);
        if (head && Math.abs(bx - eq.x) < eq.w / 2 - 8 && Math.abs(bx - eq.x) > 22) tx = bx;
      }
      const ty = head ? headY(eq, tx) : eq.top;
      if (Math.abs(tx - bx) < 2) wires.push({ d: `M${tx} ${ty} V${by + 18}`, signal: false });
      else wires.push({ d: `M${tx} ${ty} V${by} H${bx + (tx > bx ? 20 : -20)}`, signal: false });
      occupied.push({ x1: Math.min(tx, bx) - 2, y1: by - 2, x2: Math.max(tx, bx) + 2, y2: ty });
    } else if (run) {
      // 管线上的仪表（流量计等）：放在管线上方，竖线接到管线
      const segs = run.path.slice(1).map((p, i) => ({ a: run.path[i], b: p })).filter((g) => g.a.y === g.b.y)
        .sort((g1, g2) => Math.abs(g2.b.x - g2.a.x) - Math.abs(g1.b.x - g1.a.x));
      const g = segs[0];
      by = g ? g.a.y - 74 : instY;
      bx = placeBubble(g ? (g.a.x + g.b.x) / 2 : 200, by);
      if (g) wires.push({ d: `M${bx} ${by + 18} V${g.a.y}`, signal: false });
    } else {
      by = instY; bx = placeBubble(200, by);
    }
    bubbles.push({ tag, fn, num, loc, x: bx, y: by });
    occupied.push({ x1: bx - 24, y1: by - 24, x2: bx + 24, y2: by + 24 });
    if (target) {
      const top = target.vertical ? target.y : target.y - 34;
      const ex = target.vertical ? target.x - 26 : target.x;
      const sideOut = !!eq && fn.startsWith('L') && vertical(eq.kind);   // 液位：上下是取压线，信号从侧面出
      wires.push({
        d: sideOut ? `M${bx + 20} ${by} H${bx + 44} V${top - 24} H${ex} V${top}`
          : Math.abs(bx - ex) < 2 ? `M${bx} ${by + 20} V${top}` : `M${bx} ${by + 20} V${(by + 20 + top) / 2} H${ex} V${top}`,
        signal: true,
      });
    }
  }

  // 5. 设备位号和名称：图上方，对着各自的设备
  for (const e of eqs) {
    texts.push({ x: e.x, y: 96, text: e.id, anchor: 'middle', size: 16 });
    if (e.label) texts.push({ x: e.x, y: 124, text: e.label, anchor: 'middle', size: 15 });
  }
  // 管口编号
  for (const nz of nozzles) if (nz.name) {
    const dx = nz.dir === 'l' ? 2 : nz.dir === 'r' ? -2 : 14, dy = nz.dir === 'u' ? 4 : nz.dir === 'd' ? 14 : 20;
    texts.push({ x: nz.x + dx, y: nz.y + dy, text: nz.name, anchor: nz.dir === 'l' ? 'start' : nz.dir === 'r' ? 'end' : 'start', size: 10, gray: true });
  }

  const allX = [...eqs.map((e) => e.right + 40), ...offpages.map((o) => o.x + 160), ...bubbles.map((b) => b.x + 30)];
  const allY = [...eqs.map((e) => e.foot + 30), laneBase + 36 * lanes, ...bubbles.map((b) => b.y + 30)];
  const width = Math.max(1180, Math.ceil(Math.max(...allX) + 60));
  const areaBottom = Math.max(...allY) + 20;
  const height = Math.ceil(areaBottom + 210);
  return { width, height, eqs, runs, nozzles, valves, reliefs, arrows, texts, offpages, bubbles, wires, area: { x1: 30, y1: 30, x2: width - 30, y2: areaBottom } };
}

/** 立式容器椭圆封头上某个 x 处的高度。 */
export function headY(eq: Eq, x: number): number {
  const a = eq.w / 2, dx = Math.min(Math.abs(x - eq.x), a - 1);
  return eq.y - eq.h / 2 - eq.ry * Math.sqrt(1 - (dx * dx) / (a * a));
}

/** 管线开头那段往上走的立管（泵出口），够长才算。 */
function riserOf(r: Run): [Pt, Pt] | null {
  const [p0, p1] = r.path;
  return p0 && p1 && p0.x === p1.x && p0.y - p1.y >= 70 ? [p0, p1] : null;
}

/** 去掉重复点、同一直线上的中间点。 */
function simplify(pts: Pt[]): Pt[] {
  const p = pts.filter((q, i) => i === 0 || q.x !== pts[i - 1].x || q.y !== pts[i - 1].y);
  return p.filter((q, i) => {
    if (i === 0 || i === p.length - 1) return true;
    const a = p[i - 1], b = p[i + 1];
    return !((a.x === q.x && q.x === b.x) || (a.y === q.y && q.y === b.y));
  });
}
