// A4-A8 方案生成：不整篇写，以模板槽位为单位逐项填充（5.2）。
// 主体验是对话式填槽（整句多槽抽取/选项点选/跳过/汇总/生成），
// 「逐项核对」保留原工作台视图（基准出处、AI 建议依据、逐项确认都在那里）。
import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put, download, ApiError } from '../../lib/api';
import { SOURCE_LABEL, SOURCE_PILL_STYLE } from '../../lib/types';
import type {
  BaseCandidate, CompletenessView, GenAsk, GenChatMsg, GenChatPayload, GenChatProgress,
  GenChatStateView, GenChatTurnResult, PreviewView, RenderResult, SessionRowT, SessionView,
  SlotState, SlotSuggestion, SlotView, TemplateRowT,
} from '../../lib/types';
import { ErrorBox, InfoBox, Spinner } from '../../components/Common';

export default function GeneratePage() {
  // 从问答分流跳来：state 带模板与原话，落地即建会话开聊，原话作首轮输入自动抽取
  const loc = useLocation();
  const nav = useNavigate();
  const jump = (loc.state ?? null) as { templateId?: string; question?: string } | null;
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [mode, setMode] = useState<'chat' | 'form'>('chat');
  const [boot, setBoot] = useState<string | null>(null);
  const [jumpError, setJumpError] = useState<string | null>(null);

  useEffect(() => {
    if (!jump?.templateId) return;
    nav('.', { replace: true, state: null }); // 只消费一次，防返回重建
    void (async () => {
      try {
        const s = await post<SessionView>('/api/generate/sessions', {
          templateId: jump.templateId, projectHint: jump.question?.trim() || null,
        });
        setBoot(jump.question ?? null);
        setMode('chat');
        setSessionId(s.id);
      } catch (err) {
        setJumpError(err instanceof ApiError ? err.message : '按推荐模板创建会话失败');
      }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (!sessionId) return <StartView onOpen={(id) => { setBoot(null); setMode('chat'); setSessionId(id); }} outerError={jumpError} />;
  return mode === 'chat'
    ? <SessionChat key={sessionId} sessionId={sessionId} initialMessage={boot}
        onExit={() => setSessionId(null)} onWorkbench={() => setMode('form')} />
    : <SessionWorkbench sessionId={sessionId} onExit={() => setMode('chat')} />;
}

// ─── A4 模板选择 + A8 生成记录 ────────────────────────────────
function StartView({ onOpen, outerError }: { onOpen: (id: string) => void; outerError?: string | null }) {
  const [hint, setHint] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(outerError ?? null);
  const templates = useQuery({ queryKey: ['gen-templates'], queryFn: () => get<TemplateRowT[]>('/api/templates') });
  const sessions = useQuery({ queryKey: ['gen-sessions'], queryFn: () => get<SessionRowT[]>('/api/generate/sessions') });

  const create = async (templateId: string) => {
    setBusy(true);
    setError(null);
    try {
      const s = await post<SessionView>('/api/generate/sessions', { templateId, projectHint: hint.trim() || null });
      onOpen(s.id);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '创建失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '20px 24px' }}>
      <div style={{ maxWidth: 980 }}>
        <div style={{ fontSize: 15, fontWeight: 600, marginBottom: 4 }}>方案生成</div>
        <div className="hint" style={{ marginBottom: 16, lineHeight: 1.7 }}>
          不是整篇生成：选模板后进入对话式填写——整句描述项目，能确定的项自动填入，其余逐项提问；
          也可随时切到「逐项核对」工作台看基准出处与 AI 建议依据。模型只提供带依据的建议，不代为决定。
        </div>

        <div style={{ marginBottom: 12 }}>
          <div style={{ fontSize: 11.5, color: 'var(--ink-2)', marginBottom: 5 }}>项目要点（供检索基准项目，可留空）</div>
          <input
            value={hint} onChange={(e) => setHint(e.target.value)}
            placeholder="如：华东理工 10L 高压反应釜 防爆"
            style={{ width: '100%', maxWidth: 560, height: 30, padding: '0 10px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' }}
          />
        </div>
        {error && <div style={{ marginBottom: 12 }}><ErrorBox message={error} /></div>}

        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 12, marginBottom: 26 }}>
          {(templates.data ?? []).filter((t) => t.isEnabled).map((t) => (
            <div key={t.id} className="card" style={{ width: 300, padding: '14px 16px' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 5 }}>
                <span style={{ fontSize: 13, fontWeight: 600 }}>{t.name}</span>
                <span className="pill pill-neutral">{t.docType}</span>
              </div>
              <div className="hint" style={{ marginBottom: 10 }}>{t.slotCount} 个槽位{t.lastUsedAt ? ` · 最近使用 ${t.lastUsedAt.slice(0, 10)}` : ''}</div>
              <button className="pbtn" style={{ height: 28, padding: '0 14px' }} disabled={busy} onClick={() => void create(t.id)}>用此模板开始</button>
            </div>
          ))}
          {templates.data && templates.data.filter((t) => t.isEnabled).length === 0 && (
            <InfoBox>还没有已启用的模板。请管理员在「模板管理」上传并启用模板（槽位定义不完整的模板不可启用）。</InfoBox>
          )}
        </div>

        <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 8 }}>生成记录与草稿</div>
        <div className="hint" style={{ marginBottom: 10 }}>每条记录是可重放的快照：模板、基准项目、全部槽位值与来源（FR-5.17）。点击继续编辑。</div>
        {(sessions.data ?? []).length === 0 && <div className="hint">还没有生成记录。</div>}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 7, maxWidth: 640 }}>
          {(sessions.data ?? []).map((s) => (
            <div key={s.id} className="card" style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '10px 14px', cursor: 'pointer' }} onClick={() => onOpen(s.id)}>
              <div style={{ flexGrow: 1, minWidth: 0 }}>
                <div style={{ fontSize: 12.5, fontWeight: 500 }}>{s.templateName}</div>
                <div className="hint" style={{ marginTop: 2 }}>
                  {s.baseProjectNo ? `基准 ${s.baseProjectNo} · ` : ''}{s.updatedAt.slice(0, 16).replace('T', ' ')}
                </div>
              </div>
              <span className="m" style={{ fontSize: 11.5, color: 'var(--ink-2)' }}>{s.done}/{s.total}</span>
              <span className="pill" style={s.status === 'Completed'
                ? { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' }
                : { background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' }}>
                {s.status === 'Completed' ? '已输出' : '草稿'}
              </span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

// ─── 对话式填槽（A4 聊天形态）──────────────────────────────────
function SessionChat({ sessionId, initialMessage, onExit, onWorkbench }: {
  sessionId: string; initialMessage: string | null; onExit: () => void; onWorkbench: () => void;
}) {
  const qc = useQueryClient();
  const [msgs, setMsgs] = useState<GenChatMsg[]>([]);
  const [progress, setProgress] = useState<GenChatProgress | null>(null);
  const [templateName, setTemplateName] = useState('');
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const bottomRef = useRef<HTMLDivElement>(null);
  const booted = useRef(false);

  useEffect(() => { bottomRef.current?.scrollIntoView({ behavior: 'smooth' }); }, [msgs, busy]);

  const turn = useCallback(async (body: Record<string, unknown>) => {
    setBusy(true);
    setErr(null);
    try {
      const r = await post<GenChatTurnResult>(`/api/generate/sessions/${sessionId}/conversation`, body);
      setMsgs((m) => [...m, ...r.newMessages]);
      setProgress(r.progress);
      // 工作台与记录列表跟着对话即时刷新
      void qc.invalidateQueries({ queryKey: ['gen-session', sessionId] });
      void qc.invalidateQueries({ queryKey: ['gen-sessions'] });
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : '发送失败，请重试');
    } finally {
      setBusy(false);
    }
  }, [sessionId, qc]);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const [st, sv] = await Promise.all([
          get<GenChatStateView>(`/api/generate/sessions/${sessionId}/conversation`),
          get<SessionView>(`/api/generate/sessions/${sessionId}`),
        ]);
        if (cancelled) return;
        setMsgs(st.messages);
        setProgress(st.progress);
        setTemplateName(sv.templateName);
        if (st.messages.length === 0 && !booted.current) {
          booted.current = true; // StrictMode 双跑防重
          await turn({ start: true, message: initialMessage });
        }
      } catch (e) {
        if (!cancelled) setErr(e instanceof ApiError ? e.message : '载入对话失败');
      }
    })();
    return () => { cancelled = true; };
  }, [sessionId, initialMessage, turn]);

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    const text = input.trim();
    if (!text || busy) return;
    setInput('');
    void turn({ message: text });
  };

  const lastAssistantId = [...msgs].reverse().find((m) => m.role === 'assistant')?.id;
  const done = progress?.done ?? 0;
  const total = progress?.total ?? 0;

  return (
    <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minHeight: 0, minWidth: 0 }}>
      <div style={{ flexShrink: 0, padding: '10px 18px 9px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 8 }}>
          <button className="gbtn" style={{ height: 26, padding: '0 9px' }} onClick={onExit}>←</button>
          <span style={{ fontSize: 13.5, fontWeight: 600 }}>{templateName || '方案生成对话'}</span>
          <span className="pill pill-neutral">对话填写</span>
          <div style={{ flexGrow: 1 }} />
          <span className="hint">已完成 <span className="m">{done}</span> / {total}</span>
          {progress?.outputFileName && (
            <button className="gbtn" onClick={() => void download(`/api/generate/sessions/${sessionId}/output`, progress.outputFileName!)
              .catch((e2) => setErr(e2 instanceof ApiError ? e2.message : '下载失败'))}>下载 Word ↓</button>
          )}
          <button className="gbtn" onClick={onWorkbench}>逐项核对</button>
          {progress?.canRender && (
            <button className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={busy}
              onClick={() => void turn({ render: true })}>生成文档</button>
          )}
        </div>
        <div style={{ height: 4, borderRadius: 2, background: '#eef1f5', overflow: 'hidden' }}>
          <div style={{ width: `${(done / Math.max(1, total)) * 100}%`, background: '#1c6b45', height: '100%' }} />
        </div>
      </div>

      <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '16px 20px 20px' }}>
        <div style={{ maxWidth: 760, margin: '0 auto' }}>
          {msgs.map((m) => m.role === 'user' ? (
            <div key={m.id} style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 10 }}>
              <div style={{ maxWidth: '82%', padding: '8px 12px', borderRadius: 6, fontSize: 13, lineHeight: 1.7, whiteSpace: 'pre-wrap', background: 'var(--accent-bg)', border: '1px solid var(--accent-line)' }}>
                {m.content}
              </div>
            </div>
          ) : (
            <AssistantChatMsg key={m.id} msg={m} active={m.id === lastAssistantId && !busy}
              sessionId={sessionId} onTurn={turn} onError={setErr} />
          ))}
          {busy && <Spinner text="思考中…" />}
          {err && <div style={{ marginTop: 8 }}><ErrorBox message={err} /></div>}
          <div ref={bottomRef} />
        </div>
      </div>

      <form onSubmit={submit} style={{ flexShrink: 0, padding: '10px 20px 14px', background: 'var(--panel)', borderTop: '1px solid var(--line)' }}>
        <div style={{ maxWidth: 760, margin: '0 auto' }}>
          <div style={{ display: 'flex', gap: 6, marginBottom: 8, flexWrap: 'wrap' }}>
            {([['查历史项目', '查一下历史做过的项目'], ['推荐参数', '推荐一下当前这几项的参数'], ['跳过', '跳过'], ['汇总', '汇总'], ['生成文档', '生成文档']] as const).map(([label, msg]) => (
              <button key={label} type="button" className="gbtn" style={{ height: 24, fontSize: 11.5 }} disabled={busy}
                onClick={() => void turn({ message: msg })}>{label}</button>
            ))}
            <span className="hint" style={{ alignSelf: 'center', marginLeft: 6 }}>
              整句描述、查历史、要建议、提问都行，一句话里可以混着说
            </span>
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <input value={input} onChange={(e) => setInput(e.target.value)} disabled={busy}
              placeholder="回答问题或整句描述项目，回车发送"
              style={{ flexGrow: 1, height: 36, padding: '0 12px', fontSize: 14, fontFamily: 'var(--font)', border: '1px solid var(--line-strong)', borderRadius: 5, outline: 'none' }} />
            <button className="pbtn" style={{ height: 36, padding: '0 16px' }} disabled={busy || !input.trim()}>发送</button>
          </div>
        </div>
      </form>
    </div>
  );
}

/** 助手消息：正文 + payload 交互件（选项按钮/基准候选/汇总表/下载）。按钮只在最新一条上可点。 */
function AssistantChatMsg({ msg, active, sessionId, onTurn, onError }: {
  msg: GenChatMsg; active: boolean; sessionId: string;
  onTurn: (body: Record<string, unknown>) => Promise<void>; onError: (m: string) => void;
}) {
  let payload: GenChatPayload = {};
  try { payload = msg.payload ? (JSON.parse(msg.payload) as GenChatPayload) : {}; } catch { /* 老数据容错 */ }
  const choiceAsks = (payload.asks ?? []).filter((a): a is GenAsk & { choices: string[] } => !!a.choices && a.choices.length > 0);

  return (
    <div style={{ display: 'flex', justifyContent: 'flex-start', marginBottom: 10 }}>
      <div className="card" style={{ maxWidth: '88%', padding: '10px 14px' }}>
        <div style={{ fontSize: 13, lineHeight: 1.8, whiteSpace: 'pre-wrap' }}>{msg.content}</div>

        {payload.baseCandidates && payload.baseCandidates.length > 0 && (
          <div style={{ marginTop: 9 }}>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              {payload.baseCandidates.map((c) => (
                <div key={c.projectNo} style={{ width: 225, border: '1px solid var(--line)', borderRadius: 5, padding: '9px 11px' }}>
                  <div className="m" style={{ fontSize: 12, fontWeight: 600, color: 'var(--accent)' }}>{c.projectNo}</div>
                  <div style={{ fontSize: 12, margin: '2px 0' }}>{c.customerName} · {c.year}</div>
                  <div className="hint" style={{ marginBottom: 6 }}>{c.deviceType}{c.deviceModel ? ` ${c.deviceModel}` : ''} · 可继承 {c.inheritableSlots} 项</div>
                  {active && <button className="pbtn" style={{ height: 22, fontSize: 11, padding: '0 9px' }}
                    onClick={() => void onTurn({ baseProjectNo: c.projectNo })}>用这个基准</button>}
                </div>
              ))}
            </div>
            {active && <button className="gbtn" style={{ height: 24, fontSize: 11.5, marginTop: 7 }}
              onClick={() => void onTurn({ baseProjectNo: '' })}>不用基准，直接开始</button>}
          </div>
        )}

        {/* 参数顾问的建议：带依据、待确认；采纳才落表（FR-5.9/5.13） */}
        {payload.suggestions && payload.suggestions.length > 0 && (
          <div style={{ marginTop: 9, padding: '9px 11px', borderRadius: 5, background: '#fbeff7', border: '1px solid #edd2e3' }}>
            {payload.suggestions.map((s) => (
              <div key={s.tag} style={{ marginBottom: 7 }}>
                <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
                  <span style={{ fontSize: 11.5, fontWeight: 600, color: '#8f3d74' }}>{s.name}</span>
                  <span style={{ fontSize: 12.5, whiteSpace: 'pre-wrap' }}>{s.value}</span>
                  {active && <button className="pbtn" style={{ height: 22, fontSize: 11, padding: '0 9px' }}
                    onClick={() => void onTurn({ adoptTags: [s.tag] })}>采纳</button>}
                </div>
                {s.evidence.map((e, i) => (
                  <div key={i} style={{ fontSize: 11, lineHeight: 1.6, color: 'var(--ink-2)' }}>
                    <span className="m" style={{ color: '#8f3d74' }}>[{i + 1}]</span> {e.sourceTitle}{e.section ? ` › ${e.section}` : ''}{e.pageNo != null ? ` · 第 ${e.pageNo} 页` : ''}
                  </div>
                ))}
              </div>
            ))}
            {active && payload.suggestions.length > 1 && (
              <button className="gbtn" style={{ height: 24, fontSize: 11.5 }}
                onClick={() => void onTurn({ adoptTags: payload.suggestions!.map((s) => s.tag) })}>全部采纳</button>
            )}
          </div>
        )}

        {active && choiceAsks.length > 0 && (
          <div style={{ marginTop: 9, display: 'flex', flexDirection: 'column', gap: 6 }}>
            {choiceAsks.map((a) => (
              <div key={a.tag} style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
                <span style={{ fontSize: 11.5, color: 'var(--ink-2)', flexShrink: 0 }}>{a.name}：</span>
                {a.choices.map((c) => (
                  <button key={c} className="gbtn" style={{ height: 24, fontSize: 11.5 }}
                    onClick={() => void onTurn({ optionFills: [{ tag: a.tag, value: c }] })}>{c}</button>
                ))}
              </div>
            ))}
          </div>
        )}

        {payload.summary && (
          <div style={{ marginTop: 9, border: '1px solid var(--line-soft)', borderRadius: 5, overflow: 'hidden' }}>
            {payload.summary.map((sec) => (
              <div key={sec.section}>
                <div style={{ fontSize: 11.5, fontWeight: 600, color: 'var(--ink-2)', padding: '6px 10px', background: 'var(--bg-soft)', borderBottom: '1px solid var(--line-soft)' }}>{sec.section}</div>
                {sec.items.map((it) => (
                  <div key={it.name} style={{ display: 'flex', gap: 8, padding: '4px 10px', borderBottom: '1px solid var(--line-soft)', fontSize: 12 }}>
                    <span style={{ width: 130, flexShrink: 0, color: 'var(--ink-3)' }}>{it.name}</span>
                    <span style={{ whiteSpace: 'pre-wrap', color: it.value ? 'var(--ink)' : 'var(--ink-3)' }}>{it.value ?? '（未填）'}</span>
                  </div>
                ))}
              </div>
            ))}
          </div>
        )}

        {payload.rendered && (
          <div style={{ marginTop: 9 }}>
            <button className="pbtn" style={{ height: 26, fontSize: 12, padding: '0 12px' }}
              onClick={() => void download(`/api/generate/sessions/${sessionId}/output`, payload.rendered!.fileName)
                .catch((e) => onError(e instanceof ApiError ? e.message : '下载失败'))}>
              下载《{payload.rendered.fileName}》↓
            </button>
          </div>
        )}
      </div>
    </div>
  );
}

// ─── 会话工作台（A5 基准 + A6 槽位 + A12 校验 + A7 预览输出）────
function SessionWorkbench({ sessionId, onExit }: { sessionId: string; onExit: () => void }) {
  const qc = useQueryClient();
  const [panel, setPanel] = useState<'none' | 'completeness' | 'preview'>('none');
  const [error, setError] = useState<string | null>(null);

  const q = useQuery({ queryKey: ['gen-session', sessionId], queryFn: () => get<SessionView>(`/api/generate/sessions/${sessionId}`) });
  const refresh = () => void qc.invalidateQueries({ queryKey: ['gen-session', sessionId] });

  if (q.isLoading || !q.data) return <div style={{ padding: 40 }}><Spinner text="正在载入会话…" /></div>;
  const s = q.data;
  const current = s.slots.filter((v) => v.def.stage === 'Current');
  const confirmed = current.filter((v) => v.state.confirmed).length;
  const pending = current.filter((v) => !v.state.confirmed && v.state.source === 'AiSuggested').length;

  const sections = [...new Set(current.map((v) => v.def.section))];

  return (
    <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minHeight: 0, minWidth: 0 }}>
      {/* 顶条：进度绿段已确认、紫段待确认（5.2.5 色板） */}
      <div style={{ flexShrink: 0, padding: '10px 18px 9px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 8 }}>
          <button className="gbtn" style={{ height: 26, padding: '0 9px' }} onClick={onExit}>←</button>
          <span style={{ fontSize: 13.5, fontWeight: 600 }}>{s.templateName}</span>
          {s.baseProjectNo
            ? <span className="pill" style={SOURCE_PILL_STYLE.Inherited}>基准 {s.baseProjectNo}</span>
            : <span className="pill pill-neutral">未选基准项目</span>}
          {s.projectHint && <span className="hint">要点：{s.projectHint}</span>}
          <div style={{ flexGrow: 1 }} />
          <span className="hint">已确认 <span className="m">{confirmed}</span> / {current.length}{pending > 0 && <>　·　<span style={{ color: '#8f3d74' }}>待确认 {pending}</span></>}</span>
          <button className="gbtn" onClick={() => setPanel('completeness')}>完成度校验</button>
          <button className="pbtn" style={{ height: 28, padding: '0 13px' }} onClick={() => setPanel('preview')}>预览与输出</button>
        </div>
        <div style={{ height: 4, borderRadius: 2, background: '#eef1f5', overflow: 'hidden', display: 'flex' }}>
          <div style={{ width: `${(confirmed / Math.max(1, current.length)) * 100}%`, background: '#1c6b45' }} />
          <div style={{ width: `${(pending / Math.max(1, current.length)) * 100}%`, background: '#8f3d74' }} />
        </div>
      </div>

      {error && <div style={{ padding: '10px 18px' }}><ErrorBox message={error} /></div>}

      <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '16px 20px 24px' }}>
        <div style={{ maxWidth: 880 }}>
          {!s.baseProjectNo && <CandidatePicker sessionId={sessionId} onPicked={refresh} onError={setError} />}
          {sections.map((sec) => (
            <div key={sec} style={{ marginBottom: 18 }}>
              <div style={{ fontSize: 12.5, fontWeight: 600, margin: '0 0 8px', color: 'var(--ink-2)' }}>{sec}</div>
              {current.filter((v) => v.def.section === sec).map((v) => (
                <SlotRow key={v.def.tag} sessionId={sessionId} v={v} onChanged={refresh} />
              ))}
            </div>
          ))}
          {s.slots.some((v) => v.def.stage === 'Later') && (
            <div className="hint" style={{ lineHeight: 1.7 }}>
              另有 {s.slots.filter((v) => v.def.stage === 'Later').length} 个后续阶段槽位：不提问、不预填、不给建议，输出时按模板留空处理（表 5-2）。
            </div>
          )}
        </div>
      </div>

      {panel === 'completeness' && <CompletenessModal sessionId={sessionId} onClose={() => setPanel('none')} onGoPreview={() => setPanel('preview')} />}
      {panel === 'preview' && <PreviewModal sessionId={sessionId} outputFileName={s.outputFileName} onClose={() => setPanel('none')} onRendered={refresh} />}
    </div>
  );
}

// ─── A5 基准项目候选 ─────────────────────────────────────────
function CandidatePicker({ sessionId, onPicked, onError }: { sessionId: string; onPicked: () => void; onError: (m: string) => void }) {
  const q = useQuery({ queryKey: ['gen-candidates', sessionId], queryFn: () => get<BaseCandidate[]>(`/api/generate/sessions/${sessionId}/candidates`) });
  const [busy, setBusy] = useState(false);
  const pick = async (projectNo: string | null) => {
    setBusy(true);
    try {
      await put(`/api/generate/sessions/${sessionId}/base`, { projectNo });
      onPicked();
    } catch (err) {
      onError(err instanceof ApiError ? err.message : '选定基准失败');
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="card" style={{ padding: '14px 16px', marginBottom: 18 }}>
      <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 4 }}>选定基准项目（FR-5.4）</div>
      <div className="hint" style={{ marginBottom: 12, lineHeight: 1.65 }}>
        以下候选按项目要点检索而来。「可继承槽位数」直接决定省多少事——客户名称、项目编号、报价金额、日期、联系人五类默认禁止继承，会转为提问。
      </div>
      {q.isLoading && <Spinner text="正在检索候选项目…" />}
      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
        {(q.data ?? []).map((c) => (
          <div key={c.projectNo} style={{ width: 262, border: '1px solid var(--line)', borderRadius: 5, padding: '12px 13px' }}>
            <div className="m" style={{ fontSize: 12.5, fontWeight: 600, color: 'var(--accent)' }}>{c.projectNo}</div>
            <div style={{ fontSize: 12.5, margin: '3px 0 2px' }}>{c.customerName} · {c.year}</div>
            <div className="hint" style={{ marginBottom: 8 }}>{c.deviceType}{c.deviceModel ? ` ${c.deviceModel}` : ''}{c.specParams ? ` · ${c.specParams}` : ''}</div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span style={{ fontSize: 11.5 }}>可继承 <span className="m" style={{ fontWeight: 600 }}>{c.inheritableSlots}</span> 个槽位</span>
              <span className="hint">文档 {c.linkedDocs}</span>
              <div style={{ flexGrow: 1 }} />
              <button className="pbtn" style={{ height: 24, fontSize: 11.5, padding: '0 10px' }} disabled={busy} onClick={() => void pick(c.projectNo)}>选定</button>
            </div>
          </div>
        ))}
        {q.data && q.data.length === 0 && <div className="hint">没有匹配的历史项目——可以不选基准，全部槽位转为提问。</div>}
      </div>
      <div style={{ marginTop: 10 }}>
        <button className="gbtn" style={{ height: 26, fontSize: 11.5 }} disabled={busy} onClick={() => void pick(null)}>不选基准，直接逐项填写</button>
      </div>
    </div>
  );
}

// ─── A6 单个槽位行 ───────────────────────────────────────────
function SlotRow({ sessionId, v, onChanged }: { sessionId: string; v: SlotView; onChanged: () => void }) {
  const { def, state } = v;
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(state.value ?? '');
  const [sugg, setSugg] = useState<SlotSuggestion | null>(null);
  const [chatting, setChatting] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const putSlot = async (body: { value?: string | null; confirm: boolean; adoptSuggestion?: boolean }) => {
    setBusy(true);
    setErr(null);
    try {
      await put<SlotState>(`/api/generate/sessions/${sessionId}/slots/${encodeURIComponent(def.tag)}`, {
        value: body.value ?? null, confirm: body.confirm, adoptSuggestion: body.adoptSuggestion ?? false,
      });
      setEditing(false);
      setSugg(null);
      onChanged();
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : '保存失败');
    } finally {
      setBusy(false);
    }
  };

  const askSuggest = async () => {
    setBusy(true);
    setErr(null);
    try {
      setSugg(await post<SlotSuggestion>(`/api/generate/sessions/${sessionId}/slots/${encodeURIComponent(def.tag)}/suggest`));
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : '获取建议失败');
    } finally {
      setBusy(false);
    }
  };

  const src = state.source;
  const choices = def.choices ? def.choices.split(/[|,，、]/).map((c) => c.trim()).filter(Boolean) : null;
  const long = def.dataType === 'LongText' || def.dataType === 'RepeatingRows';

  return (
    <div style={{ border: '1px solid var(--line)', borderRadius: 5, padding: '11px 13px', marginBottom: 8, background: 'var(--panel)' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 6 }}>
        <span style={{ fontSize: 12.5, fontWeight: 600 }}>{def.name}</span>
        {def.required && <span style={{ color: 'var(--cls-conf-fg)', fontSize: 12 }}>*</span>}
        {def.unit && <span className="hint">{def.unit}</span>}
        {def.forbidInherit && <span className="pill pill-neutral" title="客户名称、项目编号、报价金额、日期、联系人默认禁止继承（FR-5.7）">禁止继承</span>}
        {src && <span className="pill" style={SOURCE_PILL_STYLE[src]}>{SOURCE_LABEL[src]}{src === 'Inherited' && !state.confirmed ? '（待确认）' : ''}</span>}
        <div style={{ flexGrow: 1 }} />
        {!editing && (
          <>
            {state.value != null && !state.confirmed && (
              // 确认现值：value 传 null 走「原样确认」分支，保留继承来源与出处
              <button className="pbtn" style={{ height: 24, fontSize: 11.5, padding: '0 10px' }} disabled={busy}
                onClick={() => void putSlot({ value: null, confirm: true })}>确认</button>
            )}
            <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => { setDraft(state.value ?? ''); setEditing(true); }}>
              {state.value != null ? '修改' : '填写'}
            </button>
            {!sugg && <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} disabled={busy} onClick={() => void askSuggest()}>AI 建议</button>}
            <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => setChatting(true)}>追问</button>
          </>
        )}
      </div>

      {!editing && (
        <div style={{ fontSize: 13, lineHeight: 1.7, whiteSpace: 'pre-wrap', color: state.value != null ? 'var(--ink)' : 'var(--ink-3)' }}>
          {state.value ?? (def.prompt || '待填写')}
        </div>
      )}
      {editing && (
        <div>
          {choices ? (
            <select value={draft} onChange={(e) => setDraft(e.target.value)}
              style={{ height: 28, minWidth: 220, padding: '0 9px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' }}>
              <option value="">请选择</option>
              {choices.map((c) => <option key={c}>{c}</option>)}
            </select>
          ) : long ? (
            <textarea value={draft} onChange={(e) => setDraft(e.target.value)} autoFocus
              style={{ width: '100%', minHeight: 88, padding: '8px 10px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, lineHeight: 1.75, fontFamily: 'var(--font)', resize: 'vertical' }} />
          ) : (
            <input value={draft} onChange={(e) => setDraft(e.target.value)} autoFocus
              type={def.dataType === 'Date' ? 'date' : 'text'} inputMode={def.dataType === 'Number' ? 'decimal' : undefined}
              style={{ width: '100%', maxWidth: 420, height: 28, padding: '0 9px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' }} />
          )}
          <div style={{ display: 'flex', gap: 6, marginTop: 8 }}>
            <button className="pbtn" style={{ height: 26, fontSize: 12 }} disabled={busy || !draft.trim()}
              onClick={() => void putSlot({ value: draft.trim(), confirm: true })}>保存并确认</button>
            <button className="gbtn" style={{ height: 26, fontSize: 12 }} onClick={() => setEditing(false)}>取消</button>
          </div>
        </div>
      )}

      {state.origin && !editing && (
        <div className="hint" style={{ marginTop: 6, lineHeight: 1.6 }}>依据：{state.origin}</div>
      )}

      {/* AI 建议结果：有依据给依据（FR-5.9）；没依据明说没有，不许凭常识编（FR-5.10） */}
      {sugg && !editing && (
        <div style={{ marginTop: 9, padding: '10px 12px', borderRadius: 5, background: sugg.hasEvidence ? '#fbeff7' : 'var(--bg-soft)', border: `1px solid ${sugg.hasEvidence ? '#edd2e3' : 'var(--line-soft)'}` }}>
          {sugg.hasEvidence ? (
            <>
              <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginBottom: 6 }}>
                <span style={{ fontSize: 11.5, fontWeight: 600, color: '#8f3d74' }}>AI 建议</span>
                <span style={{ fontSize: 12.5, whiteSpace: 'pre-wrap' }}>{sugg.value}</span>
              </div>
              {sugg.note && <div style={{ fontSize: 11.5, color: '#8f3d74', marginBottom: 6 }}>{sugg.note}</div>}
              {sugg.evidence.map((e, i) => (
                <div key={i} style={{ fontSize: 11, lineHeight: 1.6, color: 'var(--ink-2)', marginBottom: 3 }}>
                  <span className="m" style={{ color: '#8f3d74' }}>[{i + 1}]</span> {e.sourceTitle}{e.section ? ` › ${e.section}` : ''}{e.pageNo != null ? ` · 第 ${e.pageNo} 页` : ''}——{e.excerpt.slice(0, 90)}…
                </div>
              ))}
              <div style={{ display: 'flex', gap: 6, marginTop: 8 }}>
                <button className="pbtn" style={{ height: 24, fontSize: 11.5, padding: '0 10px' }} disabled={busy}
                  onClick={() => void putSlot({ confirm: true, adoptSuggestion: true })}>采纳</button>
                <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} disabled={busy}
                  onClick={() => { setDraft(sugg.value ?? ''); setSugg(null); setEditing(true); }}>改后采纳</button>
                <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => setSugg(null)}>忽略</button>
              </div>
            </>
          ) : (
            <div style={{ fontSize: 12, lineHeight: 1.7, color: 'var(--ink-2)' }}>
              <strong style={{ fontWeight: 600 }}>暂无可参考数据，请自行填写。</strong>
              已检索但知识库中没有可作依据的内容——不会凭常识生成一个看起来合理的值（FR-5.10）。
              {sugg.note && <div style={{ marginTop: 4 }}>{sugg.note}</div>}
              <div style={{ marginTop: 8 }}><button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => setSugg(null)}>知道了</button></div>
            </div>
          )}
        </div>
      )}

      {err && <div style={{ marginTop: 8 }}><ErrorBox message={err} /></div>}
      {chatting && <SlotChatModal sessionId={sessionId} tag={def.tag} name={def.name} onClose={() => setChatting(false)} />}
    </div>
  );
}

// ─── A10 单槽位追问弹层（FR-5.12：不影响其他槽位）──────────────
function SlotChatModal({ sessionId, tag, name, onClose }: { sessionId: string; tag: string; name: string; onClose: () => void }) {
  const [q, setQ] = useState('');
  const [log, setLog] = useState<{ role: 'me' | 'ai'; text: string }[]>([]);
  const [busy, setBusy] = useState(false);

  const send = async (e: React.FormEvent) => {
    e.preventDefault();
    const question = q.trim();
    if (!question || busy) return;
    setQ('');
    setLog((l) => [...l, { role: 'me', text: question }]);
    setBusy(true);
    try {
      const { answer } = await post<{ answer: string }>(`/api/generate/sessions/${sessionId}/slots/${encodeURIComponent(tag)}/chat`, { question });
      setLog((l) => [...l, { role: 'ai', text: answer }]);
    } catch (err) {
      setLog((l) => [...l, { role: 'ai', text: err instanceof ApiError ? `（${err.message}）` : '（追问失败，请稍后重试）' }]);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div style={{ position: 'fixed', inset: 0, background: 'rgba(28,37,48,.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 50 }} onClick={onClose}>
      <div style={{ width: 620, height: 520, display: 'flex', flexDirection: 'column', background: 'var(--panel)', borderRadius: 8, overflow: 'hidden', boxShadow: '0 18px 50px rgba(16,24,40,.22)' }} onClick={(e) => e.stopPropagation()}>
        <div style={{ flexShrink: 0, padding: '14px 18px 11px', borderBottom: '1px solid var(--line)' }}>
          <div style={{ fontSize: 14, fontWeight: 600 }}>就「{name}」追问</div>
          <div className="hint" style={{ marginTop: 2 }}>对话不影响其他槽位状态；内容记入生成记录，追溯时能看到当时为何取了这个值。</div>
        </div>
        <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '14px 18px' }}>
          {log.length === 0 && <div className="hint">例如：这个值在防爆工况下要不要调整？</div>}
          {log.map((m, i) => (
            <div key={i} style={{ display: 'flex', justifyContent: m.role === 'me' ? 'flex-end' : 'flex-start', marginBottom: 10 }}>
              <div style={{
                maxWidth: '84%', padding: '8px 11px', borderRadius: 6, fontSize: 12.5, lineHeight: 1.7, whiteSpace: 'pre-wrap',
                ...(m.role === 'me'
                  ? { background: 'var(--accent-bg)', border: '1px solid var(--accent-line)' }
                  : { background: 'var(--bg-soft)', border: '1px solid var(--line-soft)' }),
              }}>{m.text}</div>
            </div>
          ))}
          {busy && <Spinner text="思考中…" />}
        </div>
        <form onSubmit={send} style={{ flexShrink: 0, display: 'flex', gap: 8, padding: '12px 18px', borderTop: '1px solid var(--line)' }}>
          <input value={q} onChange={(e) => setQ(e.target.value)} disabled={busy} placeholder="就这个槽位提问…"
            style={{ flexGrow: 1, height: 30, padding: '0 10px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' }} />
          <button className="pbtn" style={{ height: 30, padding: '0 14px' }} disabled={busy || !q.trim()}>发送</button>
          <button type="button" className="gbtn" style={{ height: 30 }} onClick={onClose}>关闭</button>
        </form>
      </div>
    </div>
  );
}

// ─── A12 完成度校验（FR-5.13：待确认也算未完成）────────────────
function CompletenessModal({ sessionId, onClose, onGoPreview }: { sessionId: string; onClose: () => void; onGoPreview: () => void }) {
  const q = useQuery({ queryKey: ['gen-completeness', sessionId], queryFn: () => get<CompletenessView>(`/api/generate/sessions/${sessionId}/completeness`) });
  return (
    <div style={{ position: 'fixed', inset: 0, background: 'rgba(28,37,48,.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 50 }} onClick={onClose}>
      <div style={{ width: 560, maxHeight: '84vh', display: 'flex', flexDirection: 'column', background: 'var(--panel)', borderRadius: 8, overflow: 'hidden', boxShadow: '0 18px 50px rgba(16,24,40,.22)' }} onClick={(e) => e.stopPropagation()}>
        <div style={{ flexShrink: 0, padding: '15px 20px 12px', borderBottom: '1px solid var(--line)' }}>
          <div style={{ fontSize: 14, fontWeight: 600 }}>完成度校验</div>
          <div className="hint" style={{ marginTop: 2 }}>「待确认」也算未完成——AI 给了建议但没人点过采纳或忽略，界面上有值，看起来是填了的。</div>
        </div>
        <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '14px 20px' }}>
          {q.isLoading && <Spinner text="校验中…" />}
          {q.data && (
            <>
              <div style={{ marginBottom: 12 }}>
                {q.data.canRender
                  ? <InfoBox>必填槽位已全部完成（{q.data.done}/{q.data.total}），可以生成文档。</InfoBox>
                  : <ErrorBox message={`还有 ${q.data.incomplete.length} 个必填槽位未完成（${q.data.done}/${q.data.total}），不允许生成。`} />}
              </div>
              {q.data.incomplete.map((it) => (
                <div key={it.tag} style={{ display: 'flex', alignItems: 'baseline', gap: 9, padding: '8px 0', borderBottom: '1px solid var(--line-soft)' }}>
                  <span style={{ fontSize: 12.5, fontWeight: 500 }}>{it.name}</span>
                  <span className="hint">{it.section}</span>
                  <div style={{ flexGrow: 1 }} />
                  <span className="pill" style={it.reason.includes('确认') ? SOURCE_PILL_STYLE.AiSuggested : { background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' }}>{it.reason}</span>
                </div>
              ))}
            </>
          )}
        </div>
        <div style={{ flexShrink: 0, display: 'flex', gap: 8, justifyContent: 'flex-end', padding: '12px 20px', borderTop: '1px solid var(--line)', background: 'var(--bg-soft)' }}>
          <button className="gbtn" onClick={onClose}>回去补齐</button>
          {q.data?.canRender && <button className="pbtn" style={{ height: 28, padding: '0 13px' }} onClick={onGoPreview}>去预览与输出</button>}
        </div>
      </div>
    </div>
  );
}

// ─── A7 预览与输出（FR-5.14/5.15）────────────────────────────
function PreviewModal({ sessionId, outputFileName, onClose, onRendered }: {
  sessionId: string; outputFileName: string | null; onClose: () => void; onRendered: () => void;
}) {
  const q = useQuery({ queryKey: ['gen-preview', sessionId], queryFn: () => get<PreviewView>(`/api/generate/sessions/${sessionId}/preview`) });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [rendered, setRendered] = useState<RenderResult | null>(null);

  const render = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await post<RenderResult>(`/api/generate/sessions/${sessionId}/render`);
      setRendered(r);
      onRendered();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '生成失败');
    } finally {
      setBusy(false);
    }
  };

  const srcStyle = (label: string): React.CSSProperties =>
    label.includes('确认') && !label.includes('待')
      ? SOURCE_PILL_STYLE.Confirmed
      : label.includes('待确认') ? SOURCE_PILL_STYLE.AiSuggested
      : label.includes('继承') ? SOURCE_PILL_STYLE.Inherited
      : SOURCE_PILL_STYLE.Template;

  return (
    <div style={{ position: 'fixed', inset: 0, background: 'rgba(28,37,48,.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 50 }} onClick={onClose}>
      <div style={{ width: 760, height: '86vh', display: 'flex', flexDirection: 'column', background: 'var(--panel)', borderRadius: 8, overflow: 'hidden', boxShadow: '0 18px 50px rgba(16,24,40,.22)' }} onClick={(e) => e.stopPropagation()}>
        <div style={{ flexShrink: 0, padding: '15px 20px 12px', borderBottom: '1px solid var(--line)' }}>
          <div style={{ fontSize: 14, fontWeight: 600 }}>预览与输出</div>
          <div className="hint" style={{ marginTop: 2, lineHeight: 1.6 }}>
            预览只负责复核内容与来源，不在浏览器中还原 Word 版式——最终版式以模板为准，由文档服务输出（FR-5.14）。输出文件页眉带「待复核」标注，转手后标识仍在（FR-5.15）。
          </div>
        </div>
        <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '14px 20px' }}>
          {q.isLoading && <Spinner text="生成预览…" />}
          {q.data?.sections.map((sec) => (
            <div key={sec.section} style={{ marginBottom: 14 }}>
              <div style={{ fontSize: 12.5, fontWeight: 600, color: 'var(--ink-2)', marginBottom: 6 }}>{sec.section}</div>
              {sec.items.map((it) => (
                <div key={it.tag} style={{ display: 'flex', gap: 10, padding: '7px 0', borderBottom: '1px solid var(--line-soft)' }}>
                  <div style={{ width: 150, flexShrink: 0, fontSize: 11.5, color: 'var(--ink-3)', lineHeight: 1.6 }}>{it.name}</div>
                  <div style={{ flexGrow: 1, minWidth: 0, fontSize: 12.5, lineHeight: 1.7, whiteSpace: 'pre-wrap', color: it.value != null ? 'var(--ink)' : 'var(--ink-3)' }}>
                    {it.value ?? '（留空）'}
                  </div>
                  <span className="pill" style={{ ...srcStyle(it.sourceLabel), flexShrink: 0, alignSelf: 'flex-start' }}>{it.sourceLabel}</span>
                </div>
              ))}
            </div>
          ))}
        </div>
        <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '12px 20px', borderTop: '1px solid var(--line)', background: 'var(--bg-soft)' }}>
          {error && <span style={{ fontSize: 12, color: 'var(--cls-conf-fg)' }}>{error}</span>}
          {rendered && <span style={{ fontSize: 12, color: '#1c6b45' }}>已生成：{rendered.outputFileName}（回填 {rendered.slotsFilled}，留空 {rendered.leftBlank}）</span>}
          <div style={{ flexGrow: 1 }} />
          <button className="gbtn" onClick={onClose}>关闭</button>
          {(rendered || outputFileName) && (
            <button className="gbtn" onClick={() => void download(`/api/generate/sessions/${sessionId}/output`, rendered?.outputFileName ?? outputFileName ?? '方案.docx')
              .catch((err) => setError(err instanceof ApiError ? err.message : '下载失败'))}>
              下载 Word ↓
            </button>
          )}
          <button className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={busy} onClick={() => void render()}>
            {busy ? '生成中…' : '生成 Word'}
          </button>
        </div>
      </div>
    </div>
  );
}
