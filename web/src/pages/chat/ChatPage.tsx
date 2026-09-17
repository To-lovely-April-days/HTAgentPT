// 唯一入口：一个对话窗口，一份上下文。
// 用户说什么，系统就做什么——问知识、查台账、要翻译、看案例、出文档，
// 全部在这条对话流里就地完成，不切页面、不换标签。
//
// 对话骨架用 @assistant-ui/react 的 external store：消息状态由我们自己持有
// （问答走 SSE，方案生成走生成会话的回合端点），assistant-ui 负责消息流、
// 自动滚动、输入框与运行态。领域结果卡按消息 id 从我们自己的表里取，
// 不经它的 part 体系——那样两边都不别扭。
import { createContext, useCallback, useContext, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  AssistantRuntimeProvider, ComposerPrimitive, MessagePrimitive,
  ThreadPrimitive, useAuiState, useExternalStoreRuntime,
} from '@assistant-ui/react';
import { ApiError, download, get, post, sse } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { Perm } from '../../lib/types';
import type {
  CaseDetailData, CaseRow, GenChatPayload, GenChatTurnResult, LedgerTable, QaMessageRow,
  QaSessionRow, QaTemplateRec, SessionView, Source, TextTranslationResult, TicketRow,
} from '../../lib/types';
import { Prose } from '../../components/Prose';
import { ErrorBox, Spinner } from '../../components/Common';
import { AdviceCard, EvidenceCard, SummaryCard } from '../generate/ChatCards';
import { BaseCards, CasesCard, LedgerCard, NoResultCard, SourcesCard, TemplatePicker, TicketsCard, TranslationCard } from './ResultCards';
import { LivePreview } from './LivePreview';

/** 对话里的一条消息。text 是正文，其余字段是这一轮长出来的结果件。 */
interface Msg {
  id: string;
  role: 'user' | 'assistant';
  text: string;
  streaming?: boolean;
  error?: string;
  intent?: string;
  sources?: Source[];
  noResult?: { message: string; docs: string[] };
  table?: LedgerTable;
  translation?: (TextTranslationResult & { direction: string; source: string | null; sourceText: string }) | null;
  cases?: { rows: CaseRow[]; detail: CaseDetailData | null; keyword: string; note: string };
  tickets?: { rows: TicketRow[]; status: string | null; note: string };
  templates?: QaTemplateRec[] | null;
  gen?: GenChatPayload;          // 方案生成的一轮
  genSessionId?: string;
}

const MsgCtx = createContext<{
  byId: Map<string, Msg>;
  busy: boolean;
  focusSource: number | null;
  setFocusSource: (n: number | null) => void;
  pickTemplate: (t: QaTemplateRec) => void;
  genTurn: (body: Record<string, unknown>) => Promise<void>;
  correctIntent: (q: string) => void;
  genSessionId: string | null;
}>({
  byId: new Map(), busy: false, focusSource: null, setFocusSource: () => {},
  pickTemplate: () => {}, genTurn: async () => {}, correctIntent: () => {}, genSessionId: null,
});

export default function ChatPage() {
  const { profile } = useAuth();
  const has = (p: string) => profile?.permissions.includes(p) ?? false;
  const [msgs, setMsgs] = useState<Msg[]>([]);
  const [busy, setBusy] = useState(false);
  const [qaSessionId, setQaSessionId] = useState<string | null>(null);
  const [genSessionId, setGenSessionId] = useState<string | null>(null);
  const [genTitle, setGenTitle] = useState<string | null>(null);
  const [focusSource, setFocusSource] = useState<number | null>(null);
  const [showPreview, setShowPreview] = useState(true);
  const [revision, setRevision] = useState(0);
  const seq = useRef(0);
  const nav = useNavigate();
  const qc = useQueryClient();
  const sessions = useQuery({ queryKey: ['qa-sessions'], queryFn: () => get<QaSessionRow[]>('/api/qa-sessions') });

  const newId = () => `m${++seq.current}-${Date.now()}`;
  const push = (m: Msg) => setMsgs((x) => [...x, m]);
  const patch = (id: string, p: Partial<Msg>) =>
    setMsgs((x) => x.map((m) => (m.id === id ? { ...m, ...p } : m)));

  // ── 问答一轮（SSE）：意图由服务端判，结果就地长出来 ──────────
  const askQa = useCallback(async (question: string, forcedIntent?: string) => {
    const id = newId();
    push({ id, role: 'assistant', text: '', streaming: true });
    try {
      let answer = '';
      for await (const ev of sse('/api/chat/completions', {
        sessionId: qaSessionId, question, forcedIntent: forcedIntent ?? null,
        filters: { query: question },
      })) {
        const p = ev.payload as Record<string, unknown>;
        switch (ev.kind) {
          case 'meta':
            if (p.sessionId && !qaSessionId) setQaSessionId(p.sessionId as string);
            patch(id, { intent: p.intent as string });
            break;
          case 'delta':
            answer += p.text as string;
            patch(id, { text: answer });
            break;
          case 'text':
            patch(id, { text: (p.message as string) ?? '' });
            break;
          case 'sources':
            patch(id, { sources: ev.payload as unknown as Source[] });
            break;
          case 'no_result':
            patch(id, { noResult: { message: p.message as string, docs: (p.possiblyRelatedDocs as string[]) ?? [] } });
            break;
          case 'table':
            patch(id, { table: ev.payload as unknown as LedgerTable, text: '' });
            break;
          case 'translation':
            patch(id, { text: '', translation: ev.payload as unknown as Msg['translation'] });
            break;
          case 'cases':
            patch(id, {
              text: '',
              cases: {
                rows: p.rows as CaseRow[], detail: (p.detail as CaseDetailData | null) ?? null,
                keyword: (p.keyword as string) ?? '', note: (p.note as string) ?? '',
              },
            });
            break;
          case 'tickets':
            patch(id, {
              text: '',
              tickets: {
                rows: p.rows as TicketRow[], status: (p.status as string | null) ?? null,
                note: (p.note as string) ?? '',
              },
            });
            break;
          case 'generate':
            patch(id, { text: (p.message as string) ?? '', templates: (p.templates as QaTemplateRec[] | null) ?? null });
            break;
          case 'error':
            patch(id, { error: p.message as string });
            break;
        }
      }
      patch(id, { streaming: false });
    } catch (err) {
      patch(id, { streaming: false, error: err instanceof ApiError ? err.message : '连接中断，本轮未完成' });
    }
  }, [qaSessionId]);

  // ── 方案生成的一轮：会话已开时，这条对话就由它接着走 ──────────
  const runGenTurn = useCallback(async (sessionId: string, body: Record<string, unknown>) => {
    try {
      const r = await post<GenChatTurnResult>(`/api/generate/sessions/${sessionId}/conversation`, body);
      for (const m of r.newMessages) {
        if (m.role === 'user') continue;            // 用户那条我们已经先上屏了
        let payload: GenChatPayload = {};
        try { payload = m.payload ? (JSON.parse(m.payload) as GenChatPayload) : {}; } catch { /* 老数据容错 */ }
        push({ id: `g${m.id}`, role: 'assistant', text: m.content, gen: payload, genSessionId: sessionId });
      }
      setRevision((n) => n + 1);   // 文档变了，右边预览跟着重取
    } catch (err) {
      push({ id: newId(), role: 'assistant', text: '', error: err instanceof ApiError ? err.message : '这一轮没能完成' });
    }
  }, []);

  const send = useCallback(async (text: string) => {
    if (!text.trim() || busy) return;
    setBusy(true);
    push({ id: newId(), role: 'user', text });
    try {
      if (genSessionId) await runGenTurn(genSessionId, { message: text });
      else await askQa(text);
      void qc.invalidateQueries({ queryKey: ['qa-sessions'] });
    } finally { setBusy(false); }
  }, [busy, genSessionId, askQa, runGenTurn, qc]);

  // 选中模板：就地建会话并开场，此后这条对话交给它
  const pickTemplate = useCallback(async (t: QaTemplateRec) => {
    if (busy) return;
    setBusy(true);
    try {
      const lastAsk = [...msgs].reverse().find((m) => m.role === 'user')?.text ?? '';
      const s = await post<SessionView>('/api/generate/sessions', { templateId: t.id, projectHint: lastAsk });
      setGenSessionId(s.id);
      setGenTitle(t.name);
      await runGenTurn(s.id, { start: true, message: lastAsk });
    } catch (err) {
      push({ id: newId(), role: 'assistant', text: '', error: err instanceof ApiError ? err.message : '没能开始这份文档' });
    } finally { setBusy(false); }
  }, [busy, msgs, runGenTurn]);

  const genTurn = useCallback(async (body: Record<string, unknown>) => {
    if (!genSessionId || busy) return;
    setBusy(true);
    try { await runGenTurn(genSessionId, body); } finally { setBusy(false); }
  }, [genSessionId, busy, runGenTurn]);

  const correctIntent = useCallback((q: string) => { void askQa(q, 'knowledge'); }, [askQa]);

  // 打开一条历史对话：把存下来的问答重建成消息流。
  // 结果件（表格、译文、案例）是当时那一轮的现场，历史里只留正文与依据——
  // 要重新拿结果，再问一次就是了。
  const openSession = useCallback(async (id: string) => {
    if (busy) return;
    setBusy(true);
    try {
      const rows = await get<QaMessageRow[]>(`/api/qa-sessions/${id}`);
      const rebuilt: Msg[] = [];
      for (const r of rows) {
        rebuilt.push({ id: `h${r.id}-q`, role: 'user', text: r.question });
        let sources: Source[] | undefined;
        try { sources = r.sources ? (JSON.parse(r.sources) as Source[]) : undefined; } catch { /* 老数据容错 */ }
        rebuilt.push({ id: `h${r.id}-a`, role: 'assistant', text: r.answer ?? '', sources });
      }
      setMsgs(rebuilt);
      setQaSessionId(id);
      setGenSessionId(null);
      setGenTitle(null);
      setFocusSource(null);
    } catch (err) {
      push({ id: newId(), role: 'assistant', text: '', error: err instanceof ApiError ? err.message : '这条对话打不开' });
    } finally { setBusy(false); }
  }, [busy]);

  const newChat = useCallback(() => {
    if (busy) return;
    setMsgs([]); setQaSessionId(null); setGenSessionId(null); setGenTitle(null); setFocusSource(null);
  }, [busy]);

  const byId = useMemo(() => new Map(msgs.map((m) => [m.id, m])), [msgs]);

  const runtime = useExternalStoreRuntime<Msg>({
    isRunning: busy,
    messages: msgs,
    convertMessage: (m) => ({
      role: m.role,
      // 正文交给我们自己的排版件渲染，这里只是让 assistant-ui 知道这条消息存在
      content: [{ type: 'text' as const, text: m.text || ' ' }],
      id: m.id,
    }),
    onNew: async (m) => {
      const t = m.content.find((c): c is { type: 'text'; text: string } => c.type === 'text')?.text ?? '';
      await send(t);
    },
  });

  const ctx = useMemo(() => ({
    byId, busy, focusSource, setFocusSource, pickTemplate, genTurn, correctIntent, genSessionId,
  }), [byId, busy, focusSource, pickTemplate, genTurn, correctIntent, genSessionId]);

  return (
    <AssistantRuntimeProvider runtime={runtime}>
      <MsgCtx.Provider value={ctx}>
        <div className="chat-shell">
        <aside className="chat-side sc">
          <button className="gbtn chat-new" disabled={busy} onClick={newChat}>＋ 新对话</button>
          <div className="grp">最近</div>
          {sessions.data?.length === 0 && <div className="hint" style={{ padding: '0 10px' }}>还没有对话记录。</div>}
          {sessions.data?.map((x) => (
            <button key={x.id} className={'item' + (x.id === qaSessionId ? ' on' : '')}
              disabled={busy} title={x.title} onClick={() => void openSession(x.id)}>
              <div style={{ fontSize: 12, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{x.title}</div>
              <div className="hint" style={{ fontSize: 10.5 }}>{x.updatedAt.slice(0, 10)}</div>
            </button>
          ))}
        </aside>

        <div className="chat-page">
          {genSessionId && (
            <div className="chat-mode">
              <span className="pill pill-accent">正在填《{genTitle}》</span>
              <span className="hint">这条对话里说的都算这份文档的内容；问知识、要建议也照常。</span>
              <div style={{ flexGrow: 1 }} />
              <button className="gbtn" style={{ height: 22, fontSize: 11, padding: '0 9px' }}
                onClick={() => setShowPreview(!showPreview)}>{showPreview ? '收起预览' : '看预览'}</button>
              <button className="gbtn" style={{ height: 22, fontSize: 11, padding: '0 9px' }}
                onClick={() => nav(`/generate?session=${genSessionId}`)}>逐项核对</button>
              <button className="gbtn" style={{ height: 22, fontSize: 11, padding: '0 9px' }}
                onClick={() => { setGenSessionId(null); setGenTitle(null); }}>结束这份文档</button>
            </div>
          )}

          <ThreadPrimitive.Root className="chat-root">
            <ThreadPrimitive.Viewport className="chat-view sc" autoScroll>
              <ThreadPrimitive.Empty>
                <Welcome has={has} onPick={(q) => void send(q)} />
              </ThreadPrimitive.Empty>
              <ThreadPrimitive.Messages components={{ UserMessage, AssistantMessage }} />
              {busy && <div style={{ padding: '4px 0 10px' }}><Spinner text="处理中…" /></div>}
            </ThreadPrimitive.Viewport>

            <ComposerPrimitive.Root className="chat-composer">
              <ComposerPrimitive.Input
                className="chat-input" rows={1} autoFocus
                placeholder={genSessionId ? '接着说这份文档的内容，或问我别的' : '直接说你要做什么——问参数、查历史项目、要英文版、看故障案例、出一份任务单'} />
              <ComposerPrimitive.Send className="pbtn" style={{ height: 36, padding: '0 16px' }}>发送</ComposerPrimitive.Send>
            </ComposerPrimitive.Root>
          </ThreadPrimitive.Root>
        </div>

        {genSessionId && showPreview && (
          <LivePreview sessionId={genSessionId} title={genTitle} revision={revision}
            onClose={() => setShowPreview(false)} />
        )}
        </div>
      </MsgCtx.Provider>
    </AssistantRuntimeProvider>
  );
}

/** 空态。原来是五个宽窄不一的按钮竖着堆在左上角，像一列待办，
    而且只给例句、没说清系统到底能干什么——新人看不出还能查工单、能出文档。
    改成等宽的能力卡：每张写清这件事是什么、能到什么程度，底下挂一句可以直接点的例子。 */
function Welcome({ has, onPick }: { has: (p: string) => boolean; onPick: (q: string) => void }) {
  const cards: { perm: string | string[]; title: string; desc: string; sample: string }[] = [
    {
      perm: [Perm.QaInternal, Perm.QaPublic], title: '查资料',
      desc: '参数、规程、原理。答案逐句标来源，点角标能看到是哪份文档第几页。',
      sample: 'CJF-5L 磁力耦合轴承的复装力矩是多少',
    },
    {
      perm: Perm.ProjectSearch, title: '查历史项目',
      desc: '某个客户做过哪些设备、哪年做的、什么型号。只命中一条就直接摊开项目档案。',
      sample: '华东理工近三年做过哪些反应釜',
    },
    {
      perm: Perm.CaseRead, title: '看故障案例',
      desc: '按报警代码或现象找处理办法，给出现象、原因判断与处理步骤。',
      sample: 'CJF-5L 显示 E12 报警怎么处理',
    },
    {
      perm: Perm.TicketHandle, title: '看报修工单',
      desc: '哪几单待处理、某一单办到哪了，带流转记录。',
      sample: '待处理的工单有哪些',
    },
    {
      perm: Perm.Translate, title: '要英文版',
      desc: '整段文字或上一条回答，术语按已审定术语表统一，默认给中英对照。',
      sample: '把这段翻译成英文：本设备采用磁力耦合密封，最高工作压力 10MPa',
    },
    {
      perm: Perm.Generate, title: '出一份文档',
      desc: '挑个模板就在这条对话里逐项填完，能按工况给选型建议，填好直接下载。',
      sample: '帮我出一份 C 类生产任务单',
    },
  ].filter((c) => (Array.isArray(c.perm) ? c.perm.some(has) : has(c.perm)));

  return (
    <div className="chat-empty">
      <div className="welcome-hd">
        <div className="welcome-t">说一句话就行</div>
        <div className="welcome-s">不用先选功能，直接说你要做什么。下面是这个账号能做的事，点例子就能试。</div>
      </div>
      <div className="welcome-grid">
        {cards.map((c) => (
          <button key={c.title} type="button" className="welcome-card" onClick={() => onPick(c.sample)}>
            <span className="welcome-card-t">{c.title}</span>
            <span className="welcome-card-d">{c.desc}</span>
            <span className="welcome-card-s">「{c.sample}」</span>
          </button>
        ))}
      </div>
    </div>
  );
}

function UserMessage() {
  return (
    <MessagePrimitive.Root className="chat-user">
      <div className="chat-user-bubble"><MessagePrimitive.Parts /></div>
    </MessagePrimitive.Root>
  );
}

/** 助手一条：正文按标记语法排版，后面挂这一轮长出来的结果件。 */
function AssistantMessage() {
  const id = useAuiState((s) => s.message.id);
  const { byId, busy, focusSource, setFocusSource, pickTemplate, genTurn, correctIntent, genSessionId } = useContext(MsgCtx);
  const m = byId.get(id);
  if (!m) return null;
  const isLast = [...byId.keys()].pop() === id;
  const active = isLast && !busy;

  return (
    <MessagePrimitive.Root className="chat-assistant">
      <div className="card chat-bubble">
        {m.text && <Prose text={m.text} style={{ fontSize: 13.5 }}
          onCite={m.sources ? (n) => setFocusSource(n) : undefined} />}
        {m.streaming && !m.text && <Spinner text="思考中…" />}
        {m.error && <ErrorBox message={m.error} />}

        {m.noResult && <NoResultCard message={m.noResult.message} docs={m.noResult.docs} />}
        {m.sources && m.sources.length > 0 && <SourcesCard sources={m.sources} focus={focusSource} />}
        {m.gen?.baseCandidates && m.gen.baseCandidates.length > 0 && (
          <BaseCards items={m.gen.baseCandidates} active={active}
            onPick={(no) => void genTurn({ baseProjectNo: no ?? '' })} />
        )}
        {m.table && <LedgerCard table={m.table} onCorrect={() => correctIntent(lastQuestion(byId, id))} />}
        {m.translation && <TranslationCard data={m.translation} />}
        {m.cases && <CasesCard {...m.cases} />}
        {m.tickets && <TicketsCard {...m.tickets} />}
        {m.templates && m.templates.length > 0 && (
          <TemplatePicker templates={m.templates} onPick={pickTemplate} busy={busy} />
        )}

        {/* 方案生成这一轮的结果件：建议、汇总、选项、下载 */}
        {m.gen?.advices && m.gen.advices.length > 0 && (
          <AdviceCard advices={m.gen.advices} active={active} onTurn={genTurn} />
        )}
        {m.gen?.suggestions && m.gen.suggestions.length > 0 && (
          <EvidenceCard suggestions={m.gen.suggestions} active={active} onTurn={genTurn} />
        )}
        {m.gen?.summary && <SummaryCard summary={m.gen.summary} />}
        {active && m.gen?.asks && m.gen.asks.filter((a) => a.choices?.length).length > 0 && (
          <div style={{ marginTop: 9, display: 'flex', flexDirection: 'column', gap: 6 }}>
            {m.gen.asks.filter((a) => a.choices?.length).map((a) => (
              <div key={a.tag} style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
                <span style={{ fontSize: 11.5, color: 'var(--ink-2)', flexShrink: 0 }}>{a.name}：</span>
                {a.choices!.map((c) => (
                  <button key={c} className="gbtn" style={{ height: 24, fontSize: 11.5 }}
                    onClick={() => void genTurn({ optionFills: [{ tag: a.tag, value: c }] })}>{c}</button>
                ))}
              </div>
            ))}
          </div>
        )}
        {m.gen?.rendered && m.genSessionId && (
          <div style={{ marginTop: 9 }}>
            <button className="pbtn" style={{ height: 26, fontSize: 12, padding: '0 12px' }}
              onClick={() => void download(`/api/generate/sessions/${m.genSessionId}/output`, m.gen!.rendered!.fileName)}>
              下载《{m.gen.rendered.fileName}》↓
            </button>
          </div>
        )}
        {active && genSessionId && m.gen && !m.gen.rendered && (
          <div style={{ marginTop: 9, display: 'flex', gap: 6, flexWrap: 'wrap' }}>
            {(['查历史项目', '推荐参数', '跳过', '汇总', '生成文档'] as const).map((label) => (
              <button key={label} className="gbtn" style={{ height: 24, fontSize: 11.5 }}
                onClick={() => void genTurn({ message: label === '查历史项目' ? '查一下历史做过的项目' : label })}>{label}</button>
            ))}
          </div>
        )}
      </div>
    </MessagePrimitive.Root>
  );
}

/** 这条助手消息对应的那句提问——纠正意图时要把原话再发一次。 */
function lastQuestion(byId: Map<string, Msg>, assistantId: string): string {
  const ids = [...byId.keys()];
  const i = ids.indexOf(assistantId);
  for (let k = i - 1; k >= 0; k--) {
    const m = byId.get(ids[k]);
    if (m?.role === 'user') return m.text;
  }
  return '';
}
