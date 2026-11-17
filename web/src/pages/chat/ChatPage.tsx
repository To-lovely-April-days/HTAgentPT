// 唯一入口：一个对话窗口，一份上下文。
// 用户说什么，系统就做什么——问知识、查台账、要翻译、看案例、出文档，
// 全部在这条对话流里就地完成，不切页面、不换标签。
//
// 对话骨架用 @assistant-ui/react 的 external store：消息状态由我们自己持有
// （问答走 SSE，方案生成走生成会话的回合端点），assistant-ui 负责消息流、
// 自动滚动、输入框与运行态。领域结果卡按消息 id 从我们自己的表里取，
// 不经它的 part 体系——那样两边都不别扭。
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useLocation, useNavigate } from 'react-router-dom';
import {
  AssistantRuntimeProvider, ComposerPrimitive, MessagePrimitive,
  ThreadPrimitive, useAuiState, useExternalStoreRuntime,
} from '@assistant-ui/react';
import { ApiError, download, get, post, sse } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { Perm, unwrapLedgerTable } from '../../lib/types';
import type {
  CaseDetailData, CaseRow, ChatAttachment, GenChatPayload, GenChatTurnResult, KbRow, LedgerTable, QaSessionRow, VocabRow,
  QaTemplateRec, QaTimeline, SessionRowT, SessionView, Source, TextTranslationResult, TicketRow,
} from '../../lib/types';
import { SideSlotCtx } from '../../Shell';
import { BrandMark, Icon } from '../../components/Icons';
import { ATTACH_ACCEPT, formatSize, useAttachments } from './useAttachments';
import type { PendingFile } from './useAttachments';
import { Prose } from '../../components/Prose';
import { ErrorBox, Spinner } from '../../components/Common';
import { AdviceCard, EquipmentCard, EvidenceCard, SummaryCard } from '../generate/ChatCards';
import { AgentProgressCard, BaseCards, CasesCard, LedgerCard, mergeAgentSteps, NoResultCard, SourcesCard, TemplatePicker, TicketsCard, TranslatedCard, TranslationCard } from './ResultCards';
import type { AgentStep } from './ResultCards';
import { LivePreview } from './LivePreview';

/** 对话里的一条消息。text 是正文，其余字段是这一轮长出来的结果件。 */
interface Msg {
  id: string;
  role: 'user' | 'assistant';
  text: string;
  streaming?: boolean;
  error?: string;
  intent?: string;
  agentSteps?: AgentStep[];
  sources?: Source[];
  noResult?: { message: string; docs: string[] };
  table?: LedgerTable;
  translation?: (TextTranslationResult & { direction: string; source: string | null; sourceText: string }) | null;
  cases?: { rows: CaseRow[]; detail: CaseDetailData | null; keyword: string; note: string };
  tickets?: { rows: TicketRow[]; status: string | null; note: string };
  templates?: QaTemplateRec[] | null;
  gen?: GenChatPayload;          // 方案生成的一轮
  files?: string[];              // 用户这句话带的文件（只显示文件名）
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
  // 首页输入框上的处理方式：自动判断，或指定查资料/查项目/出文档（走服务端的 forcedIntent）
  const [mode, setMode] = useState<Mode>('auto');
  // 资料范围：全部 / 某个知识库 / 某一类文档。对一条对话一直生效，随检索条件下发（服务端检索本来就支持）
  const [scope, setScope] = useState<Scope>(ALL_SCOPE);
  const canScope = has(Perm.QaInternal);
  // 输入框里传文件：附件按内部资料处理，要内部问答权限；填文档时不收（那条路不读附件）
  const canAttach = has(Perm.QaInternal);
  const att = useAttachments();
  // 库清单与词表只对内部员工开放，越权请求会进审计——没这个权限就别去要
  const kbs = useQuery({ queryKey: ['kbs'], enabled: canScope, staleTime: 300_000, queryFn: () => get<KbRow[]>('/api/kbs') });
  const cats = useQuery({ queryKey: ['vocab', 'doc_category'], enabled: canScope, staleTime: 300_000,
    queryFn: () => get<VocabRow[]>('/api/vocab/doc_category') });
  const scopeOptions = useMemo<ScopeOptions>(() => ({
    kbs: (kbs.data ?? []).filter((k) => k.isActive).map((k) => ({ id: k.id, name: k.name })),
    cats: (cats.data ?? []).filter((c) => c.isActive).map((c) => c.value),
  }), [kbs.data, cats.data]);
  const sideSlot = useContext(SideSlotCtx);
  const location = useLocation();
  const seq = useRef(0);
  const nav = useNavigate();
  const qc = useQueryClient();
  const sessions = useQuery({ queryKey: ['qa-sessions'], queryFn: () => get<QaSessionRow[]>('/api/qa-sessions') });

  const newId = () => `m${++seq.current}-${Date.now()}`;
  const push = (m: Msg) => setMsgs((x) => [...x, m]);
  const patch = (id: string, p: Partial<Msg>) =>
    setMsgs((x) => x.map((m) => (m.id === id ? { ...m, ...p } : m)));
  const patchAgentSteps = (id: string, payload: unknown) =>
    setMsgs((x) => x.map((m) => (m.id === id
      ? { ...m, agentSteps: mergeAgentSteps(m.agentSteps, payload) }
      : m)));

  // ── 问答一轮（SSE）：意图由服务端判，结果就地长出来 ──────────
  const askQa = useCallback(async (question: string, forcedIntent?: string, attachmentIds?: string[]) => {
    const id = newId();
    push({ id, role: 'assistant', text: '', streaming: true });
    try {
      let answer = '';
      for await (const ev of sse('/api/chat/completions', {
        sessionId: qaSessionId, question, forcedIntent: forcedIntent ?? null,
        attachmentIds: attachmentIds && attachmentIds.length > 0 ? attachmentIds : null,
        filters: { query: question, kbIds: scope.kbId ? [scope.kbId] : null, docCategory: scope.docCategory },
      })) {
        const p = ev.payload as Record<string, unknown>;
        switch (ev.kind) {
          case 'meta':
            if (p.sessionId && !qaSessionId) setQaSessionId(p.sessionId as string);
            patch(id, { intent: p.intent as string });
            break;
          case 'agent':
          case 'progress':
            patchAgentSteps(id, ev.payload);
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
            // 台账结果可以和多Agent摘要共存；表格到达时保留已流式输出的正文。
            patch(id, { table: ledgerFromEvent(ev.payload) });
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
  }, [qaSessionId, scope]);

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
    const withFiles = canAttach && !genSessionId && att.files.some((f) => f.state !== 'error');
    push({ id: newId(), role: 'user', text,
      files: withFiles ? att.files.filter((f) => f.state !== 'error').map((f) => f.name) : undefined });
    try {
      if (genSessionId) await runGenTurn(genSessionId, { message: text });
      else {
        // 文件还在解析就等它出结果再问——不然这一问拿不到文件内容
        const { ids } = withFiles ? await att.take() : { ids: [] as string[] };
        await askQa(text, mode === 'auto' ? undefined : mode, ids);
      }
      setMode('auto');
      void qc.invalidateQueries({ queryKey: ['qa-sessions'] });
      void qc.invalidateQueries({ queryKey: ['chat-files'] });
    } finally { setBusy(false); }
  }, [busy, genSessionId, askQa, runGenTurn, qc, mode, canAttach, att]);

  // 选中模板：就地建会话并开场，此后这条对话交给它
  const pickTemplate = useCallback(async (t: QaTemplateRec) => {
    if (busy) return;
    setBusy(true);
    try {
      const lastAsk = [...msgs].reverse().find((m) => m.role === 'user')?.text ?? '';
      const s = await post<SessionView>('/api/generate/sessions',
        { templateId: t.id, projectHint: lastAsk, qaSessionId });
      setGenSessionId(s.id);
      setGenTitle(t.name);
      await runGenTurn(s.id, { start: true, message: lastAsk });
    } catch (err) {
      push({ id: newId(), role: 'assistant', text: '', error: err instanceof ApiError ? err.message : '没能开始这份文档' });
    } finally { setBusy(false); }
  }, [busy, msgs, runGenTurn, qaSessionId]);

  const genTurn = useCallback(async (body: Record<string, unknown>) => {
    if (!genSessionId || busy) return;
    setBusy(true);
    try { await runGenTurn(genSessionId, body); } finally { setBusy(false); }
  }, [genSessionId, busy, runGenTurn]);

  const correctIntent = useCallback((q: string) => { void askQa(q, 'knowledge'); }, [askQa]);

  // 打开一条历史对话：问答的轮次与这条对话里起过的生成会话的轮次一起取回来，
  // 按时间并成一串——不并的话，选完模板之后填了什么全看不见。
  const openSession = useCallback(async (id: string) => {
    if (busy) return;
    setBusy(true);
    try {
      const tl = await get<QaTimeline>(`/api/qa-sessions/${id}/timeline`);
      const rebuilt: Msg[] = tl.turns.map((t) => {
        if (t.role === 'user') return { id: t.id, role: 'user', text: t.text };
        if (t.kind === 'gen') {
          let gen: GenChatPayload = {};
          try { gen = t.payload ? (JSON.parse(t.payload) as GenChatPayload) : {}; } catch { /* 老数据容错 */ }
          return { id: t.id, role: 'assistant', text: t.text, gen, genSessionId: t.genSessionId ?? undefined };
        }
        let sources: Source[] | undefined;
        try { sources = t.sources ? (JSON.parse(t.sources) as Source[]) : undefined; } catch { /* 老数据容错 */ }
        return { id: t.id, role: 'assistant', text: t.text, intent: t.intent ?? undefined, sources, ...restore(t.payload) };
      });
      setMsgs(rebuilt);
      setQaSessionId(id);
      // 还没生成完的那一份接着填；已经生成完的就只是历史，不把人拉回填写态
      setGenSessionId(tl.resume?.sessionId ?? null);
      setGenTitle(tl.resume?.templateName ?? null);
      setRevision((n) => n + 1);
      setFocusSource(null);
    } catch (err) {
      push({ id: newId(), role: 'assistant', text: '', error: err instanceof ApiError ? err.message : '这条对话打不开' });
    } finally { setBusy(false); }
  }, [busy]);

  const newChat = useCallback(() => {
    if (busy) return;
    setMsgs([]); setQaSessionId(null); setGenSessionId(null); setGenTitle(null); setFocusSource(null); setMode('auto');
    att.clear();
  }, [busy, att]);

  // 这条对话里传过的文件（随对话保存），显示在底部输入框上方
  const sessionFiles = useQuery({
    queryKey: ['chat-files', qaSessionId], enabled: canAttach && !!qaSessionId,
    queryFn: () => get<ChatAttachment[]>(`/api/chat/attachments?sessionId=${qaSessionId}`),
  });

  // 侧栏的「新建对话」：外框经路由 state 递一个时间戳过来，这里收到就清空回首页
  const fresh = (location.state as { fresh?: number } | null)?.fresh;
  useEffect(() => { if (fresh) newChat(); }, [fresh]); // eslint-disable-line react-hooks/exhaustive-deps

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
        {/* 最近的对话放进外框侧栏（导航下面），状态仍在这一页 */}
        {sideSlot && createPortal(
          <>
            <div className="side-grp">最近</div>
            {sessions.data?.length === 0 && <div className="hint" style={{ padding: '0 14px' }}>还没有对话记录。</div>}
            {sessions.data?.map((x) => (
              <button key={x.id} className={'side-hist' + (x.id === qaSessionId ? ' on' : '')}
                disabled={busy} title={`${x.title}（${x.updatedAt.slice(0, 10)}）`} onClick={() => void openSession(x.id)}>
                {x.title}
              </button>
            ))}
          </>, sideSlot)}

        <div className="chat-page">
          {genSessionId && (
            <div className="chat-mode">
              <span className="chat-mode-t">{genTitle}</span>
              <span className="pill pill-brand" title="这条对话里说的都算这份文档的内容；问知识、要建议也照常">正在填写</span>
              <div style={{ flexGrow: 1 }} />
              <button className="gbtn" onClick={() => setShowPreview(!showPreview)}>{showPreview ? '收起预览' : '看预览'}</button>
              <button className="gbtn" onClick={() => nav(`/generate?session=${genSessionId}`)}>逐项核对</button>
              <button className="gbtn" onClick={() => { setGenSessionId(null); setGenTitle(null); }}>结束这份文档</button>
            </div>
          )}

          <ThreadPrimitive.Root className="chat-root">
            <ThreadPrimitive.Viewport className="chat-view sc" autoScroll>
              <ThreadPrimitive.Empty>
                <Welcome has={has} name={profile?.displayName ?? ''} mode={mode} onMode={setMode}
                  attach={canAttach ? { files: att.files, onAdd: att.add, onRemove: att.remove } : undefined}
                  scopePicker={canScope ? <ScopePicker value={scope} options={scopeOptions} onChange={setScope} /> : null}
                  onPick={(q) => void send(q)} onOpenDoc={(id) => nav(`/generate?session=${id}`)} />
              </ThreadPrimitive.Empty>
              <ThreadPrimitive.Messages components={{ UserMessage, AssistantMessage }} />
              {busy && <div style={{ padding: '4px 0 10px 42px' }}><Spinner text="处理中…" /></div>}
            </ThreadPrimitive.Viewport>

            {msgs.length > 0 && (
              <div className="chat-composer">
                {!genSessionId && (sessionFiles.data?.length ?? 0) > 0 && (
                  <div className="sess-files" aria-label="本对话的文件">
                    <span className="hint">本对话的文件</span>
                    {sessionFiles.data!.map((f) => (
                      <span key={f.id} className="sess-file" title={f.status === 'Failed' ? f.error ?? '解析失败' : `${f.fileName} · ${formatSize(f.size)}`}>
                        <Icon name="file" size={13} />{f.fileName}{f.status === 'Failed' && <b>（没读出来）</b>}
                      </span>
                    ))}
                  </div>
                )}
                <Composer placeholder={genSessionId ? '接着说这份文档的内容，或问我别的' : '接着问，或说下一件要做的事'}
                  attach={canAttach && !genSessionId ? { files: att.files, onAdd: att.add, onRemove: att.remove } : undefined}>
                  {canScope && !genSessionId && <ScopePicker value={scope} options={scopeOptions} onChange={setScope} />}
                </Composer>
              </div>
            )}
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

type Mode = 'auto' | 'knowledge' | 'ledger' | 'generate';

/** 资料范围：二选一——限定一个知识库，或限定一类文档；都空就是全部。 */
interface Scope { kbId: string | null; docCategory: string | null }
interface ScopeOptions { kbs: { id: string; name: string }[]; cats: string[] }
const ALL_SCOPE: Scope = { kbId: null, docCategory: null };

/** 资料范围下拉：原生 select（键盘、读屏都现成），外观做成输入框里的一枚小按钮。 */
function ScopePicker({ value, options, onChange }: { value: Scope; options: ScopeOptions; onChange: (s: Scope) => void }) {
  const current = value.kbId ? `kb:${value.kbId}` : value.docCategory ? `cat:${value.docCategory}` : 'all';
  const narrowed = current !== 'all';
  return (
    <label className={'scope' + (narrowed ? ' on' : '')} title="只在选定的资料里找答案">
      <span className="scope-cap">资料范围</span>
      <select className="scope-sel" value={current} aria-label="资料范围"
        onChange={(e) => {
          const v = e.target.value;
          onChange(v.startsWith('kb:') ? { kbId: v.slice(3), docCategory: null }
            : v.startsWith('cat:') ? { kbId: null, docCategory: v.slice(4) } : ALL_SCOPE);
        }}>
        <option value="all">全部资料</option>
        {options.kbs.length > 0 && (
          <optgroup label="知识库">
            {options.kbs.map((k) => <option key={k.id} value={`kb:${k.id}`}>{k.name}</option>)}
          </optgroup>
        )}
        {options.cats.length > 0 && (
          <optgroup label="文档类别">
            {options.cats.map((c) => <option key={c} value={`cat:${c}`}>{c}</option>)}
          </optgroup>
        )}
      </select>
    </label>
  );
}

const MODES: { key: Mode; label: string; perm: string[] }[] = [
  { key: 'auto', label: '自动判断', perm: [] },
  { key: 'knowledge', label: '查资料', perm: [Perm.QaInternal, Perm.QaPublic] },
  { key: 'ledger', label: '查项目', perm: [Perm.ProjectSearch] },
  { key: 'generate', label: '出文档', perm: [Perm.Generate] },
];

interface AttachProps { files: PendingFile[]; onAdd: (f: FileList | File[]) => void; onRemove: (key: string) => void }

/** 输入框：首页是居中的大框（输入在上、处理方式与发送在下），对话中是底部的一行。
    能传文件时左边有回形针，也可以把文件直接拖到框上；传上来的文件以小卡片排在输入上方。 */
function Composer({ placeholder, big, attach, children }: {
  placeholder: string; big?: boolean; attach?: AttachProps; children?: ReactNode;
}) {
  const pick = useRef<HTMLInputElement>(null);
  const [drag, setDrag] = useState(false);
  const send = (
    <ComposerPrimitive.Send className="composer-send" aria-label="发送">
      <Icon name="up" size={18} stroke={2.2} />
    </ComposerPrimitive.Send>
  );
  const clip = attach && (
    <>
      <button type="button" className="composer-clip" aria-label="添加文件" title="添加文件（Word、Excel、PowerPoint、PDF，单个不超过 20MB，一次最多 5 个）"
        onClick={() => pick.current?.click()}>
        <Icon name="clip" size={18} />
      </button>
      <input ref={pick} type="file" multiple accept={ATTACH_ACCEPT} hidden
        onChange={(e) => { if (e.target.files?.length) attach.onAdd(e.target.files); e.target.value = ''; }} />
    </>
  );
  const input = (
    <ComposerPrimitive.Input className="composer-input" rows={big ? 3 : 1} autoFocus
      aria-label="输入你的问题或任务" placeholder={placeholder} />
  );
  return (
    <ComposerPrimitive.Root className={'composer' + (drag ? ' drag' : '')}
      onDragOver={attach ? (e) => { if (e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDrag(true); } } : undefined}
      onDragLeave={attach ? () => setDrag(false) : undefined}
      onDrop={attach ? (e) => { e.preventDefault(); setDrag(false); if (e.dataTransfer.files.length) attach.onAdd(e.dataTransfer.files); } : undefined}>
      {attach && attach.files.length > 0 && (
        <div className="att-list">
          {attach.files.map((f) => (
            <div key={f.key} className={'att att-' + f.state} title={f.note ?? f.name}>
              <Icon name="file" size={16} />
              <span className="att-txt">
                <span className="att-n">{f.name}</span>
                <span className="att-s">
                  {f.state === 'uploading' ? '上传中…' : f.state === 'parsing' ? (f.note ?? '解析中…')
                    : f.state === 'error' ? (f.note ?? '失败') : `${formatSize(f.size)} · ${f.note ?? '已读取'}`}
                </span>
              </span>
              <button type="button" className="att-x" aria-label={`移除 ${f.name}`} onClick={() => attach.onRemove(f.key)}>
                <Icon name="x" size={13} stroke={2.2} />
              </button>
            </div>
          ))}
        </div>
      )}
      {big ? (
        <>
          {input}
          <div className="composer-bar">{clip}{children}{send}</div>
        </>
      ) : (
        <div className="composer-line">{clip}{input}{children}{send}</div>
      )}
      {drag && <div className="composer-drop">松开即可添加文件</div>}
    </ComposerPrimitive.Root>
  );
}

function greet(): string {
  const h = new Date().getHours();
  return h < 5 ? '夜深了' : h < 11 ? '早上好' : h < 13 ? '中午好' : h < 18 ? '下午好' : '晚上好';
}

function ago(iso: string): string {
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return '';
  const min = Math.round((Date.now() - t) / 60000);
  if (min < 1) return '刚刚';
  if (min < 60) return `${min} 分钟前`;
  if (min < 24 * 60) return `${Math.round(min / 60)} 小时前`;
  if (min < 48 * 60) return '昨天';
  return iso.slice(5, 10).replace('-', '/');
}

/** 首页（对话还没开始时）：问候、居中的大输入框、按权限给的示例、接着上次没填完的文档。
    不摆功能入口——说一句话就行，示例只是让人知道还能这样问。 */
function Welcome({ has, name, mode, onMode, scopePicker, attach, onPick, onOpenDoc }: {
  has: (p: string) => boolean; name: string; mode: Mode; onMode: (m: Mode) => void; scopePicker: ReactNode;
  attach?: AttachProps;
  onPick: (q: string) => void; onOpenDoc: (sessionId: string) => void;
}) {
  const samples: { perm: string[]; tag: string; text: string }[] = [
    { perm: [Perm.QaInternal, Perm.QaPublic], tag: '查资料', text: 'CJF-5L 磁力耦合轴承的复装力矩是多少' },
    { perm: [Perm.ProjectSearch], tag: '查项目', text: '华东理工近三年做过哪些反应釜' },
    { perm: [Perm.CaseRead], tag: '看案例', text: 'CJF-5L 显示 E12 报警怎么处理' },
    { perm: [Perm.TicketHandle], tag: '看工单', text: '待处理的工单有哪些' },
    { perm: [Perm.Translate], tag: '要英文', text: '把这段翻译成英文：本设备采用磁力耦合密封，最高工作压力 10MPa' },
    { perm: [Perm.Generate], tag: '出文档', text: '帮我出一份技术协议' },
  ];
  const shown = samples.filter((c) => c.perm.some(has));
  const modes = MODES.filter((m) => m.perm.length === 0 || m.perm.some(has));
  const docs = useQuery({
    queryKey: ['gen-sessions'], enabled: has(Perm.Generate),
    queryFn: () => get<SessionRowT[]>('/api/generate/sessions'),
  });
  const recent = (docs.data ?? []).slice(0, 3);

  return (
    <div className="home">
      <div>
        <h1 className="home-hi">{greet()}{name ? `，${name}` : ''}</h1>
        <p className="home-sub">查资料、翻历史项目、出技术协议——直接说要做什么，不用先选功能。</p>
      </div>

      <Composer big attach={attach}
        placeholder={attach ? '直接说要做什么，也可以把客户的需求文件拖进来问' : '例如：给华东理工出一份尼龙66合成装置的技术协议，两台带搅拌反应釜 R-101、R-102'}>
        {modes.length > 1 && (
          <div className="seg" role="group" aria-label="处理方式">
            {modes.map((m) => (
              <button key={m.key} type="button" className={'seg-b' + (mode === m.key ? ' on' : '')}
                aria-pressed={mode === m.key} onClick={() => onMode(m.key)}>{m.label}</button>
            ))}
          </div>
        )}
        {scopePicker}
      </Composer>

      {shown.length > 0 && (
        <div className="home-chips">
          {shown.map((c) => (
            <button key={c.tag} type="button" className="home-chip" onClick={() => onPick(c.text)}>
              <b>{c.tag}</b>{c.text}
            </button>
          ))}
        </div>
      )}

      {recent.length > 0 && (
        <section className="home-sec" aria-labelledby="home-docs">
          <div className="home-sec-hd">
            <h2 id="home-docs" className="home-sec-t">接着上次的工作</h2>
          </div>
          <div className="home-grid">
            {recent.map((d) => {
              const pct = d.total > 0 ? Math.round((d.done / d.total) * 100) : 0;
              const finished = d.status === 'Completed';
              return (
                <button key={d.id} type="button" className="home-card" onClick={() => onOpenDoc(d.id)}>
                  <span style={{ display: 'flex', alignItems: 'center', gap: 8, width: '100%' }}>
                    <span className={'pill ' + (finished ? 'pill-neutral' : 'pill-brand')}>{finished ? '已生成' : '填写中'}</span>
                    <span className="hint" style={{ marginLeft: 'auto' }}>{ago(d.updatedAt)}</span>
                  </span>
                  <span className="home-card-t">{d.templateName}</span>
                  <span className="home-card-m">{finished ? '已生成 · 待复核' : `已填 ${d.done} / ${d.total} 项`}</span>
                  <span className="home-bar" style={{ width: '100%' }} aria-hidden="true"><i style={{ width: `${finished ? 100 : pct}%` }} /></span>
                </button>
              );
            })}
          </div>
        </section>
      )}

      <p className="home-foot">回答逐句标注来源 · 生成的文件带「待复核」标记，人工确认后再对外使用</p>
    </div>
  );
}

function UserMessage() {
  const id = useAuiState((s) => s.message.id);
  const { byId } = useContext(MsgCtx);
  const files = byId.get(id)?.files;
  return (
    <MessagePrimitive.Root className="chat-user">
      {files && files.length > 0 && (
        <div className="chat-user-files">
          {files.map((n) => <span key={n} className="sess-file"><Icon name="file" size={13} />{n}</span>)}
        </div>
      )}
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
      <BrandMark />
      <div className="chat-bubble">
        {m.intent === 'general' && (
          <div className="hint" style={{ marginBottom: 7 }}>通用回答 · 未引用企业资料</div>
        )}
        {m.agentSteps && <AgentProgressCard steps={m.agentSteps} streaming={m.streaming} />}
        {m.text && <Prose text={m.text}
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

        {/* 方案生成这一轮的结果件：设备清单、建议、汇总、选项、下载 */}
        {m.gen?.equipment && (
          <EquipmentCard data={m.gen.equipment} active={active} onTurn={genTurn} />
        )}
        {m.gen?.advices && m.gen.advices.length > 0 && (
          <AdviceCard advices={m.gen.advices} active={active} onTurn={genTurn} />
        )}
        {m.gen?.suggestions && m.gen.suggestions.length > 0 && (
          <EvidenceCard suggestions={m.gen.suggestions} active={active} onTurn={genTurn} />
        )}
        {m.gen?.summary && <SummaryCard summary={m.gen.summary} />}
        {m.gen?.translated && <TranslatedCard data={m.gen.translated} />}
        {active && m.gen?.asks && m.gen.asks.filter((a) => a.choices?.length).length > 0 && (
          <div className="ask-box">
            {m.gen.asks.filter((a) => a.choices?.length).map((a) => (
              <div key={a.tag} className="ask-row">
                <div className="ask-name">{a.name}{a.dataType === 'ChoiceOrText' && <span className="hint">可选，也可直接输入</span>}</div>
                <div className="ask-chips">
                  {a.choices!.map((c) => (
                    <button key={c} type="button" className="chip"
                      onClick={() => void genTurn({ optionFills: [{ tag: a.tag, value: c }] })}>{c}</button>
                  ))}
                </div>
              </div>
            ))}
          </div>
        )}
        {m.gen?.rendered && m.genSessionId && (
          <div style={{ marginTop: 9 }}>
            <button className="pbtn"
              onClick={() => void download(`/api/generate/sessions/${m.genSessionId}/output`, m.gen!.rendered!.fileName)}>
              下载《{m.gen.rendered.fileName}》↓
            </button>
          </div>
        )}
        {active && genSessionId && m.gen && !m.gen.rendered && (
          <div style={{ marginTop: 12, display: 'flex', gap: 6, flexWrap: 'wrap' }}>
            {(['查历史项目', '推荐参数', '出英文版', '跳过', '汇总', '生成文档'] as const).map((label) => (
              <button key={label} className={label === '生成文档' ? 'pbtn' : 'gbtn'}
                onClick={() => void genTurn({ message: label === '查历史项目' ? '查一下历史做过的项目'
                  : label === '出英文版' ? '给我一份英文版的文档' : label })}>{label}</button>
            ))}
          </div>
        )}
      </div>
    </MessagePrimitive.Root>
  );
}

/** 把存下来的结果件还原成这条消息的字段。老数据没有 payload，就只剩正文与依据。 */
function restore(raw: string | null | undefined): Partial<Msg> {
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw) as { kind: string; mode?: string; data?: Record<string, unknown>; table?: unknown; agentSteps?: AgentStep[]; steps?: unknown };
    const { kind } = parsed;
    const data = parsed.data ?? {};
    const storedSteps = Array.isArray(parsed.agentSteps) ? parsed.agentSteps : mergeAgentSteps(undefined, parsed.steps);
    const steps = storedSteps.length > 0 ? { agentSteps: storedSteps } : {};
    switch (kind) {
      case 'table': return { ...steps, table: data as unknown as LedgerTable };
      case 'multi_agent': {
        const table = parsed.table ?? data.table;
        return {
          ...steps,
          ...(table && typeof table === 'object' ? { table: table as LedgerTable } : {}),
        };
      }
      case 'translation': return { ...steps, translation: data as unknown as Msg['translation'], text: '' };
      case 'cases': return {
        ...steps,
        text: '',
        cases: {
          rows: data.rows as CaseRow[], detail: (data.detail as CaseDetailData | null) ?? null,
          keyword: (data.keyword as string) ?? '', note: (data.note as string) ?? '',
        },
      };
      case 'tickets': return {
        ...steps,
        text: '',
        tickets: {
          rows: data.rows as TicketRow[], status: (data.status as string | null) ?? null,
          note: (data.note as string) ?? '',
        },
      };
      case 'generate': return { ...steps, templates: (data.templates as QaTemplateRec[] | null) ?? null };
      default: return steps;
    }
  } catch { return {}; }
}

/** 新版 SSE 可能将台账包在 {table} 中，旧版直接发送表格；两者都兼容。 */
function ledgerFromEvent(payload: unknown): LedgerTable {
  return unwrapLedgerTable(payload) ?? { rows: [], filters: { customer: null, deviceType: null, yearFrom: null, yearTo: null }, amountVisible: false, note: '台账结果格式无效。' };
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
