// 数据类代码块：CSV 变成可排序的表（可导出 Excel），JSON 变成可展开的树（格式错了说哪一行）。
import { useMemo, useState } from 'react';
import { CodeBlock } from './CodeBlock';
import { ViewFrame } from './Views';
import { jsonErrorLine, parseCsv } from './data';

const NUM = /^[-+]?[\d,]*\.?\d+(e[-+]?\d+)?%?$/i;
const toNum = (s: string) => Number(s.replace(/[,%]/g, ''));

export function CsvView({ code, lang }: { code: string; lang: string }) {
  const rows = useMemo(() => parseCsv(code, lang === 'tsv' ? '\t' : undefined), [code, lang]);
  const [sort, setSort] = useState<{ col: number; desc: boolean } | null>(null);
  const head = rows[0] ?? [];
  const body = rows.slice(1);
  const numeric = head.map((_, c) => body.length > 0 && body.every((r) => !r[c] || NUM.test(r[c])));
  const sorted = useMemo(() => {
    if (!sort) return body;
    const { col, desc } = sort;
    return [...body].sort((a, b) => {
      const x = a[col] ?? '', y = b[col] ?? '';
      const r = numeric[col] ? toNum(x) - toNum(y) : x.localeCompare(y, 'zh-CN');
      return desc ? -r : r;
    });
  }, [body, sort, numeric]);
  const excel = async () => {
    const { exportXlsx } = await import('../../pages/chat/blocks/util');
    await exportXlsx('数据', rows.map((r) => r.map((c, i) => (numeric[i] && c && rows[0] !== r ? toNum(c) : c))));
  };
  if (rows.length < 2) return <CodeBlock code={code} lang="csv" />;
  return (
    <ViewFrame label={`表格数据 · ${body.length} 行`} code={code} lang="csv"
      actions={<button type="button" className="cb-btn" onClick={() => void excel()}>导出 Excel</button>}>
      <div className="ptable-wrap rv-csv sc">
        <table className="ptable">
          <thead>
            <tr>{head.map((h, i) => (
              <th key={i} className={numeric[i] ? 'num' : undefined} aria-sort={sort?.col === i ? (sort.desc ? 'descending' : 'ascending') : 'none'}>
                <button type="button" className="th-sort" onClick={() => setSort(sort?.col === i ? { col: i, desc: !sort.desc } : { col: i, desc: false })}>
                  {h}<span aria-hidden="true">{sort?.col === i ? (sort.desc ? ' ▾' : ' ▴') : ''}</span>
                </button>
              </th>
            ))}</tr>
          </thead>
          <tbody>
            {sorted.map((r, i) => <tr key={i}>{head.map((_, c) => <td key={c} className={numeric[c] ? 'num' : undefined}>{r[c] ?? ''}</td>)}</tr>)}
          </tbody>
        </table>
      </div>
    </ViewFrame>
  );
}

type J = string | number | boolean | null | J[] | { [k: string]: J };

function Node({ k, v, depth }: { k: string | null; v: J; depth: number }) {
  const [open, setOpen] = useState(depth < 2);
  const key = k !== null ? <span className="jt-k">{k}: </span> : null;
  if (v === null || typeof v !== 'object') {
    const cls = v === null ? 'jt-null' : typeof v === 'string' ? 'jt-s' : typeof v === 'number' ? 'jt-n' : 'jt-b';
    return <div className="jt-row">{key}<span className={cls}>{typeof v === 'string' ? `"${v}"` : String(v)}</span></div>;
  }
  const entries: [string, J][] = Array.isArray(v) ? v.map((x, i) => [String(i), x]) : Object.entries(v);
  const brace = Array.isArray(v) ? ['[', ']'] : ['{', '}'];
  return (
    <div className="jt-row">
      <button type="button" className="jt-tg" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? '▾' : '▸'}</button>
      {key}<span className="jt-p">{brace[0]}</span>
      {!open && <span className="jt-more" onClick={() => setOpen(true)}>{entries.length} 项{brace[1]}</span>}
      {open && (
        <>
          <div className="jt-kids">{entries.map(([ck, cv]) => <Node key={ck} k={Array.isArray(v) ? null : ck} v={cv} depth={depth + 1} />)}</div>
          <span className="jt-p">{brace[1]}</span>
        </>
      )}
    </div>
  );
}

export function JsonView({ code }: { code: string }) {
  const parsed = useMemo(() => {
    try { return { ok: true as const, v: JSON.parse(code) as J }; } catch (e) { return { ok: false as const, line: jsonErrorLine(code, e) }; }
  }, [code]);
  if (!parsed.ok) {
    return (
      <ViewFrame label="JSON" code={code} lang="json" error={`格式有误${parsed.line ? `，在第 ${parsed.line} 行附近` : ''}，下面是原文`}>
        {null}
      </ViewFrame>
    );
  }
  return (
    <ViewFrame label="JSON" code={code} lang="json">
      <div className="jt sc"><Node k={null} v={parsed.v} depth={0} /></div>
    </ViewFrame>
  );
}
