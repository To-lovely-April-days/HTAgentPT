// 助手回答里的数据卡：材料成本估算表、按场景排版的表格（比价、涨跌、历史项目、方案对比、参数清单、普通表）。
// 选哪种样式由模型在 show_table 里说明；这里只负责按样式排版，数字列一律右对齐、等宽。
import { Fragment } from 'react';
import { Link } from 'react-router-dom';
import type { EstimateLine, ToolArtifact } from '../../lib/types';

type Estimate = Extract<ToolArtifact, { kind: 'estimate' }>;
type Table = Extract<ToolArtifact, { kind: 'table' }>;

const fmt = (v: number) => v.toLocaleString('zh-CN', { maximumFractionDigits: v >= 100 ? 0 : 2 });
const range = (lo: number | null, hi: number | null) => (lo == null ? '—' : hi == null || hi === lo ? fmt(lo) : `${fmt(lo)} – ${fmt(hi)}`);

async function exportXlsx(name: string, sheets: { name: string; rows: (string | number | null)[][] }[]) {
  const XLSX = await import('xlsx');
  const wb = XLSX.utils.book_new();
  for (const s of sheets) XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet(s.rows), s.name.slice(0, 30));
  XLSX.writeFile(wb, `${name || '表格'}.xlsx`);
}

// ── 材料成本估算 ─────────────────────────────────────
export function EstimateCard({ a }: { a: Estimate }) {
  const total = a.total.taxLow || 1;
  const exportIt = () => {
    const rows: (string | number | null)[][] = [['#', '分类', '材料 / 零件', '规格', '用量', '单位', '含税单价（低）', '含税单价（高）', '计价单位', '小计（低）', '小计（高）', '来源编号', '说明']];
    for (const g of a.groups) for (const l of g.lines)
      rows.push([l.i, g.name, l.name, l.spec, l.qty, l.qtyUnit, l.priceLow, l.priceHigh, l.priceUnit, l.subLow, l.subHigh, l.ref, l.why ?? l.note]);
    rows.push([], ['', '', '材料合计（含税）', '', '', '', '', '', '', a.total.taxLow, a.total.taxHigh]);
    rows.push(['', '', '材料合计（未税）', '', '', '', '', '', '', a.total.netLow, a.total.netHigh]);
    rows.push(['', '', '只含材料成本，不含人工、加工和利润']);
    void exportXlsx(a.title, [{ name: '材料成本估算', rows }]);
  };
  const lineStatus = (l: EstimateLine) => (l.status === 'ok' ? null : l.status === 'missing' ? '没查到' : l.status === 'unit' ? '单位不符' : '没给用量');
  return (
    <section className="dc-card">
      <div className="dc-hd">
        <div className="dc-hd-row">
          <b className="dc-title">{a.title}</b>
          {a.subtitle && <span className="dc-sub">{a.subtitle}</span>}
          <span className="dc-warn-pill">只含材料 · 不含人工、加工和利润</span>
        </div>
        <div className="est-kpis">
          <div className="est-kpi main"><span>材料合计（含税）</span><b>¥ {range(a.total.taxLow, a.total.taxHigh)}</b></div>
          <div className="est-kpi"><span>未税</span><b>¥ {range(a.total.netLow, a.total.netHigh)}</b></div>
          <div className="est-kpi"><span>已查到价格</span><b>{a.priced} / {a.count} 项</b></div>
          <div className="est-kpi"><span>价格日期</span><b className={a.stale ? 'warn' : ''}>{a.oldest ? `最早 ${a.oldest.slice(0, 7)}` : '—'}</b></div>
        </div>
      </div>
      <div className="dc-scroll">
        <table className="dc-table est-table">
          <thead>
            <tr><th className="c-i">#</th><th>材料 / 零件</th><th className="num">用量</th><th className="num">含税单价</th><th className="num">小计</th><th className="c-share">占材料成本</th><th className="c-ref">来源</th></tr>
          </thead>
          <tbody>
            {a.groups.map((g) => (
              <Fragment key={g.name}>
                <tr className="grp"><td colSpan={4}>{g.name}</td><td className="num" colSpan={1}>小计 <b>{range(g.subLow, g.subHigh)}</b></td><td colSpan={2} /></tr>
                {g.lines.map((l) => {
                  const st = lineStatus(l);
                  const pct = l.subLow != null ? (l.subLow / total) * 100 : null;
                  return (
                    <tr key={l.i} className={st ? 'miss' : ''}>
                      <td className="c-i">{l.i}</td>
                      <td>
                        <span className="cell-main">{l.name}</span>
                        <span className="cell-sub">{[l.spec, l.supplier, st ? l.why : l.note].filter(Boolean).join(' · ')}</span>
                      </td>
                      <td className="num">{l.qty != null ? `${fmt(l.qty)} ${l.qtyUnit}` : '—'}</td>
                      <td className="num">{st ?? <>{range(l.priceLow, l.priceHigh)}<span className="unit"> {l.priceUnit?.replace('元', '') ?? ''}</span></>}</td>
                      <td className="num strong">{range(l.subLow, l.subHigh)}</td>
                      <td className="c-share">
                        {pct != null ? (
                          <span className="share"><span className="bar"><i style={{ width: `${Math.min(100, pct)}%` }} /></span><span>{pct < 1 ? pct.toFixed(1) : Math.round(pct)}%</span></span>
                        ) : <span className="muted">—</span>}
                      </td>
                      <td className="c-ref">{l.ref != null ? <span className="ref-chip">{l.ref}</span> : <span className="ref-chip miss">?</span>}</td>
                    </tr>
                  );
                })}
              </Fragment>
            ))}
          </tbody>
          <tfoot>
            <tr><td /><td><b>材料合计（含税）</b></td><td /><td /><td className="num strong">{range(a.total.taxLow, a.total.taxHigh)}</td>
              <td colSpan={2} className="foot-note">{a.count - a.priced > 0 ? `${a.count - a.priced} 项没算进去` : ''}</td></tr>
          </tfoot>
        </table>
      </div>
      <div className="dc-ft">
        <span>合计由系统按价格库计算；用量是助手估的，请按图纸核对。{a.stale ? '用到的价格表有超过 12 个月没更新的，下单前找采购确认。' : ''}</span>
        <button type="button" onClick={exportIt}>导出 Excel</button>
      </div>
    </section>
  );
}

// ── 场景表格 ─────────────────────────────────────────
const STYLE: Record<Table['style'], [string, string]> = {
  matrix: ['供应商比价', 'b-blue'], trend: ['价格和走势', 'b-amber'], projects: ['历史项目', 'b-green'],
  compare: ['方案对比', 'b-purple'], spec: ['参数清单', 'b-teal'], plain: ['表格', 'b-gray'],
};
const NUMERIC_TYPES = new Set(['number', 'money', 'percent', 'delta']);
const num = (s: string) => {
  const m = /-?\d[\d,]*(\.\d+)?/.exec((s ?? '').replace(/−/g, '-'));
  return m ? Number(m[0].replace(/,/g, '')) : null;
};

function Spark({ text }: { text: string }) {
  const vals = text.split(/[,，\s]+/).map(Number).filter((v) => !Number.isNaN(v));
  if (vals.length < 2) return <span className="muted">—</span>;
  const min = Math.min(...vals), max = Math.max(...vals), span = max - min || 1;
  const pts = vals.map((v, i) => `${4 + (i * 72) / (vals.length - 1)},${18 - ((v - min) / span) * 14}`).join(' ');
  const up = vals[vals.length - 1] > vals[0], down = vals[vals.length - 1] < vals[0];
  return (
    <svg width="80" height="22" viewBox="0 0 80 22" aria-hidden="true">
      <polyline points={pts} fill="none" stroke={up ? 'var(--dc-up)' : down ? 'var(--dc-down)' : 'var(--dc-flat)'} strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}

function Cell({ v, type }: { v: string; type: string }) {
  const t = (v ?? '').trim();
  if (!t) return <span className="muted">—</span>;
  switch (type) {
    case 'delta': {
      const n = num(t);
      const up = t.startsWith('▲') || (n != null && n > 0 && !t.startsWith('▼'));
      const down = t.startsWith('▼') || (n != null && n < 0);
      return <span className={up ? 'd-up' : down ? 'd-down' : 'muted'}>{up && !t.startsWith('▲') ? '▲ ' : ''}{down && !t.startsWith('▼') ? '▼ ' : ''}{t.replace(/^[+-]/, '')}</span>;
    }
    case 'status': {
      const tone = /交付|完成|正常|通过|在用|满足/.test(t) ? 'ok' : /失败|停|驳回|不满足|超期/.test(t) ? 'bad' : 'mid';
      return <span className={'st-pill ' + tone}>{t}</span>;
    }
    case 'project':
      return <Link className="proj-link" to={`/projects/${encodeURIComponent(t)}`}>{t}</Link>;
    case 'ref':
      return <span className="refs-inline">{t.split(/[,，、\s[\]]+/).filter(Boolean).map((r) => <span key={r} className="ref-chip">{r}</span>)}</span>;
    case 'series':
      return <Spark text={t} />;
    case 'mark': {
      const m = /^[✓✔√]/.test(t) ? 'ok' : /^[✕✗×xX]/.test(t) ? 'bad' : /^[!！]/.test(t) ? 'mid' : null;
      return m ? <span className={'mark ' + m}><b>{t[0] === '√' || t[0] === '✔' ? '✓' : t[0] === '！' ? '!' : t[0]}</b>{t.slice(1).trim()}</span> : <>{t}</>;
    }
    default:
      return <>{t}</>;
  }
}

export function TableCard({ a }: { a: Table }) {
  const [label, tone] = STYLE[a.style] ?? STYLE.plain;
  const exportIt = () => void exportXlsx(a.title, [{ name: label, rows: [a.columns.map((c) => c.label), ...a.rows] }]);
  return (
    <section className="dc-card">
      <div className="dc-hd">
        <div className="dc-hd-row">
          <span className={'dc-badge ' + tone}>{label}</span>
          <b className="dc-title">{a.title}</b>
        </div>
      </div>
      {a.style === 'spec' ? <SpecGrid a={a} /> : <Grid a={a} />}
      <div className="dc-ft">
        <span>{a.note}</span>
        <button type="button" onClick={exportIt}>导出 Excel</button>
      </div>
    </section>
  );
}

function Grid({ a }: { a: Table }) {
  // 比价：每行在金额列里找最低价
  const moneyCols = a.columns.map((c, i) => (i > 0 && (c.type === 'money' || c.type === 'number') ? i : -1)).filter((i) => i >= 0);
  const minOf = (row: string[]) => {
    const vals = moneyCols.map((i) => num(row[i] ?? '')).filter((v): v is number => v != null);
    return vals.length >= 2 ? Math.min(...vals) : null;
  };
  const hl = a.style === 'compare' ? a.highlight : null;
  return (
    <div className="dc-scroll">
      <table className={'dc-table st-' + a.style}>
        <thead>
          <tr>
            {a.columns.map((c, i) => (
              <th key={i} className={(NUMERIC_TYPES.has(c.type) ? 'num' : '') + (hl === i ? ' rec' : '') + (a.style === 'matrix' && i > 0 ? ' sep' : '')}>
                {c.label}{hl === i && <span className="rec-tag">推荐</span>}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {a.rows.map((r, ri) => {
            const min = a.style === 'matrix' ? minOf(r) : null;
            return (
              <tr key={ri}>
                {a.columns.map((c, i) => {
                  const best = min != null && moneyCols.includes(i) && num(r[i] ?? '') === min;
                  return (
                    <td key={i} className={[NUMERIC_TYPES.has(c.type) ? 'num' : '', i === 0 ? 'first' : '', best ? 'best' : '', hl === i ? 'rec' : '',
                      a.style === 'matrix' && i > 0 ? 'sep' : ''].filter(Boolean).join(' ')}>
                      <Cell v={r[i] ?? ''} type={c.type} />
                      {best && <span className="best-tag">最低</span>}
                    </td>
                  );
                })}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/** 参数清单：每行「分组、名称、数值、来源编号」，按分组两两排开。 */
function SpecGrid({ a }: { a: Table }) {
  const groups = new Map<string, string[][]>();
  for (const r of a.rows) {
    const g = r.length >= 3 ? r[0] || '参数' : '参数';
    const row = r.length >= 3 ? r.slice(1) : r;
    groups.set(g, [...(groups.get(g) ?? []), row]);
  }
  return (
    <div>
      {[...groups.entries()].map(([g, rows]) => (
        <div key={g}>
          <div className="spec-grp">{g}</div>
          <dl className="spec-grid">
            {rows.map((r, i) => (
              <div key={i} className="spec-item">
                <dt>{r[0]}</dt>
                <dd>{r[1]}{r[2] && <span className="refs-inline">{r[2].split(/[,，、\s[\]]+/).filter(Boolean).map((x) => <span key={x} className="ref-chip">{x}</span>)}</span>}</dd>
              </div>
            ))}
          </dl>
        </div>
      ))}
    </div>
  );
}
