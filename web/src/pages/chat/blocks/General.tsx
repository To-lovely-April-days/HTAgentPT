// 通用组件：关键数字、提示框、图表、步骤、时间线、检查清单、方案卡、计算过程、流程图、原文引用。
import { useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import type { D } from './util';
import { fmt, list, niceMax, niceStep, nums, PALETTE, s, strs, tickFractions } from './util';
import { Refs } from './Refs';
import { Icon } from '../../../components/Icons';

// ── 关键数字 ─────────────────────────────────────
export function Metrics({ d }: { d: D }) {
  return (
    <div className="blk-metrics">
      {list(d.items).map((m, i) => (
        <div key={i} className="blk-metric">
          <span className="lbl">{s(m.label)}</span>
          <b>{s(m.value)}{s(m.unit) && <small>{s(m.unit)}</small>}</b>
          {(s(m.note) || s(m.ref)) && <span className="note">{s(m.note)}<Refs v={m.ref} /></span>}
        </div>
      ))}
    </div>
  );
}

// ── 提示框 ───────────────────────────────────────
const TONE: Record<string, [string, ReactNode]> = { 结论: ['ok', <Icon key="ok" name="check" size={13} stroke={2.6} />], 提示: ['info', 'i'], 注意: ['warn', '!'], 危险: ['danger', '!'] };
export function Callout({ d }: { d: D }) {
  const [cls, sym] = TONE[s(d.tone)] ?? TONE.提示;
  return (
    <div className={'blk-callout ' + cls} role={cls === 'danger' ? 'alert' : undefined}>
      <span className="sym" aria-hidden="true">{sym}</span>
      <span className="body">
        {s(d.title) && <b>{s(d.title)}</b>}
        <span>{s(d.text)}</span>
      </span>
    </div>
  );
}

// ── 图表 ─────────────────────────────────────────
export function Chart({ d, title }: { d: D; title: string }) {
  const cats = strs(d.categories);
  const series = list(d.series).map((x, i) => ({ name: s(x.name) || `系列 ${i + 1}`, values: nums(x.values), type: s(x.type) || s(d.type) || 'bar', right: s(x.axis) === 'right', color: PALETTE[i % PALETTE.length] }));
  if (s(d.type) === 'pie') return <Pie cats={cats} values={series[0]?.values ?? []} title={title} />;
  if (cats.length === 0 || series.length === 0) return <div className="muted" style={{ padding: 16 }}>图表没有数据</div>;
  const W = 640, H = 260, L = 48, R = series.some((x) => x.right) ? 52 : 16, T = 22, B = 34;
  const pw = W - L - R, ph = H - T - B;
  const maxL = niceMax(Math.max(0, ...series.filter((x) => !x.right).flatMap((x) => x.values)));
  const ticks = tickFractions(maxL);
  // 右轴和左轴分同样多格，每格取整
  const maxR = niceStep(Math.max(0, ...series.filter((x) => x.right).flatMap((x) => x.values)) / (ticks.length - 1)) * (ticks.length - 1);
  const step = pw / cats.length;
  const bars = series.filter((x) => x.type === 'bar');
  const bw = Math.min(56, (step * 0.7) / Math.max(1, bars.length));
  const y = (v: number, right: boolean) => T + ph - (v / (right ? maxR : maxL)) * ph;
  const labelBars = cats.length * bars.length <= 14;
  return (
    <div className="blk-chart">
      <svg viewBox={`0 0 ${W} ${H}`} width="100%" role="img" aria-label={title || '图表'}>
        {s(d.y_label) && <text x={4} y={12} fontSize="11" fill="#6b6b6b">{s(d.y_label)}</text>}
        {s(d.y2_label) && <text x={W - 4} y={12} fontSize="11" fill="#8a4a00" textAnchor="end">{s(d.y2_label)}</text>}
        {ticks.map((t) => (
          <g key={t}>
            <line x1={L} x2={W - R} y1={T + ph * (1 - t)} y2={T + ph * (1 - t)} stroke={t === 0 ? '#dededb' : '#f1f1ef'} />
            <text x={L - 6} y={T + ph * (1 - t) + 4} fontSize="11" fill="#6b6b6b" textAnchor="end">{fmt(maxL * t)}</text>
            {R > 16 && <text x={W - R + 6} y={T + ph * (1 - t) + 4} fontSize="11" fill="#8a4a00">{fmt(maxR * t)}</text>}
          </g>
        ))}
        {cats.map((c, i) => <text key={c + i} x={L + step * (i + 0.5)} y={H - 12} fontSize="12" fill="#4a4a4a" textAnchor="middle">{c}</text>)}
        {bars.map((b, bi) => b.values.map((v, i) => {
          const x = L + step * (i + 0.5) - (bw * bars.length) / 2 + bw * bi;
          const top = y(v, b.right);
          return (
            <g key={`${bi}-${i}`}>
              <rect x={x + 1} y={top} width={bw - 2} height={T + ph - top} rx={3} fill={b.color} />
              {labelBars && <text x={x + bw / 2} y={top - 5} fontSize="11" fontWeight="600" textAnchor="middle" fill="#141414">{fmt(v)}</text>}
            </g>
          );
        }))}
        {series.filter((x) => x.type === 'line').map((ln, li) => {
          const pts = ln.values.map((v, i) => [L + step * (i + 0.5), y(v, ln.right)] as const);
          return (
            <g key={li}>
              <polyline points={pts.map((p) => p.join(',')).join(' ')} fill="none" stroke={ln.color} strokeWidth="2.5" strokeLinejoin="round" />
              {pts.map((p, i) => (
                <g key={i}>
                  <circle cx={p[0]} cy={p[1]} r={4} fill="#fff" stroke={ln.color} strokeWidth="2" />
                  {cats.length <= 12 && <text x={p[0]} y={p[1] - 9} fontSize="11" textAnchor="middle" fill={ln.color}>{fmt(ln.values[i])}</text>}
                </g>
              ))}
            </g>
          );
        })}
      </svg>
      <div className="blk-legend">
        {series.map((x) => (
          <span key={x.name}><i className={x.type === 'line' ? 'ln' : ''} style={{ background: x.color }} />{x.name}{x.right ? '（右轴）' : ''}</span>
        ))}
        {s(d.source) && <span className="src">来源：{s(d.source)}</span>}
      </div>
    </div>
  );
}

function Pie({ cats, values, title }: { cats: string[]; values: number[]; title: string }) {
  const total = values.reduce((a, b) => a + b, 0) || 1;
  const starts = values.map((_, i) => values.slice(0, i).reduce((a, b) => a + b, 0));
  const arcs = values.map((v, i) => {
    const a0 = (starts[i] / total) * Math.PI * 2;
    const a1 = ((starts[i] + v) / total) * Math.PI * 2;
    const p = (a: number, r: number) => [100 + r * Math.sin(a), 100 - r * Math.cos(a)];
    const [x0, y0] = p(a0, 90), [x1, y1] = p(a1, 90), [x2, y2] = p(a1, 52), [x3, y3] = p(a0, 52);
    const large = a1 - a0 > Math.PI ? 1 : 0;
    return { d: `M${x0},${y0} A90,90 0 ${large} 1 ${x1},${y1} L${x2},${y2} A52,52 0 ${large} 0 ${x3},${y3} Z`, color: PALETTE[i % PALETTE.length], label: cats[i] ?? `第 ${i + 1} 项`, v };
  });
  return (
    <div className="blk-pie">
      <svg viewBox="0 0 200 200" width="180" height="180" role="img" aria-label={title || '占比'}>
        {arcs.map((a, i) => <path key={i} d={a.d} fill={a.color} stroke="#fff" strokeWidth="1.5" />)}
      </svg>
      <div className="blk-pie-legend">
        {arcs.map((a, i) => (
          <span key={i}><i style={{ background: a.color }} />{a.label}<b className="tnum">{fmt(a.v)}</b><span className="muted tnum">{((a.v / total) * 100).toFixed(1)}%</span></span>
        ))}
      </div>
    </div>
  );
}

// ── 步骤 ─────────────────────────────────────────
export function Steps({ d }: { d: D }) {
  const items = list(d.items);
  return (
    <ol className="blk-steps">
      {items.map((x, i) => (
        <li key={i}>
          <span className="no" aria-hidden="true">{i + 1}</span>
          <span className="body">
            <b>{s(x.title)}<Refs v={x.ref} /></b>
            {s(x.detail) && <span>{s(x.detail)}</span>}
            {s(x.expect) && <span className="expect">正常：{s(x.expect)}</span>}
          </span>
        </li>
      ))}
    </ol>
  );
}

// ── 时间线 ───────────────────────────────────────
const TL: Record<string, string> = { done: '已完成', now: '进行中', todo: '未开始', late: '延期' };
export function Timeline({ d }: { d: D }) {
  return (
    <div className="blk-timeline">
      {list(d.items).map((x, i, arr) => {
        const st = TL[s(x.status)] ? s(x.status) : 'todo';
        return (
          <div key={i} className={'tl ' + st}>
            <span className="track"><i className="dot" />{i < arr.length - 1 && <i className="bar" />}</span>
            <span className="date tnum">{s(x.date)}</span>
            <b>{s(x.title)}</b>
            <span className="st">{TL[st]}</span>
            {s(x.note) && <span className="note">{s(x.note)}</span>}
          </div>
        );
      })}
    </div>
  );
}

// ── 检查清单（勾选状态存在这台电脑的浏览器里）────────
export function Checklist({ d, title }: { d: D; title: string }) {
  const items = list(d.items);
  const key = useMemo(() => 'ht.check.' + hash(title + items.map((x) => s(x.text)).join('|')), [title, items]);
  const [done, setDone] = useState<number[]>(() => {
    try { return JSON.parse(localStorage.getItem(key) ?? '[]') as number[]; } catch { return []; }
  });
  const toggle = (i: number) => {
    const next = done.includes(i) ? done.filter((x) => x !== i) : [...done, i];
    setDone(next);
    try { localStorage.setItem(key, JSON.stringify(next)); } catch { /* 存不了就只在本页有效 */ }
  };
  return (
    <div className="blk-check">
      {items.map((x, i) => (
        <label key={i} className={done.includes(i) ? 'on' : ''}>
          <input type="checkbox" checked={done.includes(i)} onChange={() => toggle(i)} />
          <span><span className="t">{s(x.text)}</span>{s(x.note) && <span className="note">{s(x.note)}</span>}</span>
        </label>
      ))}
      <div className="blk-check-sum">已完成 {done.length} / {items.length}</div>
    </div>
  );
}
function hash(t: string) { let h = 0; for (let i = 0; i < t.length; i++) h = (h * 31 + t.charCodeAt(i)) | 0; return (h >>> 0).toString(36); }

// ── 方案卡 ───────────────────────────────────────
export function Options({ d }: { d: D }) {
  return (
    <div className="blk-options">
      {list(d.items).map((o, i) => (
        <div key={i} className={'opt' + (o.recommended === true || s(o.recommended) === 'true' ? ' rec' : '')}>
          <span className="hd"><b>{s(o.name)}</b>{(o.recommended === true || s(o.recommended) === 'true') && <span className="tag">推荐</span>}</span>
          {s(o.price) && <span className="price">{s(o.price)}</span>}
          {strs(o.pros).map((p, j) => <span key={'p' + j} className="pro">+ {p}</span>)}
          {strs(o.cons).map((p, j) => <span key={'c' + j} className="con">− {p}</span>)}
        </div>
      ))}
    </div>
  );
}

// ── 计算过程 ─────────────────────────────────────
export function Formula({ d }: { d: D }) {
  const r = (d.result && typeof d.result === 'object' ? d.result : {}) as D;
  return (
    <div className="blk-formula">
      {s(d.formula) && <div className="eq">{s(d.formula)}</div>}
      {list(d.vars).map((v, i) => (
        <div key={i} className="var">
          <span className="sym">{s(v.symbol)}</span><span className="nm">{s(v.name)}</span><b className="tnum">{s(v.value)}</b><Refs v={v.ref} />
        </div>
      ))}
      {(s(r.expr) || s(r.value)) && (
        <div className="res">
          <span className="expr">{s(r.expr)}{s(r.expr) && ' ='}</span>
          <b>{s(r.value)}</b>
        </div>
      )}
      {r.checked === true && <span className="chk ok"><Icon name="check" size={13} stroke={2.2} style={{ verticalAlign: '-2px', marginRight: 3 }} />系统已按式子重算核对</span>}
      {r.checked === false && <span className="chk bad">按式子重算是 {s(r.computed)}，和上面的结果对不上，请核对</span>}
    </div>
  );
}

// ── 流程图（按连线自动分层排布）──────────────────────
export function Flow({ d, title }: { d: D; title: string }) {
  const nodes = list(d.nodes).map((x, i) => ({ id: s(x.id) || String(i), label: s(x.label), sub: s(x.sub), hot: x.highlight === true }));
  const edges = list(d.edges).map((e) => ({ from: s(e.from), to: s(e.to), label: s(e.label), dashed: e.dashed === true }))
    .filter((e) => nodes.some((x) => x.id === e.from) && nodes.some((x) => x.id === e.to));
  const rank = new Map(nodes.map((x) => [x.id, 0]));
  for (let k = 0; k < nodes.length; k++)
    for (const e of edges) if (e.from !== e.to && (rank.get(e.to) ?? 0) <= (rank.get(e.from) ?? 0) && (rank.get(e.from) ?? 0) < nodes.length) rank.set(e.to, (rank.get(e.from) ?? 0) + 1);
  const cols = new Map<number, string[]>();
  for (const x of nodes) { const r = rank.get(x.id) ?? 0; cols.set(r, [...(cols.get(r) ?? []), x.id]); }
  const NW = 128, NH = 54, GX = 44, GY = 22, P = 10;
  const pos = new Map<string, { x: number; y: number }>();
  const maxRows = Math.max(1, ...[...cols.values()].map((c) => c.length));
  for (const [r, ids] of cols) ids.forEach((id, i) => pos.set(id, { x: P + r * (NW + GX), y: P + (maxRows - ids.length) * (NH + GY) / 2 + i * (NH + GY) }));
  const W = P * 2 + (Math.max(0, ...cols.keys()) + 1) * (NW + GX) - GX;
  const H = P * 2 + maxRows * (NH + GY) - GY + 30;
  return (
    <div className="blk-flow">
      <svg viewBox={`0 0 ${W} ${H}`} width={Math.min(W, 900)} style={{ maxWidth: '100%' }} role="img" aria-label={title || '流程图'}>
        <defs><marker id="blk-ar" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path d="M0,0 L10,5 L0,10 z" fill="#6b6b6b" /></marker></defs>
        {edges.map((e, i) => {
          const a = pos.get(e.from)!, b = pos.get(e.to)!;
          const back = b.x <= a.x;
          const x1 = back ? a.x + NW / 2 : a.x + NW, y1 = back ? a.y + NH : a.y + NH / 2;
          const x2 = back ? b.x + NW / 2 : b.x, y2 = back ? b.y + NH : b.y + NH / 2;
          const path = back ? `M${x1},${y1} C${x1},${y1 + 34} ${x2},${y2 + 34} ${x2},${y2 + 2}` : `M${x1},${y1} C${x1 + GX / 2},${y1} ${x2 - GX / 2},${y2} ${x2 - 2},${y2}`;
          return (
            <g key={i}>
              <path d={path} fill="none" stroke={e.dashed ? '#c26a00' : '#6b6b6b'} strokeWidth="1.5" strokeDasharray={e.dashed ? '5 4' : undefined} markerEnd="url(#blk-ar)" />
              {e.label && <text x={(x1 + x2) / 2} y={back ? Math.max(y1, y2) + 30 : (y1 + y2) / 2 - 6} fontSize="11" fill="#5a5a5a" textAnchor="middle">{e.label}</text>}
            </g>
          );
        })}
        {nodes.map((x) => {
          const p = pos.get(x.id)!;
          return (
            <g key={x.id}>
              <rect x={p.x} y={p.y} width={NW} height={NH} rx={10} fill={x.hot ? '#e8eef8' : '#f6f6f5'} stroke={x.hot ? '#2a4a85' : '#dededb'} />
              <text x={p.x + NW / 2} y={p.y + (x.sub ? 23 : 32)} fontSize="13" fontWeight="600" textAnchor="middle" fill="#141414">{x.label.slice(0, 10)}</text>
              {x.sub && <text x={p.x + NW / 2} y={p.y + 41} fontSize="11" textAnchor="middle" fill="#5a5a5a">{x.sub.slice(0, 14)}</text>}
            </g>
          );
        })}
      </svg>
    </div>
  );
}

// ── 原文引用 ─────────────────────────────────────
export function Quote({ d }: { d: D }) {
  return (
    <div className="blk-quotes">
      {list(d.items).map((q, i) => (
        <figure key={i}>
          <blockquote>「{s(q.text)}」</blockquote>
          <figcaption>
            <Refs v={q.ref} /><span>{s(q.source)}</span>
            {q.verified === true && <span className="chk ok"><Icon name="check" size={13} stroke={2.2} style={{ verticalAlign: '-2px', marginRight: 3 }} />已核对原文</span>}
            {q.verified === false && <span className="chk bad">原文里没找到这段，请核对</span>}
          </figcaption>
        </figure>
      ))}
    </div>
  );
}

