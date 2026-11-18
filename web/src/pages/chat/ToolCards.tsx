// 助手工具生成的卡片：请你确认（选项）、计算结果（代码/输出/图）、化学品数据，以及回答下面的导出条。
import { useState } from 'react';
import { ApiError, postDownload } from '../../lib/api';
import type { AskSpec, EvidenceCard, ToolArtifact } from '../../lib/types';

// ── 请你确认 ─────────────────────────────────────────
export function AskCard({ ask, active, onAnswer }: { ask: AskSpec; active: boolean; onAnswer: (text: string) => void }) {
  const [picked, setPicked] = useState<string[]>([]);
  const [own, setOwn] = useState('');
  const toggle = (l: string) => setPicked((p) => (ask.multiple ? (p.includes(l) ? p.filter((x) => x !== l) : [...p, l]) : [l]));
  const answer = own.trim() || picked.join('、');
  return (
    <div className={'ask-card' + (active ? '' : ' done')}>
      <b className="ask-q">{ask.question}</b>
      <div className="ask-opts" role={ask.multiple ? 'group' : 'radiogroup'} aria-label={ask.question}>
        {ask.options.map((o) => {
          const on = picked.includes(o.label) || (!active && ask.answered?.includes(o.label));
          return (
            <button key={o.label} type="button" className={'ask-opt' + (on ? ' on' : '')} disabled={!active}
              role={ask.multiple ? 'checkbox' : 'radio'} aria-checked={!!on} onClick={() => toggle(o.label)}>
              <span className={'ask-mark' + (ask.multiple ? ' sq' : '')} aria-hidden="true">{on ? (ask.multiple ? '✓' : '●') : ''}</span>
              <span className="ask-t"><b>{o.label}</b>{o.detail && <span>{o.detail}</span>}</span>
            </button>
          );
        })}
      </div>
      {active ? (
        <>
          <form className="ask-own" onSubmit={(e) => { e.preventDefault(); if (answer) onAnswer(answer); }}>
            <input className="inp" placeholder="都不是，我自己说…" value={own} onChange={(e) => setOwn(e.target.value)} />
            <button type="submit" className="pbtn" disabled={!answer}>确定</button>
          </form>
          <span className="hint">选完接着查，前面查到的不会重来。{ask.multiple ? '可以多选。' : ''}</span>
        </>
      ) : ask.answered && <span className="hint">↳ 你的回答：{ask.answered}</span>}
    </div>
  );
}

// ── 计算结果 ─────────────────────────────────────────
function CalcCard({ a }: { a: Extract<ToolArtifact, { kind: 'calc' }> }) {
  const [showCode, setShowCode] = useState(false);
  return (
    <section className="tool-card">
      <div className="tool-card-hd">
        <span className="tool-ic calc" aria-hidden="true">算</span>
        <b>计算结果</b>
        <span className="hint">Python · 运行 {a.seconds.toFixed(1)} 秒</span>
        <button type="button" className="tool-link" aria-expanded={showCode} onClick={() => setShowCode(!showCode)}>{showCode ? '收起代码 ▴' : '看代码 ▾'}</button>
      </div>
      {showCode && <pre className="tool-code">{a.code}</pre>}
      <div className="tool-card-body">
        {a.stdout && <pre className="tool-out">{a.stdout}</pre>}
        {a.error && <pre className="tool-out err">{a.error}</pre>}
        {a.images.map((src, i) => <img key={i} className="tool-img" src={src} alt={`计算生成的图 ${i + 1}`} />)}
      </div>
    </section>
  );
}

// ── 化学品数据 ───────────────────────────────────────
function ChemCard({ a }: { a: Extract<ToolArtifact, { kind: 'chem' }> }) {
  const props = Object.entries(a.props);
  return (
    <section className="tool-card">
      <div className="tool-card-hd">
        <span className="tool-ic chem" aria-hidden="true">化</span>
        <b>{a.title}{a.cas ? ` · CAS ${a.cas}` : ''}</b>
        {a.hazchem === true && <span className="tool-flag bad">列入《危险化学品目录》</span>}
        {a.hazchem === false && <span className="tool-flag">未列入本地危化品目录</span>}
        <a className="tool-link" href={a.url} target="_blank" rel="noopener noreferrer">PubChem ↗</a>
      </div>
      <div className="chem-grid">
        {a.formula && <div><span>分子式</span><b>{a.formula}{a.weight ? ` · ${a.weight}` : ''}</b></div>}
        {props.slice(0, 7).map(([k, v]) => <div key={k}><span>{k}</span><b title={v.join('；')}>{v[0]}</b></div>)}
      </div>
      {a.hazards.length > 0 && (
        <div className="chem-ghs">{a.hazards.map((h) => <span key={h}>{h}</span>)}</div>
      )}
      <div className="hint" style={{ padding: '0 16px 10px' }}>来自 PubChem 公开数据库（英文原文），用于选型参考前请核实。</div>
    </section>
  );
}

export function ArtifactCards({ items }: { items: ToolArtifact[] }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, margin: '4px 0 12px' }}>
      {items.map((a, i) => (a.kind === 'calc' ? <CalcCard key={i} a={a} /> : <ChemCard key={i} a={a} />))}
    </div>
  );
}

// ── 导出 ─────────────────────────────────────────────
/** 回答里的 Markdown 表格 → 二维数组。 */
function tablesOf(md: string): string[][][] {
  const lines = md.split('\n');
  const out: string[][][] = [];
  for (let i = 0; i < lines.length; i++) {
    if (!lines[i].trim().startsWith('|') || !/^\s*\|?\s*:?-{2,}/.test(lines[i + 1] ?? '')) continue;
    const rows: string[][] = [];
    const cells = (l: string) => l.trim().replace(/^\||\|$/g, '').split('|').map((c) => c.trim().replace(/\*\*/g, '').replace(/\[(\d+)\]/g, '[$1]'));
    rows.push(cells(lines[i]));
    i += 2;
    while (i < lines.length && lines[i].trim().startsWith('|')) rows.push(cells(lines[i++]));
    out.push(rows);
  }
  return out;
}

export function ExportBar({ text, title, sources }: { text: string; title: string; sources: EvidenceCard[] }) {
  const [msg, setMsg] = useState<string | null>(null);
  const tables = tablesOf(text);
  const srcLines = sources.map((c) => `[${c.n}] ${c.title}${c.pageNo != null ? ` 第 ${c.pageNo} 页` : ''}${c.url ? ` ${c.url}` : ''}`);
  const word = async () => {
    setMsg(null);
    try { await postDownload('/api/export/docx', { title, markdown: text, sources: srcLines }, `${title}.docx`); }
    catch (err) { setMsg(err instanceof ApiError ? err.message : '导出失败'); }
  };
  const excel = async () => {
    const XLSX = await import('xlsx');
    const wb = XLSX.utils.book_new();
    tables.forEach((t, i) => XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet(t), `表${i + 1}`));
    if (srcLines.length) XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet([['参考资料'], ...srcLines.map((s) => [s])]), '参考资料');
    XLSX.writeFile(wb, `${title}.xlsx`);
  };
  const copy = async () => { try { await navigator.clipboard.writeText(text); setMsg('已复制'); } catch { setMsg('复制失败，请手动选中复制'); } };
  return (
    <div className="export-bar">
      <button type="button" onClick={() => void word()}>导出 Word</button>
      {tables.length > 0 && <button type="button" onClick={() => void excel()}>导出 Excel（{tables.length} 个表）</button>}
      <button type="button" onClick={() => void copy()}>复制</button>
      {msg && <span className="hint">{msg}</span>}
    </div>
  );
}
