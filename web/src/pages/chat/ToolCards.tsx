// 助手工具生成的卡片：请你确认（选项）、计算结果（代码/输出/图）、化学品数据，以及回答下面的导出条。
import { useState } from 'react';
import { ApiError, download, postDownload } from '../../lib/api';
import type { AskSpec, EvidenceCard, HazardChemCard, HazardProcessCard, ToolArtifact } from '../../lib/types';
import { EstimateCard, TableCard } from './DataCards';
import { BlockCard } from './blocks/BlockCard';
import { usePreview } from '../../components/code/preview';
import { Icon } from '../../components/Icons';

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
              <span className={'ask-mark' + (ask.multiple ? ' sq' : '')} aria-hidden="true">{on && <Icon name={ask.multiple ? 'check' : 'dot'} size={ask.multiple ? 12 : 14} stroke={2.6} />}</span>
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

// ── 国家目录：重点监管危险化工工艺、危化品 ─────────────────
/** 危险性类别的颜色：燃爆类红、毒性腐蚀类橙、其余灰。只是看着醒目，不代表分级。 */
const hzTone = (c: string) => (/爆炸|易燃|自燃|氧化性|自反应|有机过氧化物|遇水放出/.test(c) ? 'r' : /毒性|腐蚀|致癌|致突变|生殖|致敏|吸入危害/.test(c) ? 'o' : 'g');

function HzProcess({ p }: { p: HazardProcessCard }) {
  const [more, setMore] = useState(false);
  return (
    <div className="hz-proc">
      <div className="hz-proc-hd">
        <b>重点监管危险化工工艺 · {p.name}</b><span className="ref-chip">{p.n}</span>
        <span className="hint">{p.reactionType} · 重点监控单元：{p.monitorUnits}</span>
      </div>
      <dl className="hz-kv">
        <dt>重点监控工艺参数</dt><dd>{p.params}</dd>
        <dt>安全控制的基本要求</dt><dd>{p.requirements}</dd>
        <dt>宜采用的控制方式</dt><dd>{p.controls}</dd>
        {more && <><dt>工艺危险特点</dt><dd>{p.hazards}</dd><dt>典型工艺</dt><dd>{p.typical}</dd></>}
      </dl>
      <div className="hz-foot">
        <button type="button" className="hm-link" onClick={() => setMore(!more)}>{more ? '收起' : '看危险特点、典型工艺'}</button>
        <span className="hint">来源：{p.source}{p.edited ? '（公司在后台补充过）' : '（原文）'}</span>
      </div>
    </div>
  );
}

function HzChem({ c }: { c: HazardChemCard }) {
  if (!c.found) {
    return (
      <div className="hz-chem miss">
        <b>{c.query}</b><span className="hint">《危险化学品目录》里没有这个名称{c.suggest?.length ? `；名字相近的：${c.suggest.join('、')}` : ''}</span>
      </div>
    );
  }
  return (
    <div className="hz-chem">
      <b title={c.aliases ? `别名：${c.aliases}` : undefined}>{c.name}{c.query !== c.name && <span className="hint">（{c.query}）</span>}</b>
      {c.n != null && <span className="ref-chip">{c.n}</span>}
      <span className="hz-tags">
        {c.listed ? <span className="hz r">危险化学品{c.seq ? ` · 第 ${c.seq} 号` : ''}</span> : <span className="hz g">不在危化品目录</span>}
        {c.toxic && <span className="hz r">剧毒</span>}
        {c.controlled?.map((x) => <span key={x} className="hz o">{x}</span>)}
        {c.classes?.map((x) => <span key={x} className={'hz ' + hzTone(x)}>{x}</span>)}
      </span>
      {c.cas && <span className="hint hz-cas">CAS {c.cas}</span>}
      {!!c.variants?.length && <span className="hint hz-var">{c.variants.map((v) => `${v.name}：${v.classes || '—'}`).join('；')}</span>}
      {c.note && <span className="hint hz-var">{c.note}</span>}
    </div>
  );
}

function HazardCard({ a }: { a: Extract<ToolArtifact, { kind: 'hazard' }> }) {
  return (
    <section className="tool-card">
      <div className="tool-card-hd">
        <span className="tool-ic hz" aria-hidden="true"><Icon name="shield" size={15} /></span>
        <b>国家目录</b>
        <span className="hint">本地数据：重点监管危险化工工艺、危险化学品目录（2015 版）及分类信息表、易制爆、易制毒</span>
      </div>
      <div className="tool-card-body">
        {a.processes.map((p) => <HzProcess key={p.no} p={p} />)}
        {a.chemicals.length > 0 && <div className="hz-chems">{a.chemicals.map((c) => <HzChem key={c.query} c={c} />)}</div>}
      </div>
    </section>
  );
}

// ── 已记住 / 已删掉（长期记忆）───────────────────────
function MemoryPill({ a }: { a: Extract<ToolArtifact, { kind: 'memory' }> }) {
  return (
    <div className="mem-pill">
      {a.saved.map((t) => <span key={'s' + t}><b>已记住</b>{t}</span>)}
      {a.removed.map((t) => <span key={'r' + t} className="rm"><b>已删掉</b>{t}</span>)}
      <a href="/profile#memory">管理我的记忆</a>
    </div>
  );
}

// ── 询价缺项单 ───────────────────────────────────────
const STATUS: Record<string, [string, string]> = { known: ['已知', 'known'], must: ['必须问', 'must'], should: ['建议问', 'should'] };

function formMarkdown(a: Extract<ToolArtifact, { kind: 'form' }>, vals: Record<string, string>) {
  const lines = [`# ${a.title}`, ''];
  for (const g of a.groups) {
    lines.push(`## ${g.name}`, '', '| 项目 | 内容 | 状态 | 为什么要问 |', '| --- | --- | --- | --- |');
    for (const it of g.items) {
      const k = `${g.name}/${it.field}`;
      lines.push(`| ${it.field} | ${(vals[k] ?? (it.status === 'known' ? it.value : '')).replace(/\|/g, '/')} | ${STATUS[it.status][0]} | ${it.why.replace(/\|/g, '/')} |`);
    }
    lines.push('');
  }
  return lines.join('\n');
}

function FormCard({ a, onSend }: { a: Extract<ToolArtifact, { kind: 'form' }>; onSend?: (text: string) => void }) {
  const [vals, setVals] = useState<Record<string, string>>({});
  const [msg, setMsg] = useState<string | null>(null);
  const filled = Object.values(vals).filter((v) => v.trim()).length;
  const send = () => {
    const parts: string[] = [];
    for (const g of a.groups) for (const it of g.items) {
      const v = vals[`${g.name}/${it.field}`]?.trim();
      if (v) parts.push(`${it.field}：${v}`);
    }
    if (parts.length && onSend) onSend(`缺项单补充的信息——${parts.join('；')}。请按这些接着选型。`);
  };
  const word = async () => {
    setMsg(null);
    try { await postDownload('/api/export/docx', { title: a.title, markdown: formMarkdown(a, vals), sources: [] }, `${a.title}.docx`); }
    catch (err) { setMsg(err instanceof ApiError ? err.message : '导出失败'); }
  };
  const excel = async () => {
    const XLSX = await import('xlsx');
    const rows = [['分组', '项目', '内容', '状态', '为什么要问']];
    for (const g of a.groups) for (const it of g.items)
      rows.push([g.name, it.field, vals[`${g.name}/${it.field}`] ?? (it.status === 'known' ? it.value : ''), STATUS[it.status][0], it.why]);
    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet(rows), '缺项单');
    XLSX.writeFile(wb, `${a.title}.xlsx`);
  };
  return (
    <section className="tool-card">
      <div className="tool-card-hd">
        <span className="tool-ic form" aria-hidden="true">单</span>
        <b>{a.title}</b>
        <span className="hint">已知 {a.known} 项 · 待确认 {a.missing} 项</span>
      </div>
      {a.groups.map((g) => (
        <div key={g.name} className="form-group">
          <div className="form-group-hd">{g.name}</div>
          {g.items.map((it) => {
            const k = `${g.name}/${it.field}`;
            return (
              <div key={k} className="form-row">
                <span className={'form-st ' + STATUS[it.status][1]}>{STATUS[it.status][0]}</span>
                <label className="form-f" htmlFor={'f-' + k}>{it.field}</label>
                {it.status === 'known'
                  ? <span className="form-v">{it.value}</span>
                  : <input id={'f-' + k} className="inp form-inp" placeholder={it.value || '客户回复后填在这里'} value={vals[k] ?? ''}
                      onChange={(e) => setVals({ ...vals, [k]: e.target.value })} />}
                {it.why && <span className="form-why">{it.why}</span>}
              </div>
            );
          })}
        </div>
      ))}
      <div className="export-bar" style={{ padding: '0 16px 12px' }}>
        {onSend && <button type="button" className="primary" disabled={filled === 0} onClick={send}>按已填的接着选型{filled ? `（${filled} 项）` : ''}</button>}
        <button type="button" onClick={() => void word()}>导出 Word 发给客户</button>
        <button type="button" onClick={() => void excel()}>导出 Excel</button>
        {msg && <span className="hint">{msg}</span>}
      </div>
    </section>
  );
}

// ── 专利 / 论文 ──────────────────────────────────────
function LitCard({ a }: { a: Extract<ToolArtifact, { kind: 'lit' }> }) {
  const [open, setOpen] = useState<number | null>(null);
  return (
    <section className="tool-card">
      <div className="tool-card-hd">
        <span className="tool-ic lit" aria-hidden="true">献</span>
        <b>文献检索</b>
        <span className="hint">「{a.query}」 · 专利 {a.patents} · 论文 {a.papers}</span>
      </div>
      {a.notes.length > 0 && <div className="hint" style={{ padding: '8px 16px 0' }}>{a.notes.join('；')}</div>}
      <div className="lit-list">
        {a.items.map((it) => (
          <div key={it.n} className="lit-row">
            <span className="refs-n">{it.n}</span>
            <div style={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 3 }}>
              <span><span className={'lit-kind' + (it.kind === '专利' ? ' pat' : '')}>{it.kind}</span>
                <a href={it.url} target="_blank" rel="noopener noreferrer" className="lit-t">{it.title}</a></span>
              <span className="hint">{it.meta}{it.fullText && <> · <a className="lit-ft" href={it.fullText} target="_blank" rel="noopener noreferrer">开放全文 ↗</a></>}</span>
              {it.abstract && (open === it.n
                ? <span className="lit-abs">{it.abstract}</span>
                : <button type="button" className="tool-link" style={{ marginLeft: 0, alignSelf: 'flex-start' }} onClick={() => setOpen(it.n)}>看摘要 ▾</button>)}
            </div>
          </div>
        ))}
      </div>
      <div className="hint" style={{ padding: '0 16px 10px' }}>来自 OpenAlex 和欧洲专利局公开数据，引用前请打开原文核实。</div>
    </section>
  );
}

// ── 看图 / 识别文字 ──────────────────────────────────
function ImageCard({ a }: { a: Extract<ToolArtifact, { kind: 'image' }> }) {
  return (
    <section className="tool-card">
      <div className="tool-card-hd">
        <span className="tool-ic img" aria-hidden="true">图</span>
        <b>{a.file}</b>
        <span className="hint">看图：{a.question}</span>
      </div>
      <div className="img-body">
        {a.thumb && <img src={a.thumb} alt={a.file} className="img-thumb" />}
        <div className="tool-out" style={{ fontFamily: 'inherit' }}>{a.answer}</div>
      </div>
      <div className="hint" style={{ padding: '0 16px 10px' }}>模型看图得出，位号和数字请对照原图。</div>
    </section>
  );
}

function OcrCard({ a }: { a: Extract<ToolArtifact, { kind: 'ocr' }> }) {
  const [msg, setMsg] = useState<string | null>(null);
  const copy = async () => { try { await navigator.clipboard.writeText(a.text); setMsg('已复制'); } catch { setMsg('复制失败'); } };
  return (
    <section className="tool-card">
      <div className="tool-card-hd">
        <span className="tool-ic img" aria-hidden="true">字</span>
        <b>识别出的文字 · {a.file}</b>
        <button type="button" className="tool-link" onClick={() => void copy()}>{msg ?? '复制全部'}</button>
      </div>
      <pre className="tool-out" style={{ padding: '12px 16px', fontFamily: 'inherit' }}>{a.text}</pre>
      <div className="hint" style={{ padding: '0 16px 10px' }}>{a.note}</div>
    </section>
  );
}

// ── 生成的文件（整份翻译）───────────────────────────
/** 助手读过的网页：点开在右侧用阅读模式看。 */
function WebPageCard({ a }: { a: Extract<ToolArtifact, { kind: 'webpage' }> }) {
  const open = usePreview();
  return (
    <section className="tool-card file-card">
      <span className="refs-ext" style={{ background: '#e8eef8', color: '#2a4a85' }} aria-hidden="true">网页</span>
      <span style={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
        <b>{a.title}</b><span className="hint">{a.host} · 已读，用了 {a.pieces} 段</span>
      </span>
      <button type="button" className="pbtn" style={{ marginLeft: 'auto' }}
        onClick={() => (open ? open({ kind: 'web', url: a.url, title: a.title }) : window.open(a.url, '_blank', 'noopener,noreferrer'))}>
        {open ? '在右侧打开' : '打开网页'}
      </button>
    </section>
  );
}

const FILE_TONE: Record<string, [string, string]> = {
  XLSX: ['#e6f2ec', '#1d6f42'], CSV: ['#e6f2ec', '#1d6f42'], DOCX: ['#e8eef8', '#2a4a85'], PPTX: ['#fbece4', '#b34a17'], PDF: ['#fde8ea', '#a00d25'],
};

function FileCard({ a }: { a: Extract<ToolArtifact, { kind: 'file' }> }) {
  const [msg, setMsg] = useState<string | null>(null);
  const open = usePreview();
  const ext = (a.title.split('.').pop() ?? '').toUpperCase().slice(0, 4) || 'DOCX';
  // 文件种类配色：Excel 绿、Word 蓝、PPT 橙、PDF 红
  const tone = FILE_TONE[ext] ?? FILE_TONE.DOCX;
  return (
    <section className="tool-card file-card">
      <span className="refs-ext" style={{ background: tone[0], color: tone[1] }} aria-hidden="true">{ext}</span>
      <span style={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
        <b>{a.title}</b>
        <span className="hint">{a.ok && !msg && <Icon name="check" size={12} stroke={2.4} style={{ verticalAlign: '-2px', marginRight: 3, color: '#1c6b45' }} />}{msg ?? a.detail}</span>
      </span>
      {open && <button type="button" className="gbtn" style={{ marginLeft: 'auto' }} onClick={() => open({ kind: 'file', src: a.url, fileName: a.title })}>预览</button>}
      <button type="button" className="pbtn" style={open ? undefined : { marginLeft: 'auto' }}
        onClick={() => void download(a.url, a.title).catch((e) => setMsg(e instanceof ApiError ? e.message : '下载失败'))}>下载</button>
    </section>
  );
}

export function ArtifactCards({ items, onSend }: { items: ToolArtifact[]; onSend?: (text: string) => void }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, margin: '4px 0 12px' }}>
      {items.map((a, i) => {
        switch (a.kind) {
          case 'calc': return <CalcCard key={i} a={a} />;
          case 'chem': return <ChemCard key={i} a={a} />;
          case 'hazard': return <HazardCard key={i} a={a} />;
          case 'memory': return <MemoryPill key={i} a={a} />;
          case 'form': return <FormCard key={i} a={a} onSend={onSend} />;
          case 'lit': return <LitCard key={i} a={a} />;
          case 'image': return <ImageCard key={i} a={a} />;
          case 'ocr': return <OcrCard key={i} a={a} />;
          case 'file': return <FileCard key={i} a={a} />;
          case 'estimate': return <EstimateCard key={i} a={a} />;
          case 'table': return <TableCard key={i} a={a} />;
          case 'block': return <BlockCard key={i} a={a} />;
          case 'webpage': return <WebPageCard key={i} a={a} />;
          default: return null;
        }
      })}
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
  const pdf = async () => {
    setMsg('正在生成 PDF…');
    try { await postDownload('/api/export/pdf', { title, markdown: text, sources: srcLines }, `${title}.pdf`); setMsg(null); }
    catch (err) { setMsg(err instanceof ApiError ? err.message : '导出失败'); }
  };
  const ppt = async () => {
    const { buildPpt } = await import('./pptExport');
    await buildPpt(title, text, srcLines);
  };
  return (
    <div className="export-bar">
      <button type="button" onClick={() => void word()}>导出 Word</button>
      <button type="button" onClick={() => void pdf()}>PDF</button>
      <button type="button" onClick={() => void ppt()}>PPT</button>
      {tables.length > 0 && <button type="button" onClick={() => void excel()}>导出 Excel（{tables.length} 个表）</button>}
      <button type="button" onClick={() => void copy()}>复制</button>
      {msg && <span className="hint">{msg}</span>}
    </div>
  );
}
