// 通用助手的三样东西：
// ① 「查了几步」——模型自己决定的每一步（搜什么、打开哪份、查了什么台账、算了什么），实时显示，可收起；
// ② 核对条——回答里的数字有没有在引用的原文里找到；
// ③ 右侧参考资料——只列这次真正用到的文件和原文切块，命中的句子标黄，和回答里的编号一一对应。
import { Fragment, useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import type { AssistantStep, EvidenceCard, VerifyReport } from '../../lib/types';
import { DocPreviewModal } from './ResultCards';

// ── ① 过程 ───────────────────────────────────────────
export function StepsCard({ steps, streaming, seconds }: { steps: AssistantStep[]; streaming?: boolean; seconds?: number | null }) {
  const [open, setOpen] = useState<boolean | null>(null);
  const expanded = open ?? !!streaming;
  if (steps.length === 0) return null;
  const running = steps.some((s) => s.status === 'running');
  return (
    <div className="as-steps">
      <button type="button" className="as-steps-hd" aria-expanded={expanded} onClick={() => setOpen(!expanded)}>
        <i className={'as-dot' + (running ? ' run' : '')} aria-hidden="true" />
        <b>{running ? '正在查' : `查了 ${steps.length} 步`}</b>
        {!running && seconds != null && <span className="as-muted">· 用时 {Math.round(seconds)} 秒</span>}
        {running && <span className="as-muted">· {steps[steps.length - 1].label}…</span>}
        <span className="as-muted" style={{ marginLeft: 'auto' }}>{expanded ? '收起 ▴' : '展开 ▾'}</span>
      </button>
      {expanded && steps.map((s) => (
        <div key={s.id} className={'as-step' + (s.status === 'error' ? ' err' : '')}>
          <span className="as-step-k">{s.label}</span>
          <span className="as-step-v">{s.detail || '—'}</span>
          <span className="as-step-r">{s.status === 'running' ? '…' : s.result ?? ''}</span>
        </div>
      ))}
    </div>
  );
}

// ── ② 核对 ───────────────────────────────────────────
export function VerifyBar({ report, onCite }: { report: VerifyReport; onCite: (n: number) => void }) {
  if (report.cited === 0 && report.checked === 0) return null;
  const ok = report.issues.length === 0 && report.missingRefs.length === 0;
  return (
    <div className="as-verify">
      <span className={'as-verify-s' + (ok ? ' ok' : '')}>
        {ok ? '✓ ' : ''}引用 {report.cited} 处
        {report.checked > 0 && `，${report.verified} / ${report.checked} 个数字已在原文中核实`}
      </span>
      {report.issues.map((i, k) => (
        <span key={k} className="as-verify-i" title={i.sentence}>
          <i aria-hidden="true" />「{i.value}」没在引用的原文
          {i.refs.map((n) => <button key={n} type="button" className="pcite" onClick={() => onCite(n)}>{n}</button>)}
          里找到，请核对
        </span>
      ))}
      {report.missingRefs.length > 0 && (
        <span className="as-verify-i"><i aria-hidden="true" />编号 {report.missingRefs.join('、')} 没有对应的资料</span>
      )}
    </div>
  );
}

// ── ③ 参考资料 ─────────────────────────────────────────
const EXT_COLOR: Record<string, [string, string]> = {
  PDF: ['#fde8ea', '#a00d25'], DOCX: ['#e8eef8', '#2a4a85'], DOC: ['#e8eef8', '#2a4a85'], 网页: ['#e8eef8', '#2a4a85'], 价格: ['#fbf0d9', '#7a4f00'],
  XLSX: ['#e6f2ec', '#1c6b45'], XLS: ['#e6f2ec', '#1c6b45'], PPTX: ['#fbf0d9', '#8a5a00'],
};

interface Group { key: string; kind: EvidenceCard['kind']; title: string; ext: string; meta: string; first: EvidenceCard; cards: EvidenceCard[] }

function groupCards(cards: EvidenceCard[]): Group[] {
  const map = new Map<string, Group>();
  for (const c of cards) {
    const key = c.kind === 'doc' ? `d:${c.docId}` : c.kind === 'ledger' ? 'ledger' : c.kind === 'web' ? `w:${c.url}`
      : c.kind === 'price' ? `p:${c.title.split(' · ')[0]}` : `a:${c.fileName}`;
    let g = map.get(key);
    if (!g) {
      const ext = c.kind === 'ledger' ? '台账' : c.kind === 'web' ? '网页' : c.kind === 'price' ? '价格' : (c.fileName?.split('.').pop() ?? '').toUpperCase().slice(0, 4) || '文档';
      g = {
        key, kind: c.kind, ext, first: c, cards: [],
        title: c.kind === 'ledger' ? '项目台账' : c.kind === 'price' ? c.title.split(' · ')[0] : c.title,
        meta: c.kind === 'ledger' ? '' : c.kind === 'attachment' ? '本对话上传的文件' : c.kind === 'price' ? '价格库 · 采购价'
          : c.kind === 'web' ? (c.url ? new URL(c.url).hostname : '') : [c.docCategory].filter(Boolean).join(' · '),
      };
      map.set(key, g);
    }
    g.cards.push(c);
  }
  for (const g of map.values()) if (g.kind === 'ledger') g.meta = `${g.cards.length} 条记录`;
  return [...map.values()];
}

/** 原文切块：高亮的句子包进 mark。 */
function Highlighted({ text, spans }: { text: string; spans: EvidenceCard['highlights'] }) {
  if (spans.length === 0) return <>{text}</>;
  const out: React.ReactNode[] = [];
  let last = 0;
  [...spans].sort((a, b) => a.start - b.start).forEach((s, i) => {
    if (s.start < last) return;
    out.push(<Fragment key={`t${i}`}>{text.slice(last, s.start)}</Fragment>);
    out.push(<mark key={`m${i}`}>{text.slice(s.start, s.start + s.length)}</mark>);
    last = s.start + s.length;
  });
  out.push(<Fragment key="end">{text.slice(last)}</Fragment>);
  return <>{out}</>;
}

export function RefPanel({ used, other, focus, onFocus, onClose, onOpenWeb }: {
  used: EvidenceCard[]; other: EvidenceCard[]; focus: number | null; onFocus: (n: number) => void; onClose: () => void;
  /** 网上的来源在预览面板里打开（阅读模式 + 标黄）；不给就新窗口打开 */
  onOpenWeb?: (n: number) => void;
}) {
  const nav = useNavigate();
  const [peek, setPeek] = useState<{ docId: string; title: string; page: number | null; find: string | null; note: string } | null>(null);
  const [showOther, setShowOther] = useState(false);
  const refs = useRef(new Map<number, HTMLDivElement>());
  // 公司资料在前，标准规范单独一组；网上查到的（含专利论文）再单独一组，标明需自行核实
  const isStd = (c: EvidenceCard) => c.kind === 'doc' && c.docCategory === '标准规范';
  const groups = groupCards(used.filter((c) => c.kind !== 'web' && c.kind !== 'price' && !isStd(c)));
  const priceGroups = groupCards(used.filter((c) => c.kind === 'price'));
  const stdGroups = groupCards(used.filter(isStd));
  const webGroups = groupCards(used.filter((c) => c.kind === 'web'));
  const sections = [
    { list: groups, label: '公司资料' }, { list: priceGroups, label: '价格库 · 采购价（仅有权限的人可见）' }, { list: stdGroups, label: '标准规范' }, { list: webGroups, label: '网上查到的 · 需自行核实' },
  ].filter((x) => x.list.length > 0);
  const flat = sections.flatMap((sec, si) => sec.list.map((g, i) => ({
    g,
    heading: i === 0 && (sections.length > 1 || si > 0) ? sec.label : null,
  })));
  const otherGroups = groupCards(other);

  useEffect(() => {
    if (focus != null) refs.current.get(focus)?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  }, [focus]);

  const open = (c: EvidenceCard) => {
    if (c.kind === 'ledger' && c.projectNo) { nav(`/projects/${encodeURIComponent(c.projectNo)}`); return; }
    if (c.kind === 'web' && c.url) { if (onOpenWeb) onOpenWeb(c.n); else window.open(c.url, '_blank', 'noopener,noreferrer'); return; }
    if (c.kind === 'price') { nav(`/prices?q=${encodeURIComponent(c.title.split(' · ').slice(1).join(' '))}`); return; }
    if (c.kind !== 'doc' || !c.docId) return;
    const hl = c.highlights[0];
    const find = hl ? c.text.slice(hl.start, hl.start + hl.length) : c.text.slice(0, 40);
    setPeek({ docId: c.docId, title: c.fileName ?? c.title, page: c.pageNo, find,
      note: c.pageNo != null ? `第 ${c.pageNo} 页` : '' });
  };

  return (
    <aside className="refs" aria-label="参考资料">
      <div className="refs-hd">
        <b>参考资料</b>
        <span className="as-muted">{[groups.length + stdGroups.length ? `${groups.length + stdGroups.length} 份` : '', webGroups.length ? `网页 ${webGroups.length} 个` : '', `${used.length} 段原文`].filter(Boolean).join(' · ')}</span>
        <button type="button" className="refs-x" aria-label="收起参考资料" onClick={onClose}>✕</button>
      </div>
      <div className="refs-body sc">
        {used.length === 0 && <div className="as-muted" style={{ padding: '6px 2px' }}>这次回答没有引用公司资料。</div>}
        {flat.map(({ g, heading: h }) => {
          const [bg, fg] = EXT_COLOR[g.ext] ?? ['#f1f1ef', '#4a4a4a'];
          const heading = h ? <div className="refs-sec">{h}</div> : null;
          return (
            <Fragment key={g.key}>{heading}<section className="refs-file">
              <div className="refs-file-hd">
                <span className="refs-ext" style={{ background: bg, color: fg }} aria-hidden="true">{g.ext}</span>
                <span className="refs-file-t">
                  <b title={g.title}>{g.title}</b>
                  {g.meta && <span>{g.meta}</span>}
                </span>
                {g.kind === 'doc' && <button type="button" className="refs-open" onClick={() => open(g.first)}>打开原文 ↗</button>}
                {g.kind === 'web' && <button type="button" className="refs-open" onClick={() => open(g.first)}>打开网页 ›</button>}
              </div>
              {g.cards.map((c) => (
                <div key={c.n} ref={(el) => { if (el) refs.current.set(c.n, el); else refs.current.delete(c.n); }}
                  className={'refs-chunk' + (focus === c.n ? ' on' : '')} role="button" tabIndex={0}
                  onClick={() => { onFocus(c.n); open(c); }}
                  onKeyDown={(e) => { if (e.key === 'Enter') { onFocus(c.n); open(c); } }}>
                  <span className="refs-n">{c.n}</span>
                  <div style={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 6 }}>
                    <span className="refs-where">
                      {c.kind === 'ledger' ? c.title : c.kind === 'price' ? c.title.split(' · ').slice(1).join(' · ') + (c.section ? ` · ${c.section}` : '')
                        : [c.pageNo != null ? `第 ${c.pageNo} 页` : null, c.section].filter(Boolean).join(' · ') || '原文'}
                      {(c.kind === 'doc' || c.kind === 'web' || (c.kind === 'ledger' && c.projectNo)) && <span className="refs-go">{c.kind === 'ledger' ? '看台账 ›' : c.kind === 'web' ? '打开网页 ›' : '跳到原文 ›'}</span>}
                    </span>
                    <span className="refs-text"><Highlighted text={c.text} spans={c.highlights} /></span>
                  </div>
                </div>
              ))}
            </section></Fragment>
          );
        })}
        {otherGroups.length > 0 && (
          <div className="refs-other">
            <button type="button" className="refs-other-hd" aria-expanded={showOther} onClick={() => setShowOther(!showOther)}>
              {showOther ? '▾' : '▸'} 其他相关资料 {otherGroups.length} 份<span className="as-muted">（查到了，这次回答没用上）</span>
            </button>
            {showOther && otherGroups.map((g) => (
              <div key={g.key} className="refs-other-row">
                <b>{g.title}</b>
                <span className="as-muted">{g.cards.map((c) => (c.pageNo != null ? `第 ${c.pageNo} 页` : c.section ?? '')).filter(Boolean).slice(0, 3).join('、')}</span>
                {g.kind === 'doc' && <button type="button" className="refs-open" onClick={() => open(g.first)}>打开</button>}
              </div>
            ))}
          </div>
        )}
      </div>
      {peek && (
        <DocPreviewModal src={`/api/files/${peek.docId}`} fileName={peek.title} page={peek.page} find={peek.find} note={peek.note}
          onClose={() => setPeek(null)} />
      )}
    </aside>
  );
}
