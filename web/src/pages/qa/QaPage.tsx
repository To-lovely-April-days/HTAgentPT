import { useCallback, useEffect, useRef, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, sse, download, ApiError } from '../../lib/api';
import { DELIVERY_LABEL } from '../../lib/types';
import type { LedgerTable, QaMessageRow, QaSessionRow, QaTemplateRec, Source, VocabRow } from '../../lib/types';
import { AuthImage, ClsBadge, ErrorBox, InfoBox, Spinner } from '../../components/Common';
import { Prose } from '../../components/Prose';
import type { Classification } from '../../lib/types';

// ── 一轮问答在界面上的形态（对应 SSE 事件契约）──────────────────────
interface Turn {
  id: string;
  question: string;
  intent?: string;
  rewrittenQuery?: string | null;
  notice?: string | null;
  answer: string;
  streaming: boolean;
  sources?: Source[];
  noResult?: { message: string; possiblyRelatedDocs: string[] };
  table?: LedgerTable;
  redirect?: { module: string; message: string; templates?: QaTemplateRec[] | null };
  error?: string;
  messageId?: string;
  helpful?: boolean | null;
}

export default function QaPage() {
  const qc = useQueryClient();
  // 「就本项目提问」跳转带来的预填条件（A3 → A1），只在进入时消费一次
  const preset = (useLocation().state ?? null) as
    | { presetCustomer?: string; presetDeviceType?: string; presetYear?: string }
    | null;
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [filters, setFilters] = useState({
    customerName: preset?.presetCustomer ?? '',
    year: preset?.presetYear ?? '',
    deviceType: preset?.presetDeviceType ?? '',
  });
  const [activeSources, setActiveSources] = useState<Source[] | null>(null);
  // 正文里点了 [n] 角标：右栏滚到那一条并短暂高亮
  const [focusSource, setFocusSource] = useState<number | null>(null);
  // 来源图片放大查看（FR-4.9 来源出图）
  const [lightbox, setLightbox] = useState<{ docId: string; id: number; caption: string | null; pageNo: number | null } | null>(null);
  const bottomRef = useRef<HTMLDivElement>(null);
  const showSources = useCallback((s: Source[], focus?: number) => {
    setActiveSources(s);
    setFocusSource(focus ?? null);
  }, []);

  const sessions = useQuery({ queryKey: ['qa-sessions'], queryFn: () => get<QaSessionRow[]>('/api/qa-sessions') });
  const customers = useQuery({ queryKey: ['vocab', 'customer_name'], queryFn: () => get<VocabRow[]>('/api/vocab/customer_name'), staleTime: 60_000 });
  const devices = useQuery({ queryKey: ['vocab', 'device_type'], queryFn: () => get<VocabRow[]>('/api/vocab/device_type'), staleTime: 60_000 });

  useEffect(() => { bottomRef.current?.scrollIntoView({ behavior: 'smooth' }); }, [turns]);

  const openSession = useCallback(async (id: string) => {
    setSessionId(id);
    const rows = await get<QaMessageRow[]>(`/api/qa-sessions/${id}`);
    setTurns(rows.map((m) => ({
      id: m.id, question: m.question, rewrittenQuery: m.rewrittenQuery,
      answer: m.answer ?? '', streaming: false, messageId: m.id, helpful: m.helpful,
      sources: m.sources ? (JSON.parse(m.sources) as Source[]) : undefined,
      noResult: m.noResultHints
        ? { message: '知识库中没有找到足以回答这个问题的内容。', possiblyRelatedDocs: JSON.parse(m.noResultHints) as string[] }
        : undefined,
    })));
    setActiveSources(null);
  }, []);

  const ask = useCallback(async (question: string, forcedIntent?: string) => {
    if (!question.trim() || busy) return;
    setBusy(true);
    const turnId = crypto.randomUUID();
    setTurns((t) => [...t, { id: turnId, question, answer: '', streaming: true }]);
    const patch = (p: Partial<Turn>) =>
      setTurns((t) => t.map((x) => (x.id === turnId ? { ...x, ...p } : x)));
    try {
      const body = {
        sessionId,
        question,
        forcedIntent: forcedIntent ?? null,
        filters: {
          query: question,
          customerName: filters.customerName || null,
          year: filters.year ? Number(filters.year) : null,
          deviceType: filters.deviceType || null,
        },
      };
      let answer = '';
      for await (const ev of sse('/api/chat/completions', body)) {
        const p = ev.payload as Record<string, unknown>;
        switch (ev.kind) {
          case 'meta':
            if (p.sessionId && !sessionId) setSessionId(p.sessionId as string);
            patch({ intent: p.intent as string, rewrittenQuery: p.rewrittenQuery as string, notice: (p.notice as string) ?? null });
            break;
          case 'delta':
            answer += p.text as string;
            patch({ answer });
            break;
          case 'sources': {
            const src = ev.payload as Source[];
            patch({ sources: src });
            setActiveSources(src);
            break;
          }
          case 'no_result':
            patch({ noResult: { message: p.message as string, possiblyRelatedDocs: (p.possiblyRelatedDocs as string[]) ?? [] }, notice: (p.notice as string) ?? null });
            break;
          case 'table':
            patch({ table: ev.payload as unknown as LedgerTable });
            break;
          case 'redirect':
            patch({ redirect: { module: p.module as string, message: p.message as string, templates: (p.templates as QaTemplateRec[] | null) ?? null } });
            break;
          case 'error':
            patch({ error: p.message as string });
            break;
          case 'done':
            patch({ messageId: p.messageId as string });
            break;
        }
      }
      patch({ streaming: false });
      void qc.invalidateQueries({ queryKey: ['qa-sessions'] });
    } catch (err) {
      patch({ streaming: false, error: err instanceof ApiError ? err.message : '连接中断，本轮问答未完成' });
    } finally {
      setBusy(false);
    }
  }, [busy, sessionId, filters, qc]);

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    const q = input.trim();
    if (!q) return;
    setInput('');
    void ask(q);
  };

  const feedback = async (t: Turn, helpful: boolean) => {
    if (!t.messageId) return;
    const reason = helpful ? null : window.prompt('这条回答哪里不对？（可留空）') ?? null;
    await post(`/api/qa-sessions/messages/${t.messageId}/feedback`, { helpful, reason });
    setTurns((x) => x.map((y) => (y.id === t.id ? { ...y, helpful } : y)));
  };

  return (
    <>
      {/* 左栏 240：会话历史 */}
      <aside style={{ width: 240, flexShrink: 0, background: 'var(--panel)', borderRight: '1px solid var(--line)', display: 'flex', flexDirection: 'column' }}>
        <div style={{ padding: '12px 12px 8px' }}>
          <button className="pbtn" style={{ width: '100%' }}
            onClick={() => { setSessionId(null); setTurns([]); setActiveSources(null); }}>新会话</button>
        </div>
        <div className="grp">会话历史</div>
        <div className="sc" style={{ flexGrow: 1, minHeight: 0, paddingBottom: 8 }}>
          {sessions.data?.map((s) => (
            <button key={s.id} className={'item' + (s.id === sessionId ? ' on' : '')}
              onClick={() => void openSession(s.id)}
              style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
              {s.title}
            </button>
          ))}
          {sessions.data?.length === 0 && <div className="hint" style={{ padding: '4px 18px' }}>还没有会话</div>}
        </div>
      </aside>

      {/* 中栏：对话流 */}
      <section style={{ flexGrow: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        <div style={{ height: 44, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '0 16px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
          <span style={{ fontSize: 11.5, color: 'var(--ink-3)' }}>筛选</span>
          <select className="inp" style={{ width: 150, height: 26 }} value={filters.customerName}
            onChange={(e) => setFilters((f) => ({ ...f, customerName: e.target.value }))}>
            <option value="">客户：全部</option>
            {customers.data?.filter((v) => v.isActive).map((v) => <option key={v.id} value={v.value}>{v.value}</option>)}
          </select>
          <select className="inp" style={{ width: 130, height: 26 }} value={filters.deviceType}
            onChange={(e) => setFilters((f) => ({ ...f, deviceType: e.target.value }))}>
            <option value="">设备类型：全部</option>
            {devices.data?.filter((v) => v.isActive).map((v) => <option key={v.id} value={v.value}>{v.value}</option>)}
          </select>
          <input className="inp" style={{ width: 90, height: 26 }} placeholder="年份" value={filters.year}
            onChange={(e) => setFilters((f) => ({ ...f, year: e.target.value.replace(/\D/g, '').slice(0, 4) }))} />
          <div style={{ flexGrow: 1 }} />
          <span className="hint">筛选与提问联合生效，只影响检索范围</span>
        </div>

        <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
          {turns.length === 0 && (
            <div style={{ maxWidth: 560, margin: '48px auto 0' }}>
              <div style={{ fontSize: 15, fontWeight: 600, marginBottom: 8 }}>向知识库提问</div>
              <div className="hint">
                回答只来自检索命中的文档，每个论断带来源。库里没有的内容会如实告诉你没有——不会硬答。
                项目类问题（如「2023 年给某客户做过哪些项目」）会自动转结构化台账查询。
              </div>
            </div>
          )}
          {turns.map((t) => <TurnView key={t.id} turn={t} onFeedback={feedback} onCorrect={(fi) => void ask(t.question, fi)} onShowSources={showSources} />)}
          <div ref={bottomRef} />
        </div>

        <form onSubmit={submit} style={{ flexShrink: 0, padding: '12px 22px 16px', background: 'var(--panel)', borderTop: '1px solid var(--line)' }}>
          <div style={{ display: 'flex', gap: 8 }}>
            <input name="question" value={input} onChange={(e) => setInput(e.target.value)} disabled={busy}
              placeholder="提问，回车发送——例如：CJF-5L 磁力耦合轴承的复装力矩是多少"
              style={{ flexGrow: 1, height: 38, padding: '0 12px', fontSize: 15, fontFamily: 'var(--font)', border: '1px solid var(--line-strong)', borderRadius: 5, outline: 'none' }} />
            <button className="pbtn" style={{ height: 38, padding: '0 18px' }} disabled={busy || !input.trim()}>
              {busy ? '回答中…' : '发送'}
            </button>
          </div>
        </form>
      </section>

      {/* 右栏 360：来源面板（FR-4.9） */}
      <aside className="sc" style={{ width: 360, flexShrink: 0, background: 'var(--panel)', borderLeft: '1px solid var(--line)', padding: '14px 14px 20px' }}>
        <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 9 }}>来源</div>
        {!activeSources && <div className="hint">回答生成后，这里列出每条依据的文档、章节与页码。</div>}
        {activeSources?.map((s) => (
          <div key={s.index} className="card"
            ref={s.index === focusSource ? (el) => el?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }) : undefined}
            style={{
              padding: '10px 12px', marginBottom: 8,
              ...(s.index === focusSource
                ? { borderColor: 'var(--accent)', boxShadow: '0 0 0 3px var(--accent-bg)' }
                : {}),
            }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 7, marginBottom: 5 }}>
              <span className="pill pill-accent m">[{s.index}]</span>
              <ClsBadge cls={s.classification as Classification} />
              <span className="m" style={{ fontSize: 11, color: 'var(--ink-3)', marginLeft: 'auto' }}>{s.score.toFixed(2)}</span>
            </div>
            <div style={{ fontSize: 12.5, fontWeight: 500, lineHeight: 1.5 }}>{s.docTitle}</div>
            <div className="hint" style={{ margin: '2px 0 6px' }}>
              {s.section ?? '—'}{s.pageNo != null ? ` · 第 ${s.pageNo} 页` : ''}
            </div>
            <div style={{ fontSize: 11.5, lineHeight: 1.7, color: 'var(--ink-2)' }}>{s.excerpt}…</div>
            {/* 来源页上的图（FR-4.9）：缩略内嵌，点开放大看题注与页码 */}
            {s.images && s.images.length > 0 && (
              <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginTop: 7 }}>
                {s.images.map((im) => (
                  <AuthImage key={im.id} src={`/api/files/${s.docId}/images/${im.id}`} alt={im.caption ?? ''} title={im.caption ?? undefined}
                    style={{ maxHeight: 86, maxWidth: 152, borderRadius: 4, border: '1px solid var(--line)', cursor: 'zoom-in', background: '#fff' }}
                    onClick={() => setLightbox({ docId: s.docId, id: im.id, caption: im.caption, pageNo: im.pageNo })} />
                ))}
              </div>
            )}
            <button
              onClick={() => void download(`/api/files/${s.docId}`, s.docTitle).catch((err) => alert(err instanceof ApiError ? err.message : '下载失败'))}
              style={{ fontSize: 11.5, marginTop: 6, padding: 0, border: 'none', background: 'none', color: 'var(--accent)', cursor: 'pointer', fontFamily: 'var(--font)' }}
            >下载原件 ↗</button>
          </div>
        ))}
      </aside>

      {lightbox && (
        <div onClick={() => setLightbox(null)}
          style={{ position: 'fixed', inset: 0, background: 'rgba(20,26,34,.72)', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', zIndex: 80, cursor: 'zoom-out', padding: 30 }}>
          <AuthImage src={`/api/files/${lightbox.docId}/images/${lightbox.id}`} alt={lightbox.caption ?? ''}
            style={{ maxWidth: '86vw', maxHeight: '78vh', borderRadius: 6, background: '#fff', boxShadow: '0 18px 60px rgba(0,0,0,.4)' }} />
          <div style={{ marginTop: 12, fontSize: 12.5, color: '#e8edf4', textAlign: 'center' }}>
            {lightbox.caption ?? '文档图片'}{lightbox.pageNo != null ? `　·　第 ${lightbox.pageNo} 页` : ''}　·　点击任意处关闭
          </div>
        </div>
      )}
    </>
  );
}

function TurnView({ turn: t, onFeedback, onCorrect, onShowSources }: {
  turn: Turn;
  onFeedback: (t: Turn, helpful: boolean) => void;
  onCorrect: (forcedIntent: string) => void;
  onShowSources: (s: Source[], focus?: number) => void;
}) {
  const nav = useNavigate();
  return (
    <div style={{ maxWidth: 760, margin: '0 auto 22px' }}>
      <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 8 }}>
        <div style={{ background: 'var(--accent-bg)', border: '1px solid var(--accent-line)', borderRadius: 6, padding: '8px 12px', fontSize: 15, maxWidth: '82%' }}>
          {t.question}
        </div>
      </div>

      {t.rewrittenQuery && t.rewrittenQuery !== t.question && (
        <div className="hint" style={{ marginBottom: 6 }}>已改写为：{t.rewrittenQuery}（改写结果供核对理解是否正确）</div>
      )}
      {t.notice && <div style={{ marginBottom: 8 }}><InfoBox>{t.notice}</InfoBox></div>}

      {t.table && <LedgerTableView table={t.table} onCorrect={onCorrect} />}
      {t.redirect && (
        <InfoBox>
          {t.redirect.message}
          {/* 生成意图：对话里直接给模板推荐卡，点选带着这句话进入对话式填写 */}
          {t.redirect.templates && t.redirect.templates.length > 0 && (
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginTop: 9 }}>
              {t.redirect.templates.map((tpl) => (
                <div key={tpl.id} style={{ width: 225, background: 'var(--panel)', border: '1px solid var(--line)', borderRadius: 5, padding: '10px 12px' }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 3 }}>
                    <span style={{ fontSize: 12.5, fontWeight: 600 }}>{tpl.name}</span>
                    <span className="pill pill-neutral">{tpl.docType}</span>
                  </div>
                  <div className="hint" style={{ marginBottom: 8 }}>{tpl.slotCount} 个待填项</div>
                  <button className="pbtn" style={{ height: 24, fontSize: 11.5, padding: '0 10px' }}
                    onClick={() => nav('/generate', { state: { templateId: tpl.id, question: t.question } })}>
                    用这个模板开聊
                  </button>
                </div>
              ))}
            </div>
          )}
          <div style={{ marginTop: 8 }}>
            <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => onCorrect('knowledge')}>判定有误，按知识问答回答</button>
          </div>
        </InfoBox>
      )}
      {t.noResult && (
        <InfoBox>
          {t.noResult.message}
          {t.noResult.possiblyRelatedDocs.length > 0 && (
            <div style={{ marginTop: 6 }}>
              可能相关的文档（供你判断）：{t.noResult.possiblyRelatedDocs.map((d) => `《${d}》`).join('、')}
            </div>
          )}
        </InfoBox>
      )}
      {t.error && <ErrorBox message={t.error} />}

      {(t.answer || t.streaming) && !t.table && !t.redirect && (
        <div className="card" style={{ padding: '12px 16px' }}>
          <Prose text={t.answer} style={{ fontSize: 14 }}
            onCite={t.sources ? (n) => onShowSources(t.sources!, n) : undefined} />
          {t.streaming && <div style={{ marginTop: 6 }}><Spinner text="生成中…" /></div>}
          {!t.streaming && t.sources && (
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 10, paddingTop: 10, borderTop: '1px solid var(--line-soft)' }}>
              <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => onShowSources(t.sources!)}>
                查看 {t.sources.length} 条来源
              </button>
              <div style={{ flexGrow: 1 }} />
              <span className="hint">这条回答</span>
              <button className="gbtn" style={{ height: 24, fontSize: 11.5, ...(t.helpful === true ? { color: 'var(--ok)', borderColor: 'var(--ok-line)' } : {}) }}
                onClick={() => onFeedback(t, true)}>有用</button>
              <button className="gbtn" style={{ height: 24, fontSize: 11.5, ...(t.helpful === false ? { color: 'var(--cls-conf)', borderColor: 'var(--cls-conf-line)' } : {}) }}
                onClick={() => onFeedback(t, false)}>无效</button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/** 台账意图的结构化表格（FR-4.1/3.4）：不经模型；金额列有没有由服务端定。
 * 点行进入项目详情（问答 › 台账查询结果 › 项目详情）。 */
function LedgerTableView({ table, onCorrect }: { table: LedgerTable; onCorrect: (fi: string) => void }) {
  const f = table.filters;
  const nav = useNavigate();
  return (
    <div className="card" style={{ overflow: 'hidden' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '9px 12px', borderBottom: '1px solid var(--line)', background: '#f8fafc' }}>
        <span className="pill pill-accent">台账查询</span>
        <span className="hint">
          解析条件：{[f.customer && `客户=${f.customer}`, f.deviceType && `设备=${f.deviceType}`,
            f.yearFrom && `${f.yearFrom}${f.yearTo && f.yearTo !== f.yearFrom ? `–${f.yearTo}` : ''} 年`]
            .filter(Boolean).join('，') || '无（返回最近记录）'}
        </span>
        <div style={{ flexGrow: 1 }} />
        <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => onCorrect('knowledge')}>不是查台账？按知识问答回答</button>
      </div>
      <table style={{ width: '100%', borderCollapse: 'collapse' }}>
        <thead>
          <tr>
            {['项目编号', '客户', '年份', '设备类型', '型号', ...(table.amountVisible ? ['合同金额'] : []), '交付状态'].map((h) => (
              <th key={h} style={{ fontSize: 11.5, fontWeight: 500, color: 'var(--ink-2)', textAlign: 'left', padding: '0 12px', height: 32, borderBottom: '1px solid var(--line)' }}>{h}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {table.rows.map((r) => (
            <tr key={r.projectNo} style={{ cursor: 'pointer' }} title="查看项目详情"
              onClick={() => nav(`/projects/${encodeURIComponent(r.projectNo)}`, { state: { from: 'qa' } })}>
              <td className="m" style={{ ...td, color: 'var(--accent)', fontWeight: 500 }}>{r.projectNo}</td>
              <td style={td}>{r.customerName}</td>
              <td className="m" style={td}>{r.year}</td>
              <td style={td}>{r.deviceType}</td>
              <td className="m" style={td}>{r.deviceModel ?? '—'}</td>
              {table.amountVisible && <td className="m" style={td}>{r.contractAmount != null ? r.contractAmount.toLocaleString() : '—'}</td>}
              <td style={td}>{r.deliveryStatus ? (DELIVERY_LABEL[r.deliveryStatus] ?? r.deliveryStatus) : '—'}</td>
            </tr>
          ))}
          {table.rows.length === 0 && (
            <tr><td colSpan={7} style={{ ...td, color: 'var(--ink-3)' }}>没有匹配的项目记录</td></tr>
          )}
        </tbody>
      </table>
      <div className="hint" style={{ padding: '8px 12px', borderTop: '1px solid var(--line-soft)' }}>{table.note}</div>
    </div>
  );
}

const td: React.CSSProperties = { fontSize: 12.5, padding: '0 12px', height: 38, borderBottom: '1px solid var(--line-soft)' };
