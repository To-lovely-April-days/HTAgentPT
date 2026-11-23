// 化工专用组件：工艺流程图（PFD）、管道仪表图（PID）、分子结构、反应方程式、设备简图 + 管口表、联锁因果表。
// 模型只给结构化内容（设备、物流、仪表、SMILES、管口……），图由这里按标准图例画、自动排版；
// 分子式分子量、配平、物料衡算由服务端算好写进 data（balance、formula、weight），这里只显示。
import { Fragment, useState } from 'react';
import type { ReactNode } from 'react';
import type { D } from './util';
import { fmt, list, n, s } from './util';
import { MoleculeSvg } from '../../../components/code/Views';
import { chemParts } from './chemData';
import { Icon } from '../../../components/Icons';
import { PidDrawing } from './pid/PidDrawing';

/** 化学式：数字用真正的下标排，字距不散。 */
function ChemF({ f }: { f: string }) {
  return <>{chemParts(f).map((p, i) => (p.k === 'sub' ? <sub key={i}>{p.t}</sub> : p.k === 'sup' ? <sup key={i}>{p.t}</sup> : <Fragment key={i}>{p.t}</Fragment>))}</>;
}

// ── 图例 ───────────────────────────────────────────
type Kind = 'reactor' | 'column' | 'exchanger' | 'heater' | 'cooler' | 'pump' | 'compressor' | 'tank' | 'vessel' | 'separator' | 'filter' | 'mixer' | 'valve';
const SIZE: Record<string, [number, number]> = {
  reactor: [64, 84], column: [40, 130], exchanger: [56, 56], heater: [56, 56], cooler: [56, 56], pump: [46, 46], compressor: [56, 50],
  tank: [72, 70], vessel: [48, 80], separator: [48, 80], filter: [46, 60], mixer: [36, 36], valve: [30, 22],
};
const kindOf = (t: string): Kind => {
  const x = t.toLowerCase();
  if (/react|釜|反应/.test(x)) return 'reactor';
  if (/column|tower|塔/.test(x)) return 'column';
  if (/heater|加热|再沸|reboil/.test(x)) return 'heater';
  if (/cooler|condens|冷却|冷凝/.test(x)) return 'cooler';
  if (/exchang|换热/.test(x)) return 'exchanger';
  if (/compress|压缩|风机|fan|blower/.test(x)) return 'compressor';
  if (/pump|泵/.test(x)) return 'pump';
  if (/tank|罐|槽|储/.test(x)) return 'tank';
  if (/separ|分离|闪蒸|flash/.test(x)) return 'separator';
  if (/filter|过滤/.test(x)) return 'filter';
  if (/mix|混合/.test(x)) return 'mixer';
  if (/valve|阀/.test(x)) return 'valve';
  return 'vessel';
};

function Symbol({ kind, x, y }: { kind: Kind; x: number; y: number }) {
  const [w, h] = SIZE[kind];
  const l = x - w / 2, t = y - h / 2;
  const st = { fill: '#fff', stroke: '#2b2b2b', strokeWidth: 1.4 };
  switch (kind) {
    case 'reactor':
      return <g><rect x={l} y={t} width={w} height={h} rx={w / 2.6} {...st} /><line x1={x} y1={t - 10} x2={x} y2={y + 14} stroke="#2b2b2b" strokeWidth={1.4} />
        <path d={`M${x - 14} ${y + 14} L${x + 14} ${y + 8} M${x - 14} ${y + 8} L${x + 14} ${y + 14}`} stroke="#2b2b2b" strokeWidth={1.4} />
        <rect x={x - 5} y={t - 16} width={10} height={6} fill="#2b2b2b" /></g>;
    case 'column':
      return <g><rect x={l} y={t} width={w} height={h} rx={w / 2} {...st} />
        {[0.25, 0.4, 0.55, 0.7].map((f) => <line key={f} x1={l + 5} x2={l + w - 5} y1={t + h * f} y2={t + h * f} stroke="#9a9a9a" strokeDasharray="3 3" />)}</g>;
    case 'exchanger': case 'heater': case 'cooler':
      return <g><circle cx={x} cy={y} r={w / 2} {...st} />
        <path d={`M${l - 6} ${y} L${x - 12} ${y} L${x - 4} ${y - 12} L${x + 4} ${y + 12} L${x + 12} ${y} L${l + w + 6} ${y}`} fill="none" stroke="#2b2b2b" strokeWidth={1.3} />
        {kind !== 'exchanger' && <text x={x} y={y + w / 2 - 6} textAnchor="middle" fontSize={9} fill="#6b6b6b">{kind === 'heater' ? '加热' : '冷却'}</text>}</g>;
    case 'pump':
      return <g><circle cx={x} cy={y} r={w / 2} {...st} /><path d={`M${x} ${t} L${l + w + 6} ${t} L${l + w + 6} ${t + 10}`} fill="none" stroke="#2b2b2b" strokeWidth={1.4} />
        <path d={`M${l + 6} ${t + h + 6} L${l + w - 6} ${t + h + 6} L${x} ${y + 8} Z`} fill="#fff" stroke="#2b2b2b" strokeWidth={1.2} /></g>;
    case 'compressor':
      return <path d={`M${l} ${t} L${l + w} ${t + 12} L${l + w} ${t + h - 12} L${l} ${t + h} Z`} {...st} />;
    case 'tank':
      return <g><path d={`M${l} ${t + 12} L${x} ${t} L${l + w} ${t + 12} L${l + w} ${t + h} L${l} ${t + h} Z`} {...st} /></g>;
    case 'filter':
      return <g><rect x={l} y={t} width={w} height={h} rx={6} {...st} /><line x1={l} y1={t + h} x2={l + w} y2={t} stroke="#2b2b2b" strokeDasharray="4 3" /></g>;
    case 'mixer':
      return <g><circle cx={x} cy={y} r={w / 2} {...st} /><path d={`M${x - 8} ${y - 8} L${x + 8} ${y + 8} M${x - 8} ${y + 8} L${x + 8} ${y - 8}`} stroke="#2b2b2b" /></g>;
    case 'valve':
      return <path d={`M${l} ${t} L${l + w} ${t + h} L${l + w} ${t} L${l} ${t + h} Z`} {...st} />;
    default:
      return <rect x={l} y={t} width={w} height={h} rx={w / 2} {...st} />;
  }
}

// ── 自动排版 ──────────────────────────────────────
interface Node { id: string; kind: Kind; label: string; x: number; y: number; w: number; h: number }
interface Edge { id: string; from: string | null; to: string | null; label: string; raw: D }
interface Layout { nodes: Map<string, Node>; edges: { e: Edge; d: string; mid: [number, number]; seg: [number, number, number, number] }[]; width: number; height: number }

const ROW = 150, PAD = 70;

function layout(units: D[], edgesIn: Edge[], COL = 170): Layout {
  const ids = units.map((u) => s(u.id) || s(u.tag)).filter(Boolean);
  for (const e of edgesIn) for (const v of [e.from, e.to]) if (v && !ids.includes(v)) ids.push(v);
  const byId = new Map(units.map((u) => [s(u.id) || s(u.tag), u]));
  const out = new Map<string, string[]>(ids.map((i) => [i, []]));
  for (const e of edgesIn) if (e.from && e.to && e.from !== e.to) out.get(e.from)!.push(e.to);
  // 找回流（返回边）：深度优先，指向祖先的边不参与分层
  const back = new Set<string>();
  const state = new Map<string, number>();
  const dfs = (v: string) => {
    state.set(v, 1);
    for (const w of out.get(v) ?? []) {
      if (state.get(w) === 1) back.add(`${v}>${w}`);
      else if (!state.get(w)) dfs(w);
    }
    state.set(v, 2);
  };
  const indeg = new Map(ids.map((i) => [i, 0]));
  for (const e of edgesIn) if (e.from && e.to) indeg.set(e.to, (indeg.get(e.to) ?? 0) + 1);
  for (const i of ids) if (!indeg.get(i)) dfs(i);
  for (const i of ids) if (!state.get(i)) dfs(i);
  const layer = new Map(ids.map((i) => [i, 0]));
  for (let k = 0; k < ids.length; k++)
    for (const e of edgesIn)
      if (e.from && e.to && e.from !== e.to && !back.has(`${e.from}>${e.to}`))
        layer.set(e.to, Math.max(layer.get(e.to)!, layer.get(e.from)! + 1));
  const cols: string[][] = [];
  for (const i of ids) (cols[layer.get(i)!] ??= []).push(i);
  const maxRows = Math.max(1, ...cols.map((c) => c?.length ?? 0));
  const hasFeed = edgesIn.some((e) => !e.from && e.to);
  const x0 = PAD + (hasFeed ? 70 : 0);
  const nodes = new Map<string, Node>();
  cols.forEach((col, ci) => (col ?? []).forEach((id, ri) => {
    const u = byId.get(id) ?? {};
    const kind = kindOf(s(u.type) || s(u.label) || id);
    const [w, h] = SIZE[kind];
    const y = PAD + 30 + (ri + (maxRows - col.length) / 2) * ROW;
    nodes.set(id, { id, kind, label: s(u.label), x: x0 + ci * COL, y, w, h });
  }));
  let width = x0 + (cols.length - 1) * COL + PAD + (edgesIn.some((e) => e.from && !e.to) ? 90 : 0);
  let height = PAD + 30 + (maxRows - 1) * ROW + PAD + 40;
  const bottom = height - 30;
  const edges: Layout['edges'] = [];
  const feedCount = new Map<string, number>(), prodCount = new Map<string, number>();
  let recycle = 0;
  for (const e of edgesIn) {
    const a = e.from ? nodes.get(e.from) : null, b = e.to ? nodes.get(e.to) : null;
    let d: string, mid: [number, number], seg: [number, number, number, number];
    if (a && b) {
      const sx = a.x + a.w / 2, sy = a.y, tx = b.x - b.w / 2, ty = b.y;
      if (b.x > a.x) {
        const mx = (sx + tx) / 2;
        d = `M${sx} ${sy} H${mx} V${ty} H${tx}`; mid = [(sx + mx) / 2, sy]; seg = [sx, sy, mx, sy];
        if (Math.abs(sy - ty) < 1) { mid = [(sx + tx) / 2, sy]; seg = [sx, sy, tx, ty]; }
      } else {
        // 回流：从设备底部绕到下方，再回到目标设备底部
        const yy = bottom + 14 * recycle++;
        height = Math.max(height, yy + 30);
        d = `M${a.x} ${a.y + a.h / 2} V${yy} H${b.x} V${b.y + b.h / 2}`; mid = [(a.x + b.x) / 2, yy]; seg = [b.x, yy, a.x, yy];
      }
    } else if (b) {
      const k = feedCount.get(b.id) ?? 0; feedCount.set(b.id, k + 1);
      const y = b.y - 14 + k * 22, sx = b.x - b.w / 2 - 110;
      d = `M${sx} ${y} H${b.x - b.w / 2}`; mid = [sx + 34, y]; seg = [sx, y, b.x - b.w / 2, y];
    } else if (a) {
      const k = prodCount.get(a.id) ?? 0; prodCount.set(a.id, k + 1);
      const y = a.y - 14 + k * 22, ex = a.x + a.w / 2 + 110;
      width = Math.max(width, ex + 30);
      d = `M${a.x + a.w / 2} ${y} H${ex}`; mid = [ex - 34, y]; seg = [a.x + a.w / 2, y, ex, y];
    } else continue;
    edges.push({ e, d, mid, seg });
  }
  return { nodes, edges, width, height };
}

function Diagram({ lay, children, label }: { lay: Layout; children?: ReactNode; label: string }) {
  return (
    <div className="chem-wrap sc">
      <svg className="chem-svg" viewBox={`0 0 ${lay.width} ${lay.height}`} width={lay.width} height={lay.height} role="img" aria-label={label}
        style={{ fontFamily: 'Inter, "Noto Sans SC", sans-serif' }}>
        <defs>
          <marker id="chem-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
            <path d="M0 0 L10 5 L0 10 z" fill="#2b2b2b" />
          </marker>
        </defs>
        <rect width={lay.width} height={lay.height} fill="#fff" />
        {lay.edges.map(({ d }, i) => <path key={i} d={d} fill="none" stroke="#2b2b2b" strokeWidth={1.3} markerEnd="url(#chem-arrow)" />)}
        {[...lay.nodes.values()].map((nd) => (
          <g key={nd.id}>
            <Symbol kind={nd.kind} x={nd.x} y={nd.y} />
            <text x={nd.x} y={nd.y + nd.h / 2 + 16} textAnchor="middle" fontSize={11} fontWeight={600} fill="#141414">{nd.id}</text>
            {nd.label && <text x={nd.x} y={nd.y + nd.h / 2 + 30} textAnchor="middle" fontSize={10} fill="#5a5a5a">{nd.label}</text>}
          </g>
        ))}
        {children}
      </svg>
    </div>
  );
}

const edgesOf = (arr: unknown, key = 'id'): Edge[] => list(arr).map((x) => ({
  id: s(x[key]), from: s(x.from) || null, to: s(x.to) || null, label: s(x.label), raw: x,
}));

// ── 工艺流程图 ─────────────────────────────────────
export function Pfd({ d }: { d: D }) {
  const streams = edgesOf(d.streams);
  const lay = layout(list(d.units), streams);
  const bal = (d.balance ?? {}) as D;
  const items = list(bal.items);
  const bad = items.filter((x) => x.ok === false);
  const comps = [...new Set(list(d.streams).flatMap((x) => Object.keys((x.comp ?? {}) as object)))];
  return (
    <div>
      <Diagram lay={lay} label="工艺流程图">
        {lay.edges.map(({ e, mid }, i) => e.id && (
          <g key={i}>
            <path d={`M${mid[0]} ${mid[1] - 9} L${mid[0] + 10} ${mid[1]} L${mid[0]} ${mid[1] + 9} L${mid[0] - 10} ${mid[1]} Z`} fill="#fff" stroke="#2a4a85" strokeWidth={1.2} />
            <text x={mid[0]} y={mid[1] + 3.5} textAnchor="middle" fontSize={9.5} fontWeight={700} fill="#2a4a85">{e.id}</text>
            {e.label && <text x={mid[0]} y={mid[1] - 13} textAnchor="middle" fontSize={9.5} fill="#4a4a4a">{e.label}</text>}
          </g>
        ))}
      </Diagram>
      {streams.length > 0 && (
        <div className="dc-scroll">
          <table className="dc-table compact">
            <thead><tr><th>物流</th><th>从<Icon name="forward" size={11} className="gi m" />到</th><th className="num">温度</th><th className="num">压力</th><th className="num">流量</th><th>相态</th>{comps.map((c) => <th key={c} className="num">{c}</th>)}</tr></thead>
            <tbody>
              {streams.map((e, i) => (
                <tr key={i}>
                  <td><b>{e.id}</b>{e.label && <span className="cell-sub">{e.label}</span>}</td>
                  <td className="muted">{e.from ?? '进料'}<Icon name="forward" size={11} className="gi m" />{e.to ?? '产品'}</td>
                  <td className="num">{s(e.raw.T)}</td><td className="num">{s(e.raw.P)}</td>
                  <td className="num strong">{n(e.raw.flow) != null ? fmt(n(e.raw.flow)!) : s(e.raw.flow)} <span className="muted">{s(e.raw.unit)}</span></td>
                  <td>{s(e.raw.phase)}</td>
                  {comps.map((c) => <td key={c} className="num">{s(((e.raw.comp ?? {}) as D)[c])}</td>)}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {(items.length > 0 || s(bal.note)) && (
        <div className={'chem-bal' + (bad.length ? ' bad' : '')}>
          <b><Icon name={bad.length ? 'x' : 'check'} size={13} stroke={2.2} style={{ verticalAlign: '-2px', marginRight: 4 }} />{bad.length ? `物料衡算有 ${bad.length} 处对不上` : '物料衡算核对通过'}</b>
          {s(bal.note) && <span>{s(bal.note)}</span>}
          {items.map((x, i) => (
            <span key={i} className={x.ok === false ? 'bad' : ''}>{s(x.unit)}：进 {fmt(n(x.in) ?? 0)} / 出 {fmt(n(x.out) ?? 0)} {s(bal.flowUnit)}</span>
          ))}
        </div>
      )}
    </div>
  );
}

// ── 管道仪表图 ─────────────────────────────────────
export function Pid({ d, title = '' }: { d: D; title?: string }) {
  const insts = list(d.instruments);
  return (
    <div>
      <PidDrawing d={d} title={title} />
      {insts.length > 0 && (
        <div className="dc-scroll">
          <table className="dc-table compact">
            <thead><tr><th>仪表位号</th><th>位置</th><th>用途</th><th>量程</th><th>信号去向</th></tr></thead>
            <tbody>{insts.map((x, i) => (
              <tr key={i}><td className="mono"><b>{s(x.tag)}</b></td><td className="mono">{s(x.on)}</td><td>{s(x.service)}</td><td className="num">{s(x.range)}</td><td className="mono">{s(x.signal_to)}</td></tr>
            ))}</tbody>
          </table>
        </div>
      )}
      <div className="chem-note">示意图：图例参照 HG/T 20519、HG/T 20505 画出，正式图纸以设计院出图为准。</div>
    </div>
  );
}

// ── 分子结构 ──────────────────────────────────────
export function Molecule({ d }: { d: D }) {
  const [err, setErr] = useState<string | null>(null);
  const error = s(d.error) || err;
  return (
    <div className="chem-mol">
      <div className="chem-mol-pic">{!s(d.error) && <MoleculeSvg smiles={s(d.smiles)} width={300} height={220} onError={setErr} />}</div>
      <div className="chem-mol-info">
        {s(d.name) && <b className="chem-mol-name">{s(d.name)}</b>}
        {s(d.formula) && <span><i>分子式</i><b className="chem-f"><ChemF f={s(d.formula)} /></b></span>}
        {n(d.weight) != null && <span><i>分子量</i><b>{n(d.weight)!.toFixed(2)}</b> g/mol</span>}
        {s(d.cas) && <span><i>CAS</i><b className="mono">{s(d.cas)}</b></span>}
        <span><i>SMILES</i><code className="pcode">{s(d.smiles)}</code></span>
        {error ? <span className="chem-bad"><Icon name="x" size={13} stroke={2.2} style={{ verticalAlign: '-2px', marginRight: 4 }} />{error}</span> : <span className="chem-ok">分子式、分子量由系统按结构计算</span>}
      </div>
    </div>
  );
}

// ── 反应方程式 ─────────────────────────────────────
function Side({ items }: { items: D[] }) {
  return (
    <>
      {items.map((x, i) => (
        <span key={i} className="rx-sp">
          {i > 0 && <span className="rx-plus">+</span>}
          <span className="rx-term">
            <span className="rx-f">{n(x.coef) != null && n(x.coef) !== 1 ? <span className="rx-c">{fmt(n(x.coef)!)}</span> : null}<ChemF f={s(x.formula)} /></span>
            {s(x.name) && <span className="rx-name">{s(x.name)}</span>}
          </span>
        </span>
      ))}
    </>
  );
}

export function Reaction({ d }: { d: D }) {
  const bal = (d.balance ?? {}) as D;
  return (
    <div className="rx">
      <div className="rx-eq">
        <Side items={list(d.reactants)} />
        <span className="rx-arrow">
          <span className="rx-above">{s(d.above)}</span>
          <span className="rx-line">{d.reversible === true ? '⇌' : '⟶'}</span>
          <span className="rx-below">{s(d.below)}</span>
        </span>
        <Side items={list(d.products)} />
      </div>
      <div className="rx-ft">
        {bal.ok === true && <span className="chem-ok"><Icon name="check" size={13} stroke={2.2} style={{ verticalAlign: '-2px', marginRight: 4 }} />已配平（系统逐个元素核对）</span>}
        {bal.ok === false && <span className="chem-bad"><Icon name="x" size={13} stroke={2.2} style={{ verticalAlign: '-2px', marginRight: 4 }} />没配平：{s(bal.detail)}</span>}
        {s(bal.error) && <span className="chem-bad">{s(bal.error)}</span>}
        {s(d.dH) && <span>ΔH = {s(d.dH)}</span>}
      </div>
    </div>
  );
}

// ── 设备简图 + 管口表 ───────────────────────────────
export function Equipment({ d }: { d: D }) {
  const type = s(d.type).toLowerCase();
  const horizontal = /horizontal|卧|exchanger|换热/.test(type);
  const D0 = n(d.diameter) ?? 1000, L0 = n(d.length) ?? 2000;
  const nozzles = list(d.nozzles);
  // 成比例：长边画 300px
  const scale = 300 / Math.max(D0, L0 + D0 / 2);
  const dd = D0 * scale, ll = L0 * scale, head = /平/.test(s(d.head)) ? 0 : /锥/.test(s(d.head)) ? dd / 2 : dd / 4;
  const W = horizontal ? ll + 2 * head + 220 : dd + 260, H = horizontal ? dd + 200 : ll + 2 * head + 150;
  const cx = W / 2, cy = H / 2 - (horizontal ? 10 : 0);
  // 外形
  const shell = horizontal
    ? `M${cx - ll / 2} ${cy - dd / 2} H${cx + ll / 2} A${head || 0.01} ${dd / 2} 0 0 1 ${cx + ll / 2} ${cy + dd / 2} H${cx - ll / 2} A${head || 0.01} ${dd / 2} 0 0 1 ${cx - ll / 2} ${cy - dd / 2} Z`
    : `M${cx - dd / 2} ${cy + ll / 2} V${cy - ll / 2} A${dd / 2} ${head || 0.01} 0 0 1 ${cx + dd / 2} ${cy - ll / 2} V${cy + ll / 2} A${dd / 2} ${head || 0.01} 0 0 1 ${cx - dd / 2} ${cy + ll / 2} Z`;
  const sideCount = { left: 0, right: 0, top: 0, bottom: 0 };
  const placed = nozzles.map((x) => {
    let pos = s(x.position).toLowerCase();
    if (/顶|top/.test(pos)) pos = 'top'; else if (/底|bottom/.test(pos)) pos = 'bottom'; else if (/左|left/.test(pos)) pos = 'left'; else pos = /右|right/.test(pos) ? 'right' : 'side';
    if (pos === 'side') pos = sideCount.left <= sideCount.right ? 'left' : 'right';
    const k = sideCount[pos as keyof typeof sideCount]++;
    const at = n(x.at);
    let px: number, py: number, dx = 0, dy = 0;
    if (!horizontal) {
      if (pos === 'top') { px = cx + (k % 2 ? 1 : -1) * Math.ceil(k / 2) * dd / 5; py = cy - ll / 2 - head * Math.sqrt(Math.max(0, 1 - ((px - cx) / (dd / 2)) ** 2)); dy = -1; }
      else if (pos === 'bottom') { px = cx + (k % 2 ? 1 : -1) * Math.ceil(k / 2) * dd / 5; py = cy + ll / 2 + head * Math.sqrt(Math.max(0, 1 - ((px - cx) / (dd / 2)) ** 2)); dy = 1; }
      else { px = cx + (pos === 'left' ? -dd / 2 : dd / 2); py = at != null ? cy + ll / 2 - at * scale : cy - ll / 2 + (k + 1) * ll / (nozzles.length + 1); dx = pos === 'left' ? -1 : 1; }
    } else {
      if (pos === 'top') { px = at != null ? cx - ll / 2 + at * scale : cx - ll / 2 + (k + 1) * ll / (nozzles.length + 1); py = cy - dd / 2; dy = -1; }
      else if (pos === 'bottom') { px = at != null ? cx - ll / 2 + at * scale : cx - ll / 2 + (k + 1) * ll / (nozzles.length + 1); py = cy + dd / 2; dy = 1; }
      else { px = cx + (pos === 'left' ? -ll / 2 - head : ll / 2 + head); py = cy + (k % 2 ? 1 : -1) * Math.ceil(k / 2) * dd / 5; dx = pos === 'left' ? -1 : 1; }
    }
    return { x, px, py, dx, dy };
  });
  const support = s(d.support);
  return (
    <div>
      <div className="chem-wrap sc">
        <svg className="chem-svg" viewBox={`0 0 ${W} ${H}`} width={W} height={H} role="img" aria-label="设备简图" style={{ fontFamily: 'Inter, "Noto Sans SC", sans-serif' }}>
          <rect width={W} height={H} fill="#fff" />
          <path d={shell} fill="#f6f8fc" stroke="#2b2b2b" strokeWidth={1.5} />
          {/* 切线 */}
          {horizontal
            ? <><line x1={cx - ll / 2} x2={cx - ll / 2} y1={cy - dd / 2} y2={cy + dd / 2} stroke="#9a9a9a" strokeDasharray="4 3" /><line x1={cx + ll / 2} x2={cx + ll / 2} y1={cy - dd / 2} y2={cy + dd / 2} stroke="#9a9a9a" strokeDasharray="4 3" /></>
            : <><line y1={cy - ll / 2} y2={cy - ll / 2} x1={cx - dd / 2} x2={cx + dd / 2} stroke="#9a9a9a" strokeDasharray="4 3" /><line y1={cy + ll / 2} y2={cy + ll / 2} x1={cx - dd / 2} x2={cx + dd / 2} stroke="#9a9a9a" strokeDasharray="4 3" /></>}
          {/* 塔盘 */}
          {/column|塔/.test(type) && !horizontal && Array.from({ length: 6 }, (_, i) => <line key={i} x1={cx - dd / 2 + 4} x2={cx + dd / 2 - 4} y1={cy - ll / 2 + (i + 1) * ll / 7} y2={cy - ll / 2 + (i + 1) * ll / 7} stroke="#b4b4b0" strokeDasharray="3 3" />)}
          {/* 支座 */}
          {horizontal
            ? [-1, 1].map((sg) => <path key={sg} d={`M${cx + sg * ll / 3 - 14} ${cy + dd / 2 - 4} L${cx + sg * ll / 3 - 22} ${cy + dd / 2 + 24} H${cx + sg * ll / 3 + 22} L${cx + sg * ll / 3 + 14} ${cy + dd / 2 - 4}`} fill="#fff" stroke="#2b2b2b" />)
            : /裙/.test(support)
              ? <path d={`M${cx - dd / 2} ${cy + ll / 2 + head * 0.6} V${cy + ll / 2 + head + 34} M${cx + dd / 2} ${cy + ll / 2 + head * 0.6} V${cy + ll / 2 + head + 34} M${cx - dd / 2 - 8} ${cy + ll / 2 + head + 34} H${cx + dd / 2 + 8}`} stroke="#2b2b2b" fill="none" />
              : [-1, 1].map((sg) => <line key={sg} x1={cx + sg * dd / 2} x2={cx + sg * dd / 2} y1={cy + ll / 3} y2={cy + ll / 2 + head + 34} stroke="#2b2b2b" strokeWidth={2} />)}
          {/* 管口 */}
          {placed.map(({ x, px, py, dx, dy }, i) => {
            const ex = px + dx * 26, ey = py + dy * 26;
            return (
              <g key={i}>
                <line x1={px} y1={py} x2={ex} y2={ey} stroke="#2b2b2b" strokeWidth={4} />
                <line x1={ex - (dy ? 8 : 0)} y1={ey - (dx ? 8 : 0)} x2={ex + (dy ? 8 : 0)} y2={ey + (dx ? 8 : 0)} stroke="#2b2b2b" strokeWidth={2.4} />
                <text x={ex + dx * 10 + (dy ? 12 : 0)} y={ey + dy * 12 + 4} textAnchor={dx < 0 ? 'end' : 'start'} fontSize={11} fontWeight={700} fill="#2a4a85">{s(x.id)}</text>
              </g>
            );
          })}
          {/* 尺寸 */}
          {horizontal ? (
            <g fontSize={10} fill="#5a5a5a">
              <line x1={cx - ll / 2} x2={cx + ll / 2} y1={H - 24} y2={H - 24} stroke="#9a9a9a" />
              <text x={cx} y={H - 30} textAnchor="middle">切线长 {fmt(L0)} mm</text>
              <text x={cx} y={cy + 4} textAnchor="middle">Ø{fmt(D0)}</text>
            </g>
          ) : (
            <g fontSize={10} fill="#5a5a5a">
              <line x1={W - 40} x2={W - 40} y1={cy - ll / 2} y2={cy + ll / 2} stroke="#9a9a9a" />
              <text x={W - 44} y={cy} textAnchor="end">{fmt(L0)}</text>
              <text x={cx} y={cy + 4} textAnchor="middle">Ø{fmt(D0)}</text>
            </g>
          )}
        </svg>
      </div>
      {nozzles.length > 0 && (
        <div className="dc-scroll">
          <table className="dc-table compact">
            <thead><tr><th>管口</th><th>尺寸</th><th>法兰</th><th>用途</th><th>方位</th></tr></thead>
            <tbody>{nozzles.map((x, i) => (
              <tr key={i}><td className="mono"><b>{s(x.id)}</b></td><td className="mono">{s(x.size)}</td><td className="mono">{s(x.flange)}</td><td>{s(x.service)}</td><td className="muted">{s(x.position)}{n(x.at) != null ? ` · ${fmt(n(x.at)!)} mm` : ''}</td></tr>
            ))}</tbody>
          </table>
        </div>
      )}
      <div className="chem-note">按给定尺寸成比例画的示意外形{[s(d.head), support].filter(Boolean).length ? `（${[s(d.head), support].filter(Boolean).join('，')}）` : ''}，不代替制造图。</div>
    </div>
  );
}

// ── 联锁因果表 ─────────────────────────────────────
export function Interlock({ d }: { d: D }) {
  const causes = list(d.causes), effects = list(d.effects);
  const links = new Set(list(d.links).map((x) => `${s(x.cause)}>${s(x.effect)}`));
  // 也认 causes[].effects: ["E1"]
  for (const c of causes) for (const e of Array.isArray(c.effects) ? c.effects : []) links.add(`${s(c.id)}>${s(e)}`);
  return (
    <div>
      <div className="dc-scroll">
        <table className="dc-table compact il">
          <thead>
            <tr><th>触发条件</th>{effects.map((e, i) => <th key={i} className="il-e"><b className="mono">{s(e.id)}</b><span className="mono">{s(e.tag)}</span><span>{s(e.action)}</span></th>)}</tr>
          </thead>
          <tbody>
            {causes.map((c, i) => (
              <tr key={i}>
                <td><b className="mono">{s(c.id)}</b> <span className="mono">{s(c.tag)}</span><span className="cell-sub">{s(c.condition)}</span></td>
                {effects.map((e, j) => <td key={j} className="il-x">{links.has(`${s(c.id)}>${s(e.id)}`) ? <span className="il-dot" role="img" aria-label="触发"><Icon name="dot" size={14} style={{ verticalAlign: '-2px' }} /></span> : ''}</td>)}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="chem-note">{s(d.sil) ? `SIL 等级：${s(d.sil)}。` : ''}联锁逻辑最终以 HAZOP / SIL 评估为准。</div>
    </div>
  );
}


// ── 反应热安全评估（绝热温升、MTSR、Stoessel 等级由服务端算，写在 data.computed）──
const CLS_TONE = (c: number | null) => (c == null ? 'na' : c <= 2 ? 'ok' : c === 3 ? 'warn' : 'bad');

export function Thermal({ d }: { d: D }) {
  const c = (d.computed && typeof d.computed === 'object' ? d.computed : {}) as D;
  const batch = (c.batch ?? null) as D | null;
  const semi = (c.semi ?? null) as D | null;
  const tp = n(d.Tp), mtt = n(d.MTT), td24 = n(d.TD24);
  if (s(c.error) || !batch || tp == null) return <div className="blk-empty">{s(c.error) || '缺数据，算不了'}</div>;
  const rows: [string, D][] = [['一次全投（间歇）', batch], ...(semi ? [[`滴加，累积不超过 ${fmt((n(semi.accumulation) ?? 0) * 100)}%`, semi] as [string, D]] : [])];
  const pick = semi ?? batch;
  const marks: [string, number, string][] = [['Tp', tp, '#141414'], ...(semi ? [['滴加 MTSR', n(semi.mtsr)!, '#1D4A94'] as [string, number, string]] : []),
    [semi ? '全投 MTSR' : 'MTSR', n(batch.mtsr)!, '#A00D25'], ...(td24 != null ? [['TD24', td24, '#8A5A00'] as [string, number, string]] : []),
    ...(mtt != null ? [['MTT', mtt, '#1C6B45'] as [string, number, string]] : [])];
  const vals = marks.map((m) => m[1]);
  const lo = Math.floor((Math.min(...vals) - 20) / 50) * 50, hi = Math.ceil((Math.max(...vals) + 20) / 50) * 50;
  const pos = (t: number) => `${((t - lo) / (hi - lo)) * 100}%`;
  const sorted = [...marks].sort((a, b) => a[1] - b[1]);
  return (
    <div className="thr">
      {list(d.inputs).length > 0 && (
        <div className="thr-in">
          {list(d.inputs).map((x, i) => (
            <span key={i} className={'thr-chip' + (/假设|估/.test(s(x.source)) ? ' as' : '')} title={s(x.source)}>
              <i>{s(x.name)}</i>{s(x.value)}{s(x.source) && <em>{s(x.source)}</em>}
            </span>
          ))}
        </div>
      )}
      <table className="thr-t">
        <thead><tr><th>加料方式</th><th>绝热温升</th><th>MTSR</th><th>等级</th></tr></thead>
        <tbody>
          {rows.map(([label, r]) => {
            const cls = typeof r.class === 'number' ? r.class : null;
            return (
              <tr key={label} className={r === pick ? 'pick' : undefined}>
                <td>{label}</td>
                <td className="num">{Math.round((n(r.accumulation) ?? 1) * (n(c.dTad) ?? 0))} K</td>
                <td className="num">{Math.round(n(r.mtsr) ?? 0)} ℃</td>
                <td><span className={'thr-cls ' + CLS_TONE(cls)}>{cls ?? '—'}</span></td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <div className="thr-ax" aria-label="温度位置">
        <span className="thr-line" />
        {Array.from({ length: (hi - lo) / 50 + 1 }, (_, i) => lo + i * 50).map((t) => <span key={t} className="thr-tk" style={{ left: pos(t) }}>{t}</span>)}
        {sorted.map(([label, t, color], i) => (
          <span key={label} className="thr-mk" style={{ left: pos(t), top: i % 2 ? 26 : 2 }}>
            <b style={{ color }}>{label} {Math.round(t)}</b><span style={{ background: color, height: i % 2 ? 14 : 38 }} />
          </span>
        ))}
      </div>
      <div className={'thr-say ' + CLS_TONE(typeof pick.class === 'number' ? pick.class : null)}>{s(pick.text)}</div>
    </div>
  );
}
