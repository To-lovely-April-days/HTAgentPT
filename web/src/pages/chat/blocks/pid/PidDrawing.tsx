// 管道仪表图（PID）：按 pidLayout 算好的位置画。图例参照 HG/T 20519（设备、阀门）、HG/T 20505（仪表）；
// 图框、分区编号、标题栏、说明由系统补；图上中文用仿宋，位号用窄体。
import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { createPortal } from 'react-dom';
import type { D } from '../util';
import { s } from '../util';
import { Icon } from '../../../../components/Icons';
import { layoutPid } from './pidLayout';
import { saveBlob, toDrawio, toDxf, toPdf } from './pidExport';
import { serializeSvg } from '../../../../components/code/text';
import type { Bubble, Eq, Nozzle, PidLayout, Text } from './pidLayout';

const K = '#1a1a1a';
const FS = "FangSong, STFangsong, '仿宋', 'Songti SC', serif";
const TG = "'Arial Narrow', 'Roboto Condensed', Arial, sans-serif";
const MAIN = 2;

// ── 设备 ────────────────────────────────────────────
function vvesselPath(e: Eq) {
  const x1 = e.left, x2 = e.right, y1 = e.y - e.h / 2, y2 = e.y + e.h / 2;
  return `M${x1} ${y1} A${e.w / 2} ${e.ry} 0 0 1 ${x2} ${y1} L${x2} ${y2} A${e.w / 2} ${e.ry} 0 0 1 ${x1} ${y2} Z`;
}

function Equipment({ e }: { e: Eq }) {
  const body = { fill: '#fff', stroke: K, strokeWidth: 1.8 };
  const tan = (y: number) => <line x1={e.left} y1={y} x2={e.right} y2={y} stroke={K} strokeWidth={0.6} strokeDasharray="6 3" />;
  switch (e.kind) {
    case 'reactor':
      return (
        <g>
          <path d={vvesselPath(e)} {...body} />{tan(e.y - e.h / 2)}{tan(e.y + e.h / 2)}
          <line x1={e.x} y1={e.top} x2={e.x} y2={e.top - 14} stroke={K} strokeWidth={1.6} />
          <rect x={e.x - 14} y={e.top - 30} width={28} height={16} fill="#fff" stroke={K} strokeWidth={1.3} />
          <circle cx={e.x} cy={e.top - 48} r={16} fill="#fff" stroke={K} strokeWidth={1.3} />
          <text x={e.x} y={e.top - 43} textAnchor="middle" fontSize={13} fontWeight={700} fontFamily={TG}>M</text>
          <line x1={e.x} y1={e.top - 14} x2={e.x} y2={e.y + e.h / 2 - 26} stroke={K} strokeWidth={1.4} />
          <path d={`M${e.x - 32} ${e.y + e.h / 2 - 26} H${e.x + 32} M${e.x - 32} ${e.y + e.h / 2 - 32} V${e.y + e.h / 2 - 20} M${e.x + 32} ${e.y + e.h / 2 - 32} V${e.y + e.h / 2 - 20}`} stroke={K} strokeWidth={2} />
        </g>
      );
    case 'vessel': case 'filter':
      return (
        <g>
          <path d={vvesselPath(e)} {...body} />{tan(e.y - e.h / 2)}{tan(e.y + e.h / 2)}
          <path d={`M${e.left + 12} ${e.y + e.h / 2} V${e.foot} M${e.right - 12} ${e.y + e.h / 2} V${e.foot} M${e.left + 2} ${e.foot} H${e.left + 22} M${e.right - 22} ${e.foot} H${e.right - 2}`} stroke={K} strokeWidth={1.6} />
          {e.kind === 'filter' && <line x1={e.left + 8} y1={e.y + 20} x2={e.right - 8} y2={e.y - 20} stroke={K} strokeWidth={1} strokeDasharray="5 3" />}
        </g>
      );
    case 'column':
      return (
        <g>
          <path d={vvesselPath(e)} {...body} />
          {[0.2, 0.32, 0.44, 0.56, 0.68, 0.8].map((f) => <line key={f} x1={e.left + 6} x2={e.right - 6} y1={e.y - e.h / 2 + e.h * f} y2={e.y - e.h / 2 + e.h * f} stroke={K} strokeWidth={0.8} strokeDasharray="8 4" />)}
        </g>
      );
    case 'tank':
      return <path d={`M${e.left} ${e.top + 16} L${e.x} ${e.top} L${e.right} ${e.top + 16} V${e.bottom} H${e.left} Z`} {...body} />;
    case 'exchanger': {
      const r = e.h / 2, x1 = e.left + r, x2 = e.right - r;
      return (
        <g>
          <path d={`M${x1} ${e.top} H${x2} A${r} ${r} 0 0 1 ${x2} ${e.bottom} H${x1} A${r} ${r} 0 0 1 ${x1} ${e.top} Z`} {...body} />
          <line x1={x1 + 6} y1={e.top} x2={x1 + 6} y2={e.bottom} stroke={K} strokeWidth={1.2} />
          <path d={`M${x1 + 20} ${e.y} H${x2 - 14} M${x1 + 20} ${e.y - 8} H${x2 - 14} M${x1 + 20} ${e.y + 8} H${x2 - 14}`} stroke={K} strokeWidth={0.7} />
        </g>
      );
    }
    case 'pump':
      return (
        <g>
          <path d={`M${e.x - 16} ${e.y + 16} L${e.x - 24} ${e.y + 30} H${e.x + 24} L${e.x + 16} ${e.y + 16}`} fill="#fff" stroke={K} strokeWidth={1.4} />
          <circle cx={e.x} cy={e.y} r={22} {...body} />
          <path d={`M${e.x + 12} ${e.top} V${e.y - 6}`} stroke={K} strokeWidth={1.4} />
        </g>
      );
    case 'compressor':
      return <path d={`M${e.left} ${e.top} L${e.right} ${e.top + 14} V${e.bottom - 14} L${e.left} ${e.bottom} Z`} {...body} />;
    case 'mixer':
      return (
        <g>
          <rect x={e.left} y={e.top} width={e.w} height={e.h} {...body} />
          <path d={Array.from({ length: 6 }, (_, i) => `M${e.left + 8 + i * 13} ${e.top + 4} L${e.left + 15 + i * 13} ${e.bottom - 4}`).join(' ')} stroke={K} strokeWidth={0.9} />
        </g>
      );
    default:
      return <path d={`M${e.left} ${e.top} L${e.right} ${e.bottom} L${e.right} ${e.top} L${e.left} ${e.bottom} Z`} {...body} />;
  }
}

function NozzleStub({ n }: { n: Nozzle }) {
  const len = 16;
  const [dx, dy] = n.dir === 'l' ? [1, 0] : n.dir === 'r' ? [-1, 0] : n.dir === 'u' ? [0, 1] : [0, -1];
  const x2 = n.x + dx * len, y2 = n.y + dy * len;
  const fl = dx ? `M${n.x} ${n.y - 6} V${n.y + 6} M${n.x + dx * 3} ${n.y - 6} V${n.y + 6}` : `M${n.x - 6} ${n.y} H${n.x + 6} M${n.x - 6} ${n.y + dy * 3} H${n.x + 6}`;
  return (
    <g>
      <line x1={n.x} y1={n.y} x2={x2} y2={y2} stroke={K} strokeWidth={n.utility ? 1.3 : MAIN} />
      <path d={fl} stroke={K} strokeWidth={1.2} />
      {n.utility && (
        <text x={n.dir === 'l' ? n.x - 8 : n.x} y={n.dir === 'u' ? n.y - 8 : n.dir === 'd' ? n.y + 18 : n.y + 4}
          textAnchor={n.dir === 'l' ? 'end' : 'middle'} fontSize={12} fontFamily={FS} fill="#777">{n.utility}</text>
      )}
    </g>
  );
}

// ── 阀门 ────────────────────────────────────────────
function bowtie(x: number, y: number) {
  return <path d={`M${x - 9} ${y - 6} L${x + 9} ${y + 6} L${x + 9} ${y - 6} L${x - 9} ${y + 6} Z`} fill="#fff" stroke={K} strokeWidth={1.3} />;
}
function ValveBody({ type, x, y, actuated, flowBack }: { type: string; x: number; y: number; actuated: boolean; flowBack: boolean }) {
  const t = type.toLowerCase();
  const act = actuated && (
    <g>
      <line x1={x} y1={y - 6} x2={x} y2={y - 22} stroke={K} strokeWidth={1.2} />
      <path d={`M${x - 13} ${y - 22} A13 12 0 0 1 ${x + 13} ${y - 22} Z`} fill="#fff" stroke={K} strokeWidth={1.3} />
    </g>
  );
  if (/check|止回/.test(t)) {
    const tri = flowBack ? `M${x} ${y} L${x - 9} ${y + 6} L${x - 9} ${y - 6} Z` : `M${x} ${y} L${x + 9} ${y + 6} L${x + 9} ${y - 6} Z`;
    return <g>{bowtie(x, y)}<path d={tri} fill={K} /></g>;
  }
  if (/butterfly|蝶/.test(t)) {
    return <g><path d={`M${x - 7} ${y - 8} V${y + 8} M${x + 7} ${y - 8} V${y + 8} M${x - 7} ${y + 6} L${x + 7} ${y - 6}`} stroke={K} strokeWidth={1.3} /><circle cx={x} cy={y} r={2} fill={K} />{act}</g>;
  }
  if (/ball|球/.test(t)) return <g>{bowtie(x, y)}<circle cx={x} cy={y} r={3.4} fill="#fff" stroke={K} strokeWidth={1.2} />{act}</g>;
  if (/globe|截止|control|调节/.test(t)) return <g>{bowtie(x, y)}<circle cx={x} cy={y} r={2.6} fill={K} />{act}</g>;
  if (/needle|针/.test(t)) return <g>{bowtie(x, y)}<path d={`M${x - 4} ${y - 9} L${x} ${y - 2} L${x + 4} ${y - 9} Z`} fill={K} />{act}</g>;
  return <g>{bowtie(x, y)}{act}</g>;
}

function Relief({ x, y }: { x: number; y: number }) {
  return (
    <g>
      <path d={`M${x - 7} ${y + 14} L${x + 7} ${y + 14} L${x} ${y} Z`} fill="#fff" stroke={K} strokeWidth={1.3} />
      <path d={`M${x + 14} ${y - 7} L${x + 14} ${y + 7} L${x} ${y} Z`} fill="#fff" stroke={K} strokeWidth={1.3} />
      <path d={`M${x} ${y} V${y - 4} L${x - 5} ${y - 7} L${x + 5} ${y - 11} L${x - 5} ${y - 15} L${x + 5} ${y - 19} L${x} ${y - 22} V${y - 25}`} fill="none" stroke={K} strokeWidth={1.1} />
      <line x1={x - 6} y1={y - 25} x2={x + 6} y2={y - 25} stroke={K} strokeWidth={1.3} />
    </g>
  );
}

// ── 仪表 ────────────────────────────────────────────
function InstBubble({ b }: { b: Bubble }) {
  return (
    <g>
      {b.loc === 'dcs' && <rect x={b.x - 20} y={b.y - 20} width={40} height={40} fill="#fff" stroke={K} strokeWidth={1.2} />}
      <circle cx={b.x} cy={b.y} r={18} fill={b.loc === 'dcs' ? 'none' : '#fff'} stroke={K} strokeWidth={b.loc === 'dcs' ? 1.1 : 1.3} />
      {b.loc !== 'field' && <line x1={b.x - (b.loc === 'dcs' ? 20 : 18)} y1={b.y} x2={b.x + (b.loc === 'dcs' ? 20 : 18)} y2={b.y} stroke={K} strokeWidth={1} />}
      <text x={b.x} y={b.y - 3} textAnchor="middle" fontSize={b.fn.length > 3 ? 9 : 10.5} fontWeight={700} fontFamily={TG}>{b.fn}</text>
      <text x={b.x} y={b.y + 11} textAnchor="middle" fontSize={10} fontFamily={TG}>{b.num}</text>
    </g>
  );
}

function Arrowhead({ x, y, d }: { x: number; y: number; d: string }) {
  const s6 = 6, h = 4.2;
  const pts = d === 'r' ? `${x + s6},${y} ${x - s6},${y - h} ${x - s6},${y + h}` : d === 'l' ? `${x - s6},${y} ${x + s6},${y - h} ${x + s6},${y + h}`
    : d === 'u' ? `${x},${y - s6} ${x - h},${y + s6} ${x + h},${y + s6}` : `${x},${y + s6} ${x - h},${y - s6} ${x + h},${y - s6}`;
  return <polygon points={pts} fill={K} />;
}

function Label({ t }: { t: Text }) {
  const cjk = /[　-鿿]/.test(t.text);
  const props = { fontSize: t.size ?? 11, fontFamily: cjk ? FS : TG, fill: t.gray ? '#555' : K, textAnchor: t.anchor, fontWeight: t.size === 16 ? 700 : undefined };
  return t.rotate
    ? <text {...props} transform={`translate(${t.x} ${t.y}) rotate(-90)`}>{t.text}</text>
    : <text x={t.x} y={t.y} {...props}>{t.text}</text>;
}

// ── 图框、标题栏、说明 ───────────────────────────────
function Frame({ lay, title, project, no }: { lay: PidLayout; title: string; project: string; no: string }) {
  const W = lay.width, H = lay.height;
  const cols = Math.max(4, Math.round((W - 60) / 260)), rows = 4;
  const cw = (W - 60) / cols, rh = (H - 60) / rows;
  const tw = 470, th = 170, tx = W - 30 - tw, ty = H - 30 - th;
  const rr = [ty + 34, ty + 76, ty + 106, ty + 138];
  const now = new Date();
  const date = `${now.getFullYear()}.${String(now.getMonth() + 1).padStart(2, '0')}`;
  const notes = ['说明：', '1. 本图为方案阶段示意图，不作施工依据，正式图纸以设计出图为准。', '2. 管线号：介质代号-序号-公称直径；管道等级未注明。',
    '3. 设备、阀门图例参照 HG/T 20519；仪表位号与图形符号参照 HG/T 20505。', '4. 未注明的阀门公称直径同所在管线。'];
  const tt = (x: number, y: number, t: string, o: { size?: number; anchor?: 'middle' | 'start'; bold?: boolean; gray?: boolean; tg?: boolean } = {}) => (
    <text x={x} y={y} fontSize={o.size ?? 12} textAnchor={o.anchor ?? 'start'} fontWeight={o.bold ? 700 : undefined}
      fontFamily={o.tg ? TG : FS} fill={o.gray ? '#555' : K}>{t}</text>
  );
  return (
    <g>
      <rect x={0.5} y={0.5} width={W - 1} height={H - 1} fill="none" stroke={K} strokeWidth={0.8} />
      <rect x={30} y={30} width={W - 60} height={H - 60} fill="none" stroke={K} strokeWidth={2.4} />
      {Array.from({ length: cols }, (_, i) => (
        <Fragment key={i}>
          {tt(30 + cw * (i + 0.5), 21, String(i + 1), { anchor: 'middle', tg: true, size: 11 })}
          {tt(30 + cw * (i + 0.5), H - 11, String(i + 1), { anchor: 'middle', tg: true, size: 11 })}
          {i > 0 && <path d={`M${30 + cw * i} 0 V30 M${30 + cw * i} ${H - 30} V${H}`} stroke={K} strokeWidth={0.6} />}
        </Fragment>
      ))}
      {Array.from({ length: rows }, (_, j) => (
        <Fragment key={j}>
          {tt(15, 30 + rh * (j + 0.5) + 4, 'ABCDEF'[j], { anchor: 'middle', tg: true, size: 11 })}
          {tt(W - 15, 30 + rh * (j + 0.5) + 4, 'ABCDEF'[j], { anchor: 'middle', tg: true, size: 11 })}
        </Fragment>
      ))}
      {notes.map((t, i) => <Fragment key={i}>{tt(60, ty + 30 + i * 24, t, { size: i ? 13 : 14 })}</Fragment>)}
      <rect x={tx} y={ty} width={tw} height={th} fill="#fff" stroke={K} strokeWidth={1.8} />
      {rr.map((y) => <line key={y} x1={tx} y1={y} x2={tx + tw} y2={y} stroke={K} strokeWidth={0.9} />)}
      {tt(tx + tw / 2, ty + 23, project || '　', { anchor: 'middle', size: 15 })}
      {tt(tx + tw / 2, ty + 54, title, { anchor: 'middle', size: 18, bold: true })}
      {tt(tx + tw / 2, ty + 71, '管道及仪表流程图', { anchor: 'middle', size: 12 })}
      {([[0, '图号', no || '—'], [160, '版次', 'A'], [300, '比例', '无']] as const).map(([a, k, v]) => (
        <Fragment key={k}>
          {a > 0 && <line x1={tx + a} y1={rr[1]} x2={tx + a} y2={rr[2]} stroke={K} strokeWidth={0.9} />}
          {tt(tx + a + 10, rr[1] + 20, k, { gray: true })}{tt(tx + a + 50, rr[1] + 20, v, { tg: v !== '无', size: 13 })}
        </Fragment>
      ))}
      {([[0, '设计'], [118, '校对'], [236, '审核'], [354, '日期']] as const).map(([a, k]) => (
        <Fragment key={k}>
          {a > 0 && <line x1={tx + a} y1={rr[2]} x2={tx + a} y2={rr[3]} stroke={K} strokeWidth={0.9} />}
          {tt(tx + a + 10, rr[2] + 21, k, { gray: true })}{k === '日期' && tt(tx + a + 50, rr[2] + 21, date, { tg: true })}
        </Fragment>
      ))}
      {tt(tx + tw / 2, ty + 158, '示意图 · 正式图纸以设计出图为准 · HTLAB 智能体生成', { anchor: 'middle', gray: true })}
    </g>
  );
}

function Sheet({ lay, title, project, no }: { lay: PidLayout; title: string; project: string; no: string }) {
  const offpage = (o: PidLayout['offpages'][number], i: number) => (
    <g key={'o' + i}>
      <path d={`M${o.x} ${o.y - 14} H${o.x + 124} L${o.x + 140} ${o.y} L${o.x + 124} ${o.y + 14} H${o.x} Z`} fill="#fff" stroke={K} strokeWidth={1.2} />
      <text x={o.x + 62} y={o.y + 5} textAnchor="middle" fontSize={13} fontFamily={FS}>{o.text}</text>
    </g>
  );
  return (
    <svg className="chem-svg pid-svg" viewBox={`0 0 ${lay.width} ${lay.height}`} width={lay.width} height={lay.height} role="img" aria-label={`管道仪表图：${title}`}>
      <rect width={lay.width} height={lay.height} fill="#fff" data-bg="1" />
      <g data-layer="FRAME"><Frame lay={lay} title={title} project={project} no={no} />
        {lay.eqs.map((e) => <line key={'lb' + e.id} x1={e.x - 62} y1={104} x2={e.x + 62} y2={104} stroke={K} strokeWidth={1} />)}
      </g>
      <g data-layer="INST">{lay.wires.map((w, i) => <path key={'w' + i} d={w.d} fill="none" stroke={K} strokeWidth={w.signal ? 1 : 0.9} strokeDasharray={w.signal ? '7 4' : undefined} />)}</g>
      <g data-layer="PIPE">{lay.runs.map((r, i) => <polyline key={'r' + i} points={r.path.map((p) => `${p.x},${p.y}`).join(' ')} fill="none" stroke={K} strokeWidth={MAIN} strokeLinejoin="miter" />)}</g>
      <g data-layer="EQUIP">
        {lay.eqs.map((e) => <g key={e.id} data-item="eq"><Equipment e={e} /></g>)}
        {lay.nozzles.map((n, i) => <g key={'n' + i} data-item="nozzle"><NozzleStub n={n} /></g>)}
      </g>
      <g data-layer="VALVE">
        {lay.valves.map((v, i) => (
          <g key={'v' + i} data-item="valve" transform={v.vertical ? `rotate(90 ${v.x} ${v.y})` : undefined}>
            <ValveBody type={v.type} x={v.x} y={v.y} actuated={v.actuated} flowBack={v.flow === 'l' || v.flow === 'u'} />
          </g>
        ))}
        {lay.reliefs.map((r, i) => <g key={'p' + i} data-item="relief"><Relief x={r.x} y={r.y} /></g>)}
      </g>
      <g data-layer="PIPE">
        {lay.arrows.map((a, i) => <g key={'a' + i} data-item="arrow"><Arrowhead {...a} /></g>)}
        {lay.offpages.map((o, i) => <g key={'o' + i} data-item="offpage">{offpage(o, i)}</g>)}
      </g>
      <g data-layer="INST">{lay.bubbles.map((b, i) => <g key={'b' + i} data-item="bubble"><InstBubble b={b} /></g>)}</g>
      <g data-layer="TEXT">{lay.texts.map((t, i) => <Label key={'t' + i} t={t} />)}</g>
    </svg>
  );
}

/** 对话里的管道仪表图：卡片里按卡片宽度整张缩小显示（看全貌），点图或「全屏看」放大看细节。 */
export function PidDrawing({ d, title }: { d: D; title: string }) {
  const lay = useMemo(() => layoutPid(d), [d]);
  const [full, setFull] = useState(false);
  const [cw, setCw] = useState(0);
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = box.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setCw(el.clientWidth));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  useEffect(() => {
    if (!full) return;
    const f = (e: KeyboardEvent) => { if (e.key === 'Escape') setFull(false); };
    window.addEventListener('keydown', f);
    return () => window.removeEventListener('keydown', f);
  }, [full]);
  const k = cw ? Math.min(1, cw / lay.width) : 0.4;
  const name = title || s(d.title) || '管道及仪表流程图';
  const sheet = <Sheet lay={lay} title={name} project={s(d.project)} no={s(d.drawing_no)} />;
  const [msg, setMsg] = useState<string | null>(null);
  const file = (name + (s(d.drawing_no) ? ' ' + s(d.drawing_no) : '')).replace(/[\\/:*?"<>|]/g, '');
  const svgEl = () => box.current?.querySelector('svg.pid-svg') as SVGSVGElement | null;
  const run = async (kind: 'drawio' | 'dxf' | 'pdf') => {
    const el = svgEl();
    if (!el) return;
    setMsg(null);
    try {
      if (kind === 'dxf') saveBlob(`${file}.dxf`, toDxf(el), 'application/dxf');
      else if (kind === 'drawio') saveBlob(`${file}.drawio`, toDrawio(el, name), 'application/xml');
      else { setMsg('正在生成 PDF…'); saveBlob(`${file}.pdf`, await toPdf(serializeSvg(el), lay.width, lay.height, name), 'application/pdf'); setMsg(null); }
    } catch {
      setMsg('导出失败，换个浏览器再试');
    }
  };
  return (
    <>
      <div className="pid-wrap" ref={box}>
        <button type="button" className="pid-thumb" style={{ height: lay.height * k }} onClick={() => setFull(true)} aria-label="放大看管道仪表图">
          <div style={{ transform: `scale(${k})`, transformOrigin: '0 0', width: lay.width }}>{sheet}</div>
        </button>
        <button type="button" className="pid-full-btn" onClick={() => setFull(true)}>全屏看</button>
      </div>
      <div className="pid-export">
        <span>下载图纸</span>
        <button type="button" onClick={() => void run('drawio')} title="用 draw.io 打开接着改：设备、阀门是图块，文字和管线都能改">draw.io</button>
        <button type="button" onClick={() => void run('dxf')} title="AutoCAD 等 CAD 软件打开，按图层分，1 单位 = 1 mm">DXF</button>
        <button type="button" onClick={() => void run('pdf')} title="A3 横版，打印或发给客户">PDF（A3）</button>
        {msg && <i>{msg}</i>}
      </div>
      {full && <FullView onClose={() => setFull(false)}>{sheet}</FullView>}
    </>
  );
}

function FullView({ children, onClose }: { children: ReactNode; onClose: () => void }) {
  // 挂到页面最外层：放在对话里会被对话区域的层叠关系压在下面
  return createPortal(
    <div className="pid-full" role="dialog" aria-modal="true" aria-label="管道仪表图" onClick={onClose}>
      <div className="pid-full-in" onClick={(e) => e.stopPropagation()}>{children}</div>
      <button type="button" className="pid-full-x" aria-label="关闭" onClick={onClose}><Icon name="x" size={18} /></button>
    </div>,
    document.body,
  );
}
