// 回答里的组件：按 block 类型分发到具体组件，外面统一套标题、说明、复制和导出。
import { useRef, useState } from 'react';
import { ApiError, postDownload } from '../../../lib/api';
import type { D } from './util';
import { exportXlsx, followupsMail, list, s, strs } from './util';
import { Callout, Chart, Checklist, Flow, Formula, Metrics, Options, Quote, Steps, Timeline } from './General';
import { Bom, Clause, Datasheet, Deviation, FaultTree, Followups, Gantt, Match, OpWindow, Properties, RiskMatrix } from './Industry';
import { Equipment, Interlock, Molecule, Pfd, Pid, Reaction } from './Chem';
import { chemRows } from './chemData';
import { downloadText, serializeSvg, svgToPng } from '../../../components/code/text';

const META: Record<string, [string, string]> = {
  metrics: ['关键数字', 'b-blue'], callout: ['提示', 'b-gray'], chart: ['图表', 'b-amber'], steps: ['操作步骤', 'b-green'],
  timeline: ['时间线', 'b-teal'], checklist: ['检查清单', 'b-purple'], options: ['方案卡', 'b-gray'], formula: ['计算过程', 'b-blue'],
  flow: ['流程图', 'b-green'], quote: ['原文引用', 'b-red'], datasheet: ['设备数据表', 'b-blue'], deviation: ['技术偏离表', 'b-red'],
  window: ['操作窗口', 'b-green'], fault_tree: ['故障诊断树', 'b-amber'], risk_matrix: ['风险矩阵', 'b-red'], bom: ['BOM 结构', 'b-teal'],
  properties: ['物性数据', 'b-blue'], clause: ['规范条款', 'b-purple'], match: ['选型匹配', 'b-green'], followups: ['跟进事项', 'b-gray'],
  gantt: ['交付计划', 'b-teal'],
  pfd: ['工艺流程图', 'b-blue'], pid: ['管道仪表图', 'b-blue'], molecule: ['分子结构', 'b-purple'], reaction: ['反应方程式', 'b-purple'],
  equipment: ['设备简图', 'b-teal'], interlock: ['联锁因果表', 'b-red'],
};
const PICTURE = new Set(['pfd', 'pid', 'molecule', 'equipment', 'flow', 'chart']);

/** 能导出成表格的组件 → 行。 */
function rowsOf(kind: string, d: D): (string | number | null)[][] | null {
  switch (kind) {
    case 'datasheet':
      return [...list(d.header).map((h) => [s(h.label), s(h.value)]), [],
        ...list(d.sections).flatMap((sec) => [[s(sec.title), s(sec.col_a) || '设计', s(sec.col_b) || '操作', '单位', '来源'],
          ...list(sec.rows).map((r) => [s(r.name), s(r.a), s(r.b), s(r.unit), s(r.ref)]), []])];
    case 'deviation': return [['条款', '客户要求', '我方响应', '偏离', '说明'], ...list(d.items).map((x) => [s(x.clause), s(x.requirement), s(x.response), s(x.type), s(x.note)])];
    case 'bom': return [['层级', '名称', '数量', '材质', '图号'], ...list(d.items).map((x) => [s(x.level), s(x.name), s(x.qty), s(x.material), s(x.drawing)])];
    case 'match': return [['需求', '客户要求', '本方案', '是否满足'], ...list(d.items).map((x) => [s(x.need), s(x.required), s(x.offered), ({ yes: '满足', no: '不满足', partial: '部分' } as Record<string, string>)[s(x.ok)] ?? ''])];
    case 'followups': return [['事项', '负责', '截止', '状态'], ...list(d.items).map((x) => [s(x.task), s(x.owner), s(x.due), s(x.status)])];
    case 'properties': return [[s(d.substance), s(d.condition), s(d.phase)], ['物性', '数值', '单位'], ...list(d.items).map((x) => [s(x.name), s(x.value), s(x.unit)])];
    case 'risk_matrix': return [['编号', '风险', '可能性', '严重程度', '措施'], ...list(d.items).map((x, i) => [s(x.id) || `R${i + 1}`, s(x.title), s(x.likelihood), s(x.severity), s(x.measure)])];
    case 'metrics': return [['项目', '数值', '单位', '说明'], ...list(d.items).map((x) => [s(x.label), s(x.value), s(x.unit), s(x.note)])];
    case 'chart': {
      const series = list(d.series);
      return [['', ...series.map((x) => s(x.name))], ...strs(d.categories).map((c, i) => [c, ...series.map((x) => (Array.isArray(x.values) ? s((x.values as unknown[])[i]) : ''))])];
    }
    case 'gantt': return [['工序', '开始', '结束', '状态'], ...list(d.tasks).map((x) => [s(x.name), s(x.start), s(x.end), s(x.status)])];
    case 'pfd': case 'pid': case 'equipment': case 'interlock': return chemRows(kind, d);
    default: return null;
  }
}

/** 适合导出成 Word 的组件 → Markdown。 */
function mdOf(kind: string, d: D): string | null {
  switch (kind) {
    case 'checklist': return list(d.items).map((x) => `- [ ] ${s(x.text)}${s(x.note) ? `（${s(x.note)}）` : ''}`).join('\n');
    case 'steps': return list(d.items).map((x, i) => `${i + 1}. **${s(x.title)}** ${s(x.detail)}${s(x.expect) ? `\n   正常：${s(x.expect)}` : ''}`).join('\n');
    case 'options': return list(d.items).map((o) => `## ${s(o.name)}${o.recommended === true ? '（推荐）' : ''}\n${s(o.price)}\n${strs(o.pros).map((p) => `- 优点：${p}`).join('\n')}\n${strs(o.cons).map((p) => `- 代价：${p}`).join('\n')}`).join('\n\n');
    case 'deviation': case 'datasheet': case 'followups': {
      const rows = rowsOf(kind, d) ?? [];
      const head = rows[0] ?? [];
      return [`| ${head.join(' | ')} |`, `| ${head.map(() => '---').join(' | ')} |`, ...rows.slice(1).filter((r) => r.length).map((r) => `| ${r.map((c) => String(c ?? '').replace(/\|/g, '/')).join(' | ')} |`)].join('\n');
    }
    default: return null;
  }
}

export function BlockCard({ a }: { a: { block: string; title: string; note: string; data: unknown } }) {
  const d = (a.data && typeof a.data === 'object' ? a.data : {}) as D;
  const [label, tone] = META[a.block] ?? ['组件', 'b-gray'];
  const bodyRef = useRef<HTMLDivElement>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const title = a.title || (a.block === 'callout' ? '' : label);

  // 提示框本身就是一条醒目的提示，不再套卡片外框
  if (a.block === 'callout') return <Callout d={d} />;

  const rows = rowsOf(a.block, d);
  const md = mdOf(a.block, d);
  const copy = async () => {
    const text = a.block === 'followups' ? followupsMail(d) : bodyRef.current?.innerText ?? '';
    try { await navigator.clipboard.writeText(text); setMsg(a.block === 'followups' ? '已复制成邮件' : '已复制'); } catch { setMsg('复制失败'); }
  };
  const word = async () => {
    setMsg(null);
    try { await postDownload('/api/export/docx', { title: title || label, markdown: md ?? '', sources: [] }, `${title || label}.docx`); }
    catch (err) { setMsg(err instanceof ApiError ? err.message : '导出失败'); }
  };
  const body = (() => {
    switch (a.block) {
      case 'metrics': return <Metrics d={d} />;
      case 'chart': return <Chart d={d} title={title} />;
      case 'steps': return <Steps d={d} />;
      case 'timeline': return <Timeline d={d} />;
      case 'checklist': return <Checklist d={d} title={title} />;
      case 'options': return <Options d={d} />;
      case 'formula': return <Formula d={d} />;
      case 'flow': return <Flow d={d} title={title} />;
      case 'quote': return <Quote d={d} />;
      case 'datasheet': return <Datasheet d={d} />;
      case 'deviation': return <Deviation d={d} />;
      case 'window': return <OpWindow d={d} />;
      case 'fault_tree': return <FaultTree d={d} />;
      case 'risk_matrix': return <RiskMatrix d={d} />;
      case 'bom': return <Bom d={d} />;
      case 'properties': return <Properties d={d} />;
      case 'clause': return <Clause d={d} />;
      case 'match': return <Match d={d} />;
      case 'followups': return <Followups d={d} />;
      case 'gantt': return <Gantt d={d} />;
      case 'pfd': return <Pfd d={d} />;
      case 'pid': return <Pid d={d} />;
      case 'molecule': return <Molecule d={d} />;
      case 'reaction': return <Reaction d={d} />;
      case 'equipment': return <Equipment d={d} />;
      case 'interlock': return <Interlock d={d} />;
      default: return <pre className="tool-out">{JSON.stringify(d, null, 2)}</pre>;
    }
  })();
  // 图类组件：把里面的 svg 导出成 SVG / PNG
  const svgText = () => {
    const el = bodyRef.current?.querySelector('svg.chem-svg, svg.mol-svg, svg');
    return el instanceof SVGSVGElement ? serializeSvg(el) : null;
  };
  const picture = PICTURE.has(a.block);
  return (
    <section className="dc-card">
      <div className="dc-hd"><div className="dc-hd-row"><span className={'dc-badge ' + tone}>{label}</span>{title !== label && <b className="dc-title">{title}</b>}</div></div>
      <div ref={bodyRef}>{body}</div>
      <div className="dc-ft">
        <span>{msg ?? a.note}</span>
        <button type="button" onClick={() => void copy()}>{a.block === 'followups' ? '复制成邮件' : '复制'}</button>
        {md && <button type="button" onClick={() => void word()}>导出 Word</button>}
        {picture && <button type="button" onClick={() => { const t = svgText(); if (t) downloadText(`${title || label}.svg`, t, 'image/svg+xml'); }}>SVG</button>}
        {picture && <button type="button" onClick={() => { const t = svgText(); if (t) void svgToPng(t, `${title || label}.png`); }}>PNG</button>}
        {rows && <button type="button" onClick={() => void exportXlsx(title || label, rows)}>导出 Excel</button>}
      </div>
    </section>
  );
}
