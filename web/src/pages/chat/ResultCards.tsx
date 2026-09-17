// 对话里各类意图的结果卡。全部就地展示，不跳页——
// 用户说什么，结果就在同一条对话流里长出来。
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { download } from '../../lib/api';
import { DELIVERY_LABEL, TICKET_LABEL } from '../../lib/types';
import type {
  CaseDetailData, CaseRow, GenTranslated, LedgerTable, ProjectDetail, QaTemplateRec, Source,
  TextTranslationResult, TicketRow, TicketStatus,
} from '../../lib/types';
import { FileView } from '../../components/FileView';
import { Prose } from '../../components/Prose';
import { ProjectPreviewModal } from './ProjectPreviewModal';
import { ClsBadge } from '../../components/Common';
import type { Classification } from '../../lib/types';

/** 台账查询：结构化结果，不经模型生成（FR-3.4/4.1）。点行看项目档案。 */
export function LedgerCard({ table, onCorrect }: { table: LedgerTable; onCorrect: () => void }) {
  const nav = useNavigate();
  const [peek, setPeek] = useState<string | null>(null);
  if (table.detail) return <ProjectCard detail={table.detail} amountVisible={table.amountVisible} />;
  const f = table.filters;
  const cond = [f.customer && `客户=${f.customer}`, f.deviceType && `设备=${f.deviceType}`,
    f.yearFrom && `${f.yearFrom}${f.yearTo && f.yearTo !== f.yearFrom ? `–${f.yearTo}` : ''} 年`]
    .filter(Boolean).join('，') || '无（返回最近记录）';
  return (
    <div className="advc" style={{ borderColor: 'var(--line)' }}>
      <div className="advc-head" style={{ background: 'var(--bg-soft)' }}>
        <span className="advc-cap" style={{ color: 'var(--ink-2)' }}>项目台账</span>
        <span className="pill pill-neutral">{table.rows.length} 条</span>
        <span className="advc-note">解析条件：{cond}</span>
      </div>
      <div style={{ overflowX: 'auto', background: 'var(--panel)' }}>
        <table className="ltable">
          <thead><tr>
            {['项目编号', '客户', '年份', '设备型号', ...(table.amountVisible ? ['合同金额'] : []), '交付状态', ''].map((h, i) => (
              <th key={h || `x${i}`}>{h}</th>
            ))}
          </tr></thead>
          <tbody>
            {table.rows.map((r) => (
              <tr key={r.projectNo} onClick={() => nav(`/projects/${encodeURIComponent(r.projectNo)}`, { state: { from: 'chat' } })}
                title="打开项目档案">
                <td className="m" style={{ color: 'var(--accent)', fontWeight: 500 }}>{r.projectNo}</td>
                <td>{r.customerName}</td>
                <td className="m">{r.year}</td>
                <td className="m">{r.deviceModel ?? '—'}</td>
                {table.amountVisible && <td className="m">{r.contractAmount != null ? r.contractAmount.toLocaleString() : '—'}</td>}
                <td>{r.deliveryStatus ? (DELIVERY_LABEL[r.deliveryStatus] ?? r.deliveryStatus) : '—'}</td>
                <td onClick={(e) => { e.stopPropagation(); setPeek(r.projectNo); }}>
                  <span className="gbtn" style={{ height: 20, fontSize: 11, padding: '0 8px' }}>预览</span>
                </td>
              </tr>
            ))}
            {table.rows.length === 0 && <tr><td colSpan={7} style={{ color: 'var(--ink-3)' }}>没有匹配的项目记录</td></tr>}
          </tbody>
        </table>
      </div>
      <div className="advc-foot">
        <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={onCorrect}>不是查台账？按知识问答回答</button>
        <span className="hint" style={{ alignSelf: 'center' }}>点「预览」就地看原件，点行打开完整档案</span>
      </div>
      {peek && <ProjectPreviewModal projectNo={peek} onClose={() => setPeek(null)} />}
    </div>
  );
}

/** 就地翻译：默认对照，术语命中标出来（FR-6.1/6.5）。 */
export function TranslationCard({ data }: {
  data: TextTranslationResult & { direction: string; source: string | null; sourceText: string };
}) {
  const [side, setSide] = useState<'pair' | 'target'>('pair');
  const zh2en = data.direction === 'zh2en';
  return (
    <div className="advc" style={{ borderColor: 'var(--cls-pub-line)' }}>
      <div className="advc-head" style={{ background: 'var(--cls-pub-bg)' }}>
        <span className="advc-cap" style={{ color: 'var(--cls-pub)' }}>{zh2en ? '中译英' : '英译中'}</span>
        {data.source && <span className="pill pill-neutral">译自{data.source}</span>}
        {data.termsApplied.length > 0 && <span className="pill pill-pub">{data.termsApplied.length} 条术语已套用</span>}
        <span style={{ marginLeft: 'auto', display: 'flex', gap: 5 }}>
          <button className="gbtn" style={{ height: 21, fontSize: 11, padding: '0 8px' }}
            onClick={() => setSide(side === 'pair' ? 'target' : 'pair')}>
            {side === 'pair' ? '只看译文' : '看对照'}
          </button>
          <button className="gbtn" style={{ height: 21, fontSize: 11, padding: '0 8px' }}
            onClick={() => void navigator.clipboard?.writeText(data.translation)}>复制译文</button>
        </span>
      </div>
      {data.contractNotice && (
        <div style={{ padding: '7px 11px', background: 'var(--cls-int-bg)', borderTop: '1px solid var(--cls-int-line)',
          color: 'var(--cls-int)', fontSize: 11.5, lineHeight: 1.7 }}>{data.contractNotice}</div>
      )}
      <div style={{ background: 'var(--panel)', borderTop: '1px solid var(--line-soft)' }}>
        {side === 'target' ? (
          <div style={{ padding: '9px 12px', fontSize: 13, lineHeight: 1.85, whiteSpace: 'pre-wrap' }}>{data.translation}</div>
        ) : data.pairs.length > 0 ? (
          data.pairs.map((p, i) => (
            <div key={i} className="tpair">
              <div className="tpair-s">{p.source}</div>
              <div className="tpair-t">{p.target}</div>
            </div>
          ))
        ) : (
          <div style={{ padding: '9px 12px', fontSize: 13, lineHeight: 1.85, whiteSpace: 'pre-wrap' }}>{data.translation}</div>
        )}
      </div>
      {data.termsApplied.length > 0 && (
        <div className="advc-foot" style={{ flexWrap: 'wrap' }}>
          <span className="hint" style={{ alignSelf: 'center' }}>已套用术语</span>
          {data.termsApplied.slice(0, 8).map((t) => (
            <span key={t.zh + t.en} className="pill pill-pub">{t.zh} → {t.en}</span>
          ))}
        </div>
      )}
    </div>
  );
}

/** 故障案例：命中一条直接摊开，多条给列表。 */
export function CasesCard({ rows, detail, keyword, note }: {
  rows: CaseRow[]; detail: CaseDetailData | null; keyword: string; note: string;
}) {
  const nav = useNavigate();
  const [open, setOpen] = useState<CaseDetailData | null>(detail);
  return (
    <div className="advc" style={{ borderColor: 'var(--cls-conf-line)' }}>
      <div className="advc-head" style={{ background: 'var(--cls-conf-bg)' }}>
        <span className="advc-cap" style={{ color: 'var(--cls-conf)' }}>故障案例</span>
        <span className="pill pill-neutral">{rows.length} 条</span>
        <span className="advc-note">检索词：{keyword || '（全部）'}</span>
      </div>
      {open ? (
        <div style={{ background: 'var(--panel)', borderTop: '1px solid var(--line-soft)', padding: '10px 12px' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 7 }}>
            <span className="m" style={{ fontSize: 12, fontWeight: 600, color: 'var(--cls-conf)' }}>{open.caseNo}</span>
            <span className="pill pill-neutral">{open.deviceModel}</span>
            {open.alarmCode && <span className="pill pill-conf m">{open.alarmCode}</span>}
            <button className="gbtn" style={{ height: 21, fontSize: 11, padding: '0 8px', marginLeft: 'auto' }}
              onClick={() => nav(`/cases/${open.id}`, { state: { from: 'chat' } })}>打开完整档案</button>
            {rows.length > 1 && (
              <button className="gbtn" style={{ height: 21, fontSize: 11, padding: '0 8px' }}
                onClick={() => setOpen(null)}>回到列表</button>
            )}
          </div>
          <CaseSection title="故障现象" text={open.phenomenon} />
          <CaseSection title="原因判断" text={open.causeAnalysis} />
          <CaseSection title="处理步骤" text={open.steps} />
          {open.spareParts && <CaseSection title="所用备件" text={open.spareParts} />}
          <CaseSection title="处理结果" text={open.result} />
        </div>
      ) : (
        rows.map((r) => (
          <div key={r.id} className="advc-row">
            <span className="advc-bar" />
            <button type="button" className="advc-tog" onClick={() => void openCase(r.id, setOpen)}>
              <span className="m" style={{ flex: '0 0 auto', width: 92, fontSize: 11.5, color: 'var(--cls-conf)' }}>{r.caseNo}</span>
              <span className="advc-val">
                <b>{r.deviceModel}</b>{r.alarmCode ? ` · ${r.alarmCode}` : ''} — {r.phenomenon}
              </span>
            </button>
            <span className="advc-act">
              {r.sourceCompany && <span className="pill pill-pub">来自{r.sourceCompany}</span>}
            </span>
          </div>
        ))
      )}
      <div className="advc-foot"><span className="hint">{note}</span></div>
    </div>
  );
}

async function openCase(id: string, set: (d: CaseDetailData) => void) {
  const { get } = await import('../../lib/api');
  try { set(await get<CaseDetailData>(`/api/cases/${id}`)); } catch { /* 详情打不开就留在列表 */ }
}

function CaseSection({ title, text }: { title: string; text: string }) {
  return (
    <div style={{ marginBottom: 9 }}>
      <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--ink-3)', marginBottom: 2 }}>{title}</div>
      <div style={{ fontSize: 12.5, lineHeight: 1.8, whiteSpace: 'pre-wrap' }}>{text}</div>
    </div>
  );
}

/** 生成意图：把匹配的模板递到对话里，点一个就在这儿开始填。 */
export function TemplatePicker({ templates, onPick, busy }: {
  templates: QaTemplateRec[]; onPick: (t: QaTemplateRec) => void; busy: boolean;
}) {
  return (
    <div className="advc" style={{ borderColor: 'var(--accent-line)' }}>
      <div className="advc-head" style={{ background: 'var(--accent-bg)' }}>
        <span className="advc-cap" style={{ color: 'var(--accent)' }}>可用模板</span>
        <span className="pill pill-neutral">{templates.length} 个</span>
        <span className="advc-note">挑一个就在这儿开始填，不换页</span>
      </div>
      {templates.map((t) => (
        <div key={t.id} className="advc-row">
          <span className="advc-bar" />
          <button type="button" className="advc-tog" disabled={busy} onClick={() => onPick(t)}>
            <span className="advc-val"><b>{t.name}</b></span>
          </button>
          <span className="advc-act">
            <span className="pill pill-neutral">{t.docType}</span>
            <span className="hint">{t.slotCount} 项</span>
            <button className="pbtn" style={{ height: 22, fontSize: 11, padding: '0 9px' }}
              disabled={busy} onClick={() => onPick(t)}>用这个</button>
          </span>
        </div>
      ))}
    </div>
  );
}

/** 回答的依据：折起来，点开看每条出处与原文片段。 */
export function SourcesCard({ sources, focus }: { sources: Source[]; focus: number | null }) {
  const [open, setOpen] = useState(false);
  const show = open || focus != null;
  return (
    <div style={{ marginTop: 8 }}>
      <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => setOpen(!open)}>
        {show ? '收起依据' : `查看 ${sources.length} 条依据`}
      </button>
      {show && (
        <div style={{ marginTop: 7, display: 'flex', flexDirection: 'column', gap: 6 }}>
          {sources.map((s) => (
            <div key={s.index} className="card"
              ref={s.index === focus ? (el) => el?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }) : undefined}
              style={{
                padding: '8px 11px',
                ...(s.index === focus ? { borderColor: 'var(--accent)', boxShadow: '0 0 0 3px var(--accent-bg)' } : {}),
              }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 7, marginBottom: 4 }}>
                <span className="pill pill-accent m">{s.index}</span>
                <ClsBadge cls={s.classification as Classification} />
                <span style={{ fontSize: 12, fontWeight: 500 }}>{s.docTitle}</span>
                <span className="m" style={{ fontSize: 10.5, color: 'var(--ink-3)', marginLeft: 'auto' }}>{s.score.toFixed(2)}</span>
              </div>
              <div className="hint" style={{ marginBottom: 3 }}>
                {s.section ?? '—'}{s.pageNo != null ? ` · 第 ${s.pageNo} 页` : ''}
              </div>
              <div style={{ fontSize: 11.5, lineHeight: 1.7, color: 'var(--ink-2)' }}>{s.excerpt}…</div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/** 没有检索到依据时：说清楚为什么，不硬答（FR-4.8）。 */
export function NoResultCard({ message, docs }: { message: string; docs: string[] }) {
  return (
    <div style={{ marginTop: 8, padding: '9px 12px', borderRadius: 6, background: 'var(--cls-int-bg)',
      border: '1px solid var(--cls-int-line)', fontSize: 12, lineHeight: 1.8, color: 'var(--cls-int)' }}>
      <Prose text={message} style={{ fontSize: 12, color: 'inherit' }} />
      {docs.length > 0 && <div style={{ marginTop: 4 }}>可能相关的文档：{docs.map((d) => `《${d}》`).join('、')}</div>}
    </div>
  );
}

/** 项目档案：台账只命中一条时直接摊开，省掉再点一次。 */
export function ProjectCard({ detail, amountVisible }: { detail: ProjectDetail; amountVisible: boolean }) {
  const nav = useNavigate();
  const r = detail.row;
  const fields: [string, string][] = [
    ['客户', r.customerName],
    ['年份', String(r.year)],
    ['设备类型', r.deviceType],
    ['设备型号', r.deviceModel ?? '—'],
    ['规格参数', r.specParams ?? '—'],
    ...(amountVisible && r.contractAmount != null
      ? ([['合同金额', r.contractAmount.toLocaleString()]] as [string, string][]) : []),
    ['交付状态', r.deliveryStatus ? (DELIVERY_LABEL[r.deliveryStatus] ?? r.deliveryStatus) : '—'],
    ['负责人', r.ownerName ?? '—'],
  ];
  return (
    <div className="advc" style={{ borderColor: 'var(--accent-line)' }}>
      <div className="advc-head" style={{ background: 'var(--accent-bg)' }}>
        <span className="advc-cap m" style={{ color: 'var(--accent)' }}>{r.projectNo}</span>
        <span className="pill pill-neutral">{r.customerName}</span>
        <span className="advc-note">台账只命中这一条，档案直接摊开</span>
      </div>
      <div style={{ background: 'var(--panel)', borderTop: '1px solid var(--line-soft)' }}>
        {fields.map(([k, v]) => (
          <div key={k} className="sum-row"><span className="sum-name">{k}</span><span className="sum-val">{v}</span></div>
        ))}
      </div>
      {detail.documents.length > 0 && (
        <>
          <div className="sum-sec">项目资料<span className="sum-cnt">{detail.documents.length}</span></div>
          {detail.documents.map((d) => (
            <div key={d.docId} className="advc-row">
              <span className="advc-bar" />
              <button type="button" className="advc-tog"
                onClick={() => nav(`/admin/corpus/${d.docId}/preview`, { state: { from: 'chat' } })}>
                <span className="advc-val">{d.title}</span>
              </button>
              <span className="advc-act">
                <ClsBadge cls={d.classification} />
                <span className="hint">{d.docCategory}</span>
              </span>
            </div>
          ))}
        </>
      )}
      <div className="advc-foot">
        <button className="gbtn" style={{ height: 24, fontSize: 11.5 }}
          onClick={() => nav(`/projects/${encodeURIComponent(r.projectNo)}`, { state: { from: 'chat' } })}>
          打开完整档案
        </button>
      </div>
    </div>
  );
}

const TICKET_PILL: Record<TicketStatus, string> = {
  Submitted: 'pill-int', Assigned: 'pill-accent', InProgress: 'pill-accent',
  Resolved: 'pill-ok', Closed: 'pill-neutral',
};

/** 报修工单：一行一单，点开看流转记录（FR-8.8）。 */
export function TicketsCard({ rows, status, note }: {
  rows: TicketRow[]; status: string | null; note: string;
}) {
  const nav = useNavigate();
  const [open, setOpen] = useState<string | null>(rows.length === 1 ? rows[0].id : null);
  return (
    <div className="advc" style={{ borderColor: 'var(--cls-int-line)' }}>
      <div className="advc-head" style={{ background: 'var(--cls-int-bg)' }}>
        <span className="advc-cap" style={{ color: 'var(--cls-int)' }}>报修工单</span>
        <span className="pill pill-neutral">{rows.length} 条</span>
        {status && <span className="pill pill-int">{TICKET_LABEL[status as TicketStatus] ?? status}</span>}
        <span className="advc-note">点一条看流转记录</span>
      </div>
      {rows.map((t) => (
        <div key={t.id}>
          <div className={`advc-row${open === t.id ? ' open' : ''}`}>
            <span className="advc-bar" />
            <button type="button" className="advc-tog" aria-expanded={open === t.id}
              onClick={() => setOpen(open === t.id ? null : t.id)}>
              <span className="advc-tri">▶</span>
              <span className="m" style={{ flex: '0 0 auto', width: 104, fontSize: 11.5, color: 'var(--cls-int)' }}>{t.ticketNo}</span>
              <span className="advc-val"><b>{t.deviceNo}</b> — {t.description}</span>
            </button>
            <span className="advc-act">
              <span className={`pill ${TICKET_PILL[t.status] ?? 'pill-neutral'}`}>{TICKET_LABEL[t.status] ?? t.status}</span>
              {t.assigneeName && <span className="hint">{t.assigneeName}</span>}
            </span>
          </div>
          {open === t.id && (
            <div className="advc-body">
              <div style={{ marginBottom: 5 }}>联系方式：{t.contact}　·　客户号：{t.customerNo}</div>
              {t.trail.length === 0 ? <div className="hint">还没有流转记录。</div> : t.trail.map((e, i) => (
                <div key={i} style={{ display: 'flex', gap: 8, marginBottom: 2 }}>
                  <span className="m" style={{ flex: '0 0 auto', color: 'var(--ink-3)' }}>{e.at.slice(0, 16).replace('T', ' ')}</span>
                  <span className="pill pill-neutral">{TICKET_LABEL[e.status as TicketStatus] ?? e.status}</span>
                  <span style={{ minWidth: 0 }}>{e.by}{e.note ? `：${e.note}` : ''}</span>
                </div>
              ))}
            </div>
          )}
        </div>
      ))}
      <div className="advc-foot">
        <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => nav('/tickets', { state: { from: 'chat' } })}>
          去工单台改状态或派工
        </button>
        <span className="hint" style={{ alignSelf: 'center' }}>{note}</span>
      </div>
    </div>
  );
}

/** 基准候选：查到历史项目后递出来，点一张就按它的配置继承。 */
export function BaseCards({ items, active, onPick }: {
  items: { projectNo: string; customerName: string; year: number; deviceType: string; deviceModel: string | null; inheritableSlots: number }[];
  active: boolean; onPick: (projectNo: string | null) => void;
}) {
  // 拿它当基准之前先看一眼这单当时是怎么做的——光有编号和型号决定不了
  const [peek, setPeek] = useState<string | null>(null);
  return (
    <div className="advc" style={{ borderColor: 'var(--accent-line)' }}>
      <div className="advc-head" style={{ background: 'var(--accent-bg)' }}>
        <span className="advc-cap" style={{ color: 'var(--accent)' }}>可作基准的历史项目</span>
        <span className="pill pill-neutral">{items.length} 条</span>
        <span className="advc-note">选一条就按它的配置往当前单子里填，逐项可改</span>
      </div>
      {items.map((c) => (
        <div key={c.projectNo} className="advc-row">
          <span className="advc-bar" />
          <button type="button" className="advc-tog" disabled={!active} onClick={() => onPick(c.projectNo)}>
            <span className="m" style={{ flex: '0 0 auto', width: 132, fontSize: 11.5, color: 'var(--accent)', fontWeight: 500 }}>
              {c.projectNo}
            </span>
            <span className="advc-val">
              {c.customerName} · {c.year} · {c.deviceType}{c.deviceModel ? ` ${c.deviceModel}` : ''}
            </span>
          </button>
          <span className="advc-act">
            <span className="pill pill-ok">可继承 {c.inheritableSlots} 项</span>
            <button className="gbtn" style={{ height: 22, fontSize: 11, padding: '0 9px' }}
              onClick={() => setPeek(c.projectNo)}>预览</button>
            {active && <button className="pbtn" style={{ height: 22, fontSize: 11, padding: '0 9px' }}
              onClick={() => onPick(c.projectNo)}>用这个</button>}
          </span>
        </div>
      ))}
      {active && (
        <div className="advc-foot">
          <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => onPick(null)}>
            不用基准，逐项填
          </button>
        </div>
      )}
      {peek && (
        <ProjectPreviewModal projectNo={peek} onClose={() => setPeek(null)}
          onPick={active ? onPick : undefined} />
      )}
    </div>
  );
}

/** 一份文件的版式预览：不进语料库也能看，比如刚译出来的那一版。 */
export function DocPreviewModal({ src, fileName, onClose }: {
  src: string; fileName: string; onClose: () => void;
}) {
  useEffect(() => {
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', esc);
    return () => window.removeEventListener('keydown', esc);
  }, [onClose]);
  return (
    <div className="modal-mask" onClick={onClose}>
      <div className="modal-box pv" onClick={(e) => e.stopPropagation()}>
        <div className="pv-hd">
          <span style={{ fontSize: 13, fontWeight: 600 }}>{fileName}</span>
          <div style={{ flexGrow: 1 }} />
          <button className="pbtn" style={{ height: 26, fontSize: 12, padding: '0 12px' }}
            onClick={() => void download(src, fileName)}>下载 ↓</button>
          <button className="gbtn" style={{ height: 26, fontSize: 12 }} onClick={onClose}>关闭</button>
        </div>
        <div className="pv-body">
          <div className="pv-main"><FileView src={src} fileName={fileName} /></div>
        </div>
      </div>
    </div>
  );
}

/** 整篇译文（FR-6.3）：译出来的是一份排好版的文件，所以这里给的是「看原件版式」和「下载」，
    不是一段贴在对话里的文字。回填不了的地方、套用了哪些已审定术语，都摆在明处——
    译文能不能对外，得由人看着这两样决定（FR-6.6）。 */
export function TranslatedCard({ data }: { data: GenTranslated }) {
  const [peek, setPeek] = useState(false);
  const [more, setMore] = useState(false);
  const zh2en = data.direction === 'zh2en';
  const src = `/api/generate/translated/${data.taskId}.docx`;
  return (
    <div className="advc" style={{ borderColor: 'var(--cls-pub-line)', marginTop: 9 }}>
      <div className="advc-head" style={{ background: 'var(--cls-pub-bg)' }}>
        <span className="advc-cap" style={{ color: 'var(--cls-pub)' }}>{zh2en ? '英文版' : '中文版'}</span>
        <span style={{ fontSize: 12, color: 'var(--ink-2)' }}>{data.fileName}</span>
        <span className="pill pill-neutral">{data.translated}/{data.paragraphs} 段</span>
        {data.terms.length > 0 && <span className="pill pill-pub">{data.terms.length} 条术语已套用</span>}
        {data.unfillable.length > 0 && <span className="pill pill-int">{data.unfillable.length} 处未回填</span>}
        <span style={{ marginLeft: 'auto', display: 'flex', gap: 5 }}>
          <button className="gbtn" style={{ height: 21, fontSize: 11, padding: '0 8px' }}
            onClick={() => setPeek(true)}>看版式</button>
          <button className="pbtn" style={{ height: 21, fontSize: 11, padding: '0 10px' }}
            onClick={() => void download(src, data.fileName)}>下载 ↓</button>
        </span>
      </div>
      {data.notice && (
        <div style={{ padding: '7px 11px', background: 'var(--cls-int-bg)', borderTop: '1px solid var(--cls-int-line)',
          color: 'var(--cls-int)', fontSize: 11.5, lineHeight: 1.7 }}>{data.notice}</div>
      )}
      {data.draftBlank > 0 && (
        <div style={{ padding: '7px 11px', borderTop: '1px solid var(--line-soft)', fontSize: 11.5,
          color: 'var(--ink-2)', lineHeight: 1.7 }}>
          中文稿里还有 {data.draftBlank} 项留空，译文里同样是空的。补齐后说一声「再出一版英文的」即可。
        </div>
      )}
      {(data.terms.length > 0 || data.unfillable.length > 0) && (
        <div className="advc-foot" style={{ flexWrap: 'wrap' }}>
          <button className="gbtn" style={{ height: 21, fontSize: 11, padding: '0 8px' }}
            onClick={() => setMore(!more)}>{more ? '收起明细' : '看术语与未回填明细'}</button>
        </div>
      )}
      {more && (
        <div style={{ padding: '9px 12px', borderTop: '1px solid var(--line-soft)', background: 'var(--panel)' }}>
          {data.terms.length > 0 && (
            <>
              <div className="sum-sec" style={{ marginBottom: 6 }}>按已审定译法统一</div>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5, marginBottom: data.unfillable.length ? 10 : 0 }}>
                {data.terms.map((t) => (
                  <span key={t.zh + t.en} className="pill pill-pub">{t.zh} → {t.en}</span>
                ))}
              </div>
            </>
          )}
          {data.unfillable.length > 0 && (
            <>
              <div className="sum-sec" style={{ marginBottom: 6 }}>没能回填，仍是原文</div>
              <ul style={{ margin: 0, paddingLeft: 18, fontSize: 11.5, lineHeight: 1.8, color: 'var(--ink-2)' }}>
                {data.unfillable.map((u, i) => <li key={i}>{u}</li>)}
              </ul>
            </>
          )}
        </div>
      )}
      {peek && <DocPreviewModal src={src} fileName={data.fileName} onClose={() => setPeek(false)} />}
    </div>
  );
}
