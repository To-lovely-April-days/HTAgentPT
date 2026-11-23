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
import { ApiError, download, get, post, put, sse } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { Perm, unwrapLedgerTable } from '../../lib/types';
import type {
  CaseDetailData, CaseRow, ChatAttachment, GenChatPayload, GenChatTurnResult, KbRow, LedgerTable, QaSessionRow, VocabRow,
  QaTemplateRec, QaTimeline, SessionRowT, SessionView, Source, TextTranslationResult, TicketRow,
  AssistantStep, EvidenceCard as EvidenceRef, VerifyReport, AskSpec, ToolArtifact, AgentsCardData, PlanState, PptTemplateRow,
} from '../../lib/types';
import { AgentsCard } from './AgentsCard';
import { PlanCard } from './PlanCard';
import { RefPanel, StepsCard, VerifyBar } from './AssistantParts';
import { ArtifactCards, AskCard, ExportBar } from './ToolCards';
import { SideSlotCtx } from '../../Shell';
import { BrandMark, Icon } from '../../components/Icons';
import { ATTACH_ACCEPT, formatSize, useAttachments } from './useAttachments';
import type { PendingFile } from './useAttachments';
import { Prose } from '../../components/Prose';
import { ErrorBox } from '../../components/Common';
import { AdviceCard, EquipmentCard, EvidenceCard, SummaryCard } from '../generate/ChatCards';
import { AgentProgressCard, BaseCards, CasesCard, LedgerCard, mergeAgentSteps, NoResultCard, SourcesCard, TemplatePicker, TicketsCard, TranslatedCard, TranslationCard } from './ResultCards';
import type { AgentStep } from './ResultCards';
import { LivePreview } from './LivePreview';
import { Home } from './home/Home';
import { InputPet } from '../../components/pet/InputPet';
import { ThinkingStage } from '../../components/pet/ThinkingStage';
import { FeedbackBar } from './Feedback';
import { ThinkPicker } from './ThinkPicker';
import type { Level, LevelsInfo } from './ThinkPicker';
import { PreviewPanel } from './preview/PreviewPanel';
import type { Panel } from './preview/PreviewPanel';
import type { WebTab } from './preview/WebBrowser';
import { PreviewCtx, htmlBlocks } from '../../components/code/preview';
import { PlusMenu } from './PlusMenu';
import { PptPicker } from './PptTemplates';
import { usePptTemplates } from './pptUtil';
import type { PlusProps } from './PlusMenu';
import { projectColor, scopeLabel } from './composerUtil';
import type { PluginRow, ProjectRow, Scope, ScopeOptions, SkillRow } from './composerUtil';
import { ProjectDialog, ProjectHome } from './ProjectHome';
import type { ProjectEdit } from './ProjectHome';
import type { PreviewItem } from '../../components/code/preview';

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
  skill?: string;                // 用户这一问点名用的技能
  genSessionId?: string;
  // 通用助手这一轮：过程、依据（用到的 / 没用上的）、核对结果、用时、提示
  steps?: AssistantStep[];
  evidence?: { used: EvidenceRef[]; other: EvidenceRef[] };
  verify?: VerifyReport;
  seconds?: number | null;
  notice?: string;
  ask?: AskSpec;                 // 助手停下来请你确认
  artifacts?: ToolArtifact[];    // 计算结果、化学品数据等卡片
  messageId?: string;            // 存下来的那条回答（反馈用）
  helpful?: boolean | null;
  feedbackReason?: string | null;
  level?: string;                // 这一轮用的思考档位（fast / standard / deep）
  truncated?: boolean;           // 写到「回答最长」停了
  agents?: AgentsCardData[];     // 分头查：每次一张，子代理的进度和结论
  plan?: PlanState | null;       // 多步任务的任务清单
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
  refs: { msgId: string; focus: number | null } | null;
  showRefs: (msgId: string, focus?: number | null) => void;
  sendText: (text: string) => void;
  openPreview: (item: PreviewItem) => void;
  /** 深度档开着、员工能自己选时，回答下面有「深度再答」 */
  deepOk: boolean;
  deepAgain: (question: string) => void;
}>({
  byId: new Map(), busy: false, focusSource: null, setFocusSource: () => {},
  pickTemplate: () => {}, genTurn: async () => {}, correctIntent: () => {}, genSessionId: null,
  refs: null, showRefs: () => {}, sendText: () => {}, openPreview: () => {}, deepOk: false, deepAgain: () => {},
});

/** 网页来源：同一个网址的几段依据合在一起，给浏览面板标黄用。 */
function webItem(cards: EvidenceRef[], n: number): PreviewItem | null {
  const c = cards.find((x) => x.n === n);
  if (!c || c.kind !== 'web' || !c.url) return null;
  const same = cards.filter((x) => x.kind === 'web' && x.url === c.url);
  const quotes = same.map((x) => (x.highlights.length > 0 ? x.highlights.map((h) => x.text.slice(h.start, h.start + h.length)).join('\n') : x.text));
  return { kind: 'web', url: c.url, title: c.title, label: `[${n}]`, quotes };
}

/** 长回答（够长、有好几个标题）可以在面板里按文档看。 */
const isLongDoc = (text: string) => text.length > 1500 && (text.match(/^#{1,3}\s/gm) ?? []).length >= 3;

export default function ChatPage() {
  const { profile } = useAuth();
  const has = (p: string) => profile?.permissions.includes(p) ?? false;
  const [msgs, setMsgs] = useState<Msg[]>([]);
  const [busy, setBusy] = useState(false);
  const [qaSessionId, setQaSessionId] = useState<string | null>(null);
  const [genSessionId, setGenSessionId] = useState<string | null>(null);
  const [genTitle, setGenTitle] = useState<string | null>(null);
  const [focusSource, setFocusSource] = useState<number | null>(null);
  // 右侧参考资料：看的是哪条回答、聚焦第几条依据。null 为收起
  const [refs, setRefs] = useState<{ msgId: string; focus: number | null } | null>(null);
  const showRefs = useCallback((msgId: string, focus: number | null = null) => setRefs({ msgId, focus }), []);
  const [showPreview, setShowPreview] = useState(true);
  // 右侧预览面板：网页代码、网页浏览、长文档、文件。打开时盖住参考资料
  const [panel, setPanel] = useState<Panel | null>(null);
  const [webTabs, setWebTabs] = useState<WebTab[]>([]);
  const [webActive, setWebActive] = useState(0);
  // 助手联网时自动开右侧网页；人正在看文件、文档、网页预览之类别的东西时不抢
  const panelRef = useRef(panel);
  useEffect(() => { panelRef.current = panel; }, [panel]);
  const autoWeb = (url: string, title?: string) => {
    if (panelRef.current && panelRef.current.kind !== 'web') return;
    openPreview({ kind: 'web', url, title });
  };
  const openPreview = useCallback((item: PreviewItem) => {
    if (item.kind === 'web') {
      setWebTabs((tabs) => {
        // 内置浏览器的标签按标签号认，别的按网址认
        const at = item.live ? tabs.findIndex((t) => t.live === item.live) : tabs.findIndex((t) => !t.live && t.url === item.url);
        if (at >= 0) {
          setWebActive(at);
          return tabs.map((t, i) => (i === at ? { ...t, quotes: item.quotes ?? t.quotes, label: item.label ?? t.label, title: t.title ?? item.title } : t));
        }
        setWebActive(tabs.length);
        return [...tabs, { url: item.url, title: item.title, label: item.label, quotes: item.quotes, live: item.live }];
      });
      setPanel({ kind: 'web' });
    } else setPanel(item);
  }, []);
  const closeWebTab = useCallback((i: number) => {
    setWebTabs((tabs) => {
      const next = tabs.filter((_, k) => k !== i);
      if (next.length === 0) setPanel(null);
      setWebActive((a) => Math.max(0, Math.min(a > i ? a - 1 : a, next.length - 1)));
      return next;
    });
  }, []);
  // 网页标签切到「可操作」、或内置浏览器里冒出新标签（网页自己开的新窗口、助手开的）
  const setLive = useCallback((i: number, live: string, url?: string, title?: string) => {
    setWebTabs((tabs) => tabs.map((t, k) => (k === i ? { ...t, live, url: url ?? t.url, title: title || t.title } : t)));
  }, []);
  const adoptTabs = useCallback((extra: WebTab[]) => {
    setWebTabs((tabs) => {
      const known = new Set(tabs.map((t) => t.live));
      const add = extra.filter((t) => !known.has(t.live)).slice(0, Math.max(0, 8 - tabs.length));
      return add.length ? [...tabs, ...add] : tabs;
    });
  }, []);
  const [revision, setRevision] = useState(0);
  // 首页输入框上的处理方式：自动判断，或指定查资料/查项目/出文档（走服务端的 forcedIntent）
  const [mode, setMode] = useState<Mode>('auto');
  // 资料范围：全部 / 某个知识库 / 某一类文档。对一条对话一直生效，随检索条件下发（服务端检索本来就支持）
  const [scope, setScope] = useState<Scope>(ALL_SCOPE);
  const canScope = has(Perm.QaInternal);
  // 「＋」里点名的技能：只管下一问，发出去就清掉
  const [skill, setSkill] = useState<SkillRow | null>(null);
  const skills = useQuery({ queryKey: ['chat-skills'], staleTime: 300_000, queryFn: () => get<SkillRow[]>('/api/chat/skills') });
  // 「＋ → 插件」的工具开关：关掉的记在这台电脑上，以后每一问都带上
  const plugins = useQuery({ queryKey: ['chat-plugins'], staleTime: 300_000, queryFn: () => get<PluginRow[]>('/api/chat/plugins') });
  const [off, setOff] = useState<string[]>(() => {
    try { const v = JSON.parse(localStorage.getItem(OFF_KEY) ?? '[]'); return Array.isArray(v) ? v.filter((x) => typeof x === 'string') : []; } catch { return []; }
  });
  const toggleOff = useCallback((code: string) => setOff((cur) => {
    const next = cur.includes(code) ? cur.filter((c) => c !== code) : [...cur, code];
    try { localStorage.setItem(OFF_KEY, JSON.stringify(next)); } catch { /* 存不了就只管这一页 */ }
    return next;
  }), []);
  // 「＋ → PPT 模板」选的模板：这次对话做 PPT 都用它，开新对话清掉
  const [ppt, setPpt] = useState<PptTemplateRow | null>(null);
  const [pptPick, setPptPick] = useState(false);
  const pptList = usePptTemplates();
  // 项目：这条对话在哪个项目里（新对话就是要建在哪个项目里）。对话还没开始、又在项目里时，显示项目首页
  const [projectId, setProjectId] = useState<string | null>(null);
  // 'new' = 侧栏点的新建（建好进项目首页）；'here' = 「＋」菜单里点的新建（建好把这条对话放进去）
  const [projectDlg, setProjectDlg] = useState<ProjectEdit | 'new' | 'here' | null>(null);
  const [allProjects, setAllProjects] = useState(false);
  const projects = useQuery({ queryKey: ['chat-projects'], queryFn: () => get<ProjectRow[]>('/api/chat-projects') });
  // 思考档位：自动 / 快速 / 标准 / 深度。只管这一个对话，开新对话回到自动
  const [level, setLevel] = useState<Level>('auto');
  const levels = useQuery({ queryKey: ['chat-levels'], staleTime: 60_000, queryFn: () => get<LevelsInfo>('/api/chat/levels') });
  const canPickLevel = levels.data?.userChoice ?? false;
  const deepOk = canPickLevel && (levels.data?.deepAvailable ?? false);
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
  // 记忆：默认开着；「＋」里一点就关、再点就开，和个人中心的开关是同一个
  const mem = useQuery({ queryKey: ['my-memories'], staleTime: 60_000, retry: false,
    queryFn: () => get<{ globalOn: boolean; enabled: boolean }>('/api/profile/memories') });
  const toggleMemory = useCallback(async () => {
    if (!mem.data) return;
    try { await put('/api/profile/memories/enabled', { enabled: !mem.data.enabled }); } catch { /* 下面重取，显示实际状态 */ }
    void qc.invalidateQueries({ queryKey: ['my-memories'] });
    void qc.invalidateQueries({ queryKey: ['home-today'] });
  }, [mem.data, qc]);
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
  const askQa = useCallback(async (question: string, forcedIntent?: string, attachmentIds?: string[], levelOverride?: Level) => {
    const id = newId();
    const started = Date.now();
    push({ id, role: 'assistant', text: '', streaming: true });
    try {
      let answer = '';
      for await (const ev of sse('/api/chat/completions', {
        sessionId: qaSessionId, question, forcedIntent: forcedIntent ?? null,
        attachmentIds: attachmentIds && attachmentIds.length > 0 ? attachmentIds : null,
        filters: { query: question, kbIds: scope.kbId ? [scope.kbId] : null, docCategory: scope.docCategory },
        level: levelOverride ?? (level === 'auto' ? null : level),
        // 右边开着可操作的网页：助手自动读这一页
        browserTab: panel?.kind === 'web' ? webTabs[Math.min(webActive, webTabs.length - 1)]?.live ?? null : null,
        projectId, skill: skill?.code ?? null, pptTemplate: ppt?.id ?? null,
        off: off.length > 0 ? off : null,
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
          case 'replace':
            // 模型说了一句「我先查一下」又去查了：那句不算答案，清掉
            answer = (p.text as string) ?? '';
            patch(id, { text: answer });
            break;
          case 'step':
            setMsgs((x) => x.map((m) => {
              if (m.id !== id) return m;
              const step = ev.payload as AssistantStep;
              const list = m.steps ?? [];
              const at = list.findIndex((s2) => s2.id === step.id);
              return { ...m, steps: at < 0 ? [...list, step] : list.map((s2, i) => (i === at ? { ...s2, ...step } : s2)) };
            }));
            break;
          case 'evidence':
            patch(id, { evidence: ev.payload as Msg['evidence'] });
            if (((ev.payload as Msg['evidence'])?.used.length ?? 0) > 0) setRefs({ msgId: id, focus: null });
            break;
          case 'verify':
            patch(id, { verify: ev.payload as VerifyReport });
            break;
          case 'notice':
            patch(id, { notice: p.message as string });
            break;
          case 'level':
            patch(id, { level: p.level as string });
            break;
          case 'truncated':
            patch(id, { truncated: true, level: p.level as string });
            break;
          case 'browser':
            // 助手在内置浏览器里开了网页：右边面板跟着打开这个标签
            openPreview({ kind: 'web', url: p.url as string, title: p.title as string, live: p.tab as string });
            break;
          case 'agents': {
            const card = ev.payload as AgentsCardData;
            setMsgs((x) => x.map((m) => {
              if (m.id !== id) return m;
              const list = m.agents ?? [];
              return { ...m, agents: list.some((c) => c.id === card.id) ? list.map((c) => (c.id === card.id ? card : c)) : [...list, card] };
            }));
            break;
          }
          case 'plan':
            patch(id, { plan: ev.payload as PlanState });
            break;
          case 'ask':
            patch(id, { ask: ev.payload as AskSpec });
            break;
          case 'artifact':
            setMsgs((x) => x.map((m) => (m.id === id ? { ...m, artifacts: [...(m.artifacts ?? []), ev.payload as ToolArtifact] } : m)));
            // 助手读了网页：右侧跟着打开（阅读模式）
            if (p.kind === 'webpage' && typeof p.url === 'string') autoWeb(p.url, p.title as string | undefined);
            break;
          case 'websearch':
            // 助手联网搜索了：右侧打开排第一的结果
            if (typeof p.url === 'string') autoWeb(p.url, p.title as string | undefined);
            break;
          case 'done': {
            patch(id, { seconds: (Date.now() - started) / 1000, ...(typeof p.messageId === 'string' ? { messageId: p.messageId } : {}) });
            // 回答里写了网页：写完直接在右侧打开预览
            const pages = htmlBlocks(answer);
            if (pages.length > 0) setPanel({ kind: 'html', code: pages[pages.length - 1] });
            break;
          }
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
  }, [qaSessionId, scope, level, panel, webTabs, webActive, openPreview, projectId, skill, ppt, off]);

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

  const send = useCallback(async (text: string, levelOverride?: Level) => {
    if (!text.trim() || busy) return;
    setBusy(true);
    const withFiles = canAttach && !genSessionId && att.files.some((f) => f.state !== 'error');
    const picked = !genSessionId && skill ? skill.name : undefined;
    if (picked) setSkill(null);      // 技能只管这一问（askQa 这一轮还拿着它）
    push({ id: newId(), role: 'user', text, skill: picked,
      files: withFiles ? att.files.filter((f) => f.state !== 'error').map((f) => f.name) : undefined });
    try {
      if (genSessionId) await runGenTurn(genSessionId, { message: text });
      else {
        // 文件还在解析就等它出结果再问——不然这一问拿不到文件内容
        const { ids } = withFiles ? await att.take() : { ids: [] as string[] };
        await askQa(text, mode === 'auto' ? undefined : mode, ids, levelOverride);
      }
      setMode('auto');
      void qc.invalidateQueries({ queryKey: ['qa-sessions'] });
      void qc.invalidateQueries({ queryKey: ['chat-files'] });
      if (projectId) { void qc.invalidateQueries({ queryKey: ['chat-projects'] }); void qc.invalidateQueries({ queryKey: ['chat-project', projectId] }); }
    } finally { setBusy(false); }
  }, [busy, genSessionId, askQa, runGenTurn, qc, mode, canAttach, att, skill, projectId]);

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
  const openSession = useCallback(async (id: string, inProject?: string | null) => {
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
        return { id: t.id, role: 'assistant', text: t.text, intent: t.intent ?? undefined, sources, ...restore(t.payload),
          messageId: t.messageId ?? undefined, helpful: t.helpful ?? null, feedbackReason: t.feedbackReason ?? null };
      });
      setMsgs(rebuilt);
      setQaSessionId(id);
      setLevel('auto'); setSkill(null); setPpt(null);
      setProjectId(inProject !== undefined ? inProject : sessions.data?.find((x) => x.id === id)?.projectId ?? null);
      // 还没生成完的那一份接着填；已经生成完的就只是历史，不把人拉回填写态
      setGenSessionId(tl.resume?.sessionId ?? null);
      setGenTitle(tl.resume?.templateName ?? null);
      setRevision((n) => n + 1);
      setFocusSource(null);
      setRefs(null);
      setPanel(null); setWebTabs([]);
    } catch (err) {
      push({ id: newId(), role: 'assistant', text: '', error: err instanceof ApiError ? err.message : '这条对话打不开' });
    } finally { setBusy(false); }
  }, [busy, sessions.data]);

  /** 开新对话；带项目 id 就是在这个项目里开（先显示项目首页）。 */
  const newChat = useCallback((inProject: string | null = null) => {
    if (busy) return;
    setMsgs([]); setQaSessionId(null); setGenSessionId(null); setGenTitle(null); setFocusSource(null); setMode('auto'); setRefs(null); setLevel('auto');
    setSkill(null); setPpt(null); setProjectId(inProject);
    setPanel(null); setWebTabs([]);
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
  // 后台「回答质量」里点「重问」：带着原来的问题过来，开一条新对话直接问
  const reask = (location.state as { ask?: string } | null)?.ask;
  useEffect(() => {
    if (!reask) return;
    // 等这一轮渲染完再开新对话：清空和发送都在同一个回调里
    const t = setTimeout(() => { window.history.replaceState({}, ''); newChat(); void send(reask); }, 0);
    return () => clearTimeout(t);
  }, [reask]); // eslint-disable-line react-hooks/exhaustive-deps

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
    byId, busy, focusSource, setFocusSource, pickTemplate, genTurn, correctIntent, genSessionId, refs, showRefs,
    sendText: (t: string) => void send(t), openPreview,
    deepOk, deepAgain: (q: string) => void send(q, 'deep'),
  }), [byId, busy, focusSource, pickTemplate, genTurn, correctIntent, genSessionId, refs, showRefs, send, openPreview, deepOk]);
  const thinkPicker = canPickLevel ? <ThinkPicker value={level} onChange={setLevel} deepOk={deepOk} /> : null;

  // 放进项目 / 移出项目：对话已经有了就马上改；还没开始就记着，第一问建对话时带上
  const moveProject = useCallback(async (pid: string | null) => {
    if (qaSessionId) {
      try { await put(`/api/qa-sessions/${qaSessionId}/project`, { projectId: pid }); }
      catch (err) { push({ id: newId(), role: 'assistant', text: '', error: err instanceof ApiError ? err.message : '没能放进项目' }); return; }
      void qc.invalidateQueries({ queryKey: ['qa-sessions'] });
      void qc.invalidateQueries({ queryKey: ['chat-projects'] });
      void qc.invalidateQueries({ queryKey: ['chat-project'] });
    }
    setProjectId(pid);
  }, [qaSessionId, qc]);
  const project = projects.data?.find((p) => p.id === projectId) ?? null;
  const narrowed = scopeLabel(scope, scopeOptions);
  const plus: PlusProps = {
    // 选了做 PPT 的技能：顺手弹出模板，挑一套或者让助手挑
    skills: skills.data?.length ? { list: skills.data, value: skill, onChange: (s) => { setSkill(s); if (s?.ppt && pptList.data?.length) setPptPick(true); } } : undefined,
    plugins: plugins.data?.length ? { list: plugins.data, off, onToggle: toggleOff } : undefined,
    onManage: profile?.roleCode === 'admin' ? () => nav('/admin/skills') : undefined,
    memory: mem.data?.globalOn ? { on: mem.data.enabled, onToggle: () => void toggleMemory() } : undefined,
    scope: canScope ? { value: scope, options: scopeOptions, onChange: setScope } : undefined,
    project: { list: projects.data ?? [], value: projectId, onChange: (pid) => void moveProject(pid), onNew: () => setProjectDlg('here') },
    ppt: pptList.data?.length ? { value: ppt?.name ?? null, onOpen: () => setPptPick(true) } : undefined,
  };
  const chips = (
    <>
      {project && msgs.length === 0 && <span className="chipx"><i>项目</i><span className="chipx-n">{project.name}</span></span>}
      {skill && (
        <span className="chipx dark"><i>技能</i><span className="chipx-n">{skill.name}</span>
          <button type="button" aria-label={`不用技能「${skill.name}」`} onClick={() => setSkill(null)}><Icon name="x" size={12} stroke={2.2} /></button>
        </span>
      )}
      {ppt && (
        <span className="chipx"><i>PPT 模板</i><span className="chipx-n">{ppt.name}</span>
          <button type="button" aria-label={`不用模板「${ppt.name}」`} onClick={() => setPpt(null)}><Icon name="x" size={12} stroke={2.2} /></button>
        </span>
      )}
      {narrowed && (
        <span className="chipx"><i>资料</i><span className="chipx-n">{narrowed}</span>
          <button type="button" aria-label="资料范围改回全部" onClick={() => setScope(ALL_SCOPE)}><Icon name="x" size={12} stroke={2.2} /></button>
        </span>
      )}
    </>
  );
  const attachProps = canAttach ? { files: att.files, onAdd: att.add, onRemove: att.remove } : undefined;
  const crumbTitle = sessions.data?.find((x) => x.id === qaSessionId)?.title ?? msgs.find((m) => m.role === 'user')?.text.slice(0, 30) ?? '';
  const refMsg = refs ? byId.get(refs.msgId) : undefined;
  // 这条对话里写过的所有网页（每次改都是新的一版）
  const htmlVersions = useMemo(() => msgs.filter((m) => m.role === 'assistant' && !m.streaming).flatMap((m) => htmlBlocks(m.text)), [msgs]);

  return (
    <AssistantRuntimeProvider runtime={runtime}>
      <MsgCtx.Provider value={ctx}>
        <PreviewCtx.Provider value={openPreview}>
        <div className="chat-shell">
        {/* 最近的对话放进外框侧栏（导航下面），状态仍在这一页 */}
        {sideSlot && createPortal(
          <>
            <div className="side-grp side-grp-act">项目
              <button type="button" aria-label="新建项目" title="新建项目" onClick={() => setProjectDlg('new')}><Icon name="plus" size={13} /></button>
            </div>
            {(projects.data ?? []).slice(0, allProjects ? undefined : 5).map((p) => (
              <button key={p.id} type="button" className={'side-pj' + (p.id === projectId ? ' on' : '')} disabled={busy}
                title={p.ledgerNo ? `${p.name}（台账 ${p.ledgerNo}）` : p.name} onClick={() => newChat(p.id)}>
                <span className="pj-ic" style={{ background: projectColor(p.id) }}>{p.name.slice(0, 1)}</span>
                <span className="n">{p.name}</span><span className="c">{p.count || ''}</span>
              </button>
            ))}
            {(projects.data?.length ?? 0) > 5 && (
              <button type="button" className="side-more" onClick={() => setAllProjects(!allProjects)}>
                {allProjects ? '收起' : `全部项目 ${projects.data!.length} 个`}<Icon name={allProjects ? 'down' : 'right'} size={12} />
              </button>
            )}
            {projects.data?.length === 0 && (
              <button type="button" className="side-more" onClick={() => setProjectDlg('new')}>把一个客户、一台设备的对话和资料放在一起</button>
            )}
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
          {project && msgs.length > 0 && (
            <div className="chat-crumb">
              <Icon name="box" size={13} />
              <button type="button" onClick={() => newChat(project.id)} disabled={busy} title="回项目首页">{project.name}</button>
              <Icon name="right" size={12} style={{ color: 'var(--ink-4)' }} />
              <b>{crumbTitle}</b>
            </div>
          )}
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
            <ThreadPrimitive.Viewport className="chat-view sc" autoScroll={msgs.length > 0}>
              <ThreadPrimitive.Empty>
                {projectId ? (
                  <ProjectHome id={projectId} busy={busy} onOpenSession={(sid) => void openSession(sid, projectId)}
                    onEdit={(p) => setProjectDlg(p)} onGone={() => { newChat(); void qc.invalidateQueries({ queryKey: ['chat-projects'] }); }}
                    onPreview={(f) => openPreview({ kind: 'file', src: `/api/chat/attachments/${f.id}/file`, fileName: f.fileName })}
                    composer={(
                      <Composer big busy={busy} attach={attachProps} plus={plus} chips={chips}
                        placeholder={`在「${project?.name ?? '这个项目'}」里问点什么`}>{thinkPicker}</Composer>
                    )} />
                ) : (
                  <Welcome has={has} name={profile?.displayName ?? ''}
                    composer={(
                      <Composer big attach={attachProps} plus={plus} chips={chips}
                        placeholder={attachProps ? '问点什么，或把客户文件拖进来' : '问点什么'}>{thinkPicker}</Composer>
                    )}
                    onPick={(q) => void send(q)} onOpenDoc={(id) => nav(`/generate?session=${id}`)} />
                )}
              </ThreadPrimitive.Empty>
              <ThreadPrimitive.Messages components={{ UserMessage, AssistantMessage }} />
            </ThreadPrimitive.Viewport>

            {msgs.length > 0 && (
              <div className="chat-composer">
                {!genSessionId && (sessionFiles.data?.length ?? 0) > 0 && (
                  <div className="sess-files" aria-label="本对话的文件">
                    <span className="hint">本对话的文件</span>
                    {sessionFiles.data!.map((f) => (
                      <button key={f.id} type="button" className="sess-file" title={f.status === 'Failed' ? f.error ?? '解析失败' : `${f.fileName} · ${formatSize(f.size)} · 点开预览`}
                        onClick={() => openPreview({ kind: 'file', src: `/api/chat/attachments/${f.id}/file`, fileName: f.fileName })}>
                        <Icon name="file" size={13} />{f.fileName}{f.status === 'Failed' && <b>（没读出来）</b>}
                      </button>
                    ))}
                  </div>
                )}
                <Composer busy={busy} placeholder={genSessionId ? '接着说这份文档的内容，或问我别的' : '接着问，或说下一件要做的事'}
                  attach={!genSessionId ? attachProps : undefined} plus={genSessionId ? undefined : plus} chips={genSessionId ? undefined : chips}>
                  {!genSessionId && thinkPicker}
                </Composer>
              </div>
            )}
          </ThreadPrimitive.Root>
        </div>

        {genSessionId && showPreview && !panel && (
          <LivePreview sessionId={genSessionId} title={genTitle} revision={revision}
            onClose={() => setShowPreview(false)} />
        )}
        {panel && (
          <PreviewPanel panel={panel} htmlVersions={htmlVersions} webTabs={webTabs} webActive={webActive} sessionId={qaSessionId}
            setPanel={setPanel} setWebActive={setWebActive} closeWebTab={closeWebTab}
            openWeb={(url) => openPreview({ kind: 'web', url })} setLive={setLive} adoptTabs={adoptTabs} onClose={() => setPanel(null)} />
        )}
        {!panel && !(genSessionId && showPreview) && refs && refMsg?.evidence && (
          <RefPanel key={refs.msgId} used={refMsg.evidence.used} other={refMsg.evidence.other} focus={refs.focus}
            onFocus={(n) => setRefs({ msgId: refs.msgId, focus: n })} onClose={() => setRefs(null)}
            onOpenWeb={(n) => { const it = webItem(refMsg.evidence!.used, n); if (it) openPreview(it); }} />
        )}
        {pptPick && <PptPicker value={ppt} onPick={setPpt} onClose={() => setPptPick(false)} skillName={skill?.ppt ? skill.name : undefined} />}
        {projectDlg && (
          <ProjectDialog edit={typeof projectDlg === 'string' ? null : projectDlg} onClose={() => setProjectDlg(null)}
            onDone={(pid) => {
              const how = projectDlg;
              setProjectDlg(null);
              void qc.invalidateQueries({ queryKey: ['chat-projects'] });
              void qc.invalidateQueries({ queryKey: ['chat-project', pid] });
              // 「＋」里新建的：这条对话已经开了就放进去；其余新建的都进项目首页
              if (how === 'here' && qaSessionId) void moveProject(pid);
              else if (how === 'new' || how === 'here') newChat(pid);
            }} />
        )}
        </div>
        </PreviewCtx.Provider>
      </MsgCtx.Provider>
    </AssistantRuntimeProvider>
  );
}

type Mode = 'auto' | 'knowledge' | 'ledger' | 'generate';

const ALL_SCOPE: Scope = { kbId: null, docCategory: null };

interface AttachProps { files: PendingFile[]; onAdd: (f: FileList | File[]) => void; onRemove: (key: string) => void }

/** 输入框：上面是带的文件，中间打字（跟着内容长高，到半屏左右在框里滚），下面一条：左边「＋」和选过的小标签，右边思考档位和发送。
    首页是大框（默认 3 行）。也可以把文件直接拖到框上。 */
function Composer({ placeholder, big, attach, plus, chips, children, busy = false }: {
  placeholder: string; big?: boolean; attach?: AttachProps;
  /** 「＋」菜单里除了传文件以外的几项 */
  plus?: Omit<PlusProps, 'onUpload'>;
  /** 「＋」旁边的小标签：选过的技能、资料范围 */
  chips?: ReactNode;
  /** 右边、发送前面：思考档位 */
  children?: ReactNode;
  /** 助手正在回答：输入框上的小人坐到右端抱着笔记本等 */
  busy?: boolean;
}) {
  const pick = useRef<HTMLInputElement>(null);
  const [drag, setDrag] = useState(false);
  return (
    <ComposerPrimitive.Root className={'composer has-pet' + (big ? ' big' : '') + (drag ? ' drag' : '')}
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
      {/* 滚到底时不抢焦点：右边「可操作」的网页画面上打字时，字要留在网页里 */}
      <ComposerPrimitive.Input className="composer-input" minRows={big ? 3 : 1} autoFocus unstable_focusOnScrollToBottom={false}
        aria-label="输入你的问题或任务" placeholder={placeholder} />
      <div className="composer-bar">
        <PlusMenu {...plus} onUpload={attach ? () => pick.current?.click() : undefined} />
        {attach && (
          <input ref={pick} type="file" multiple accept={ATTACH_ACCEPT} hidden
            onChange={(e) => { if (e.target.files?.length) attach.onAdd(e.target.files); e.target.value = ''; }} />
        )}
        <span className="composer-chips">{chips}</span>
        {children}
        <ComposerPrimitive.Send className="composer-send" aria-label="发送">
          <Icon name="up" size={18} stroke={2.2} />
        </ComposerPrimitive.Send>
      </div>
      {drag && <div className="composer-drop">松开即可添加文件</div>}
      <InputPet busy={busy} size={big ? 40 : 32} />
    </ComposerPrimitive.Root>
  );
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

/** 首页（对话还没开始时）：吉祥物 + 今天的台词、大输入框、最要紧的几件正事、歇一会、表情包（见 home/Home）。 */
function Welcome({ has, name, composer, onPick, onOpenDoc }: {
  has: (p: string) => boolean; name: string; composer: ReactNode;
  onPick: (q: string) => void; onOpenDoc: (sessionId: string) => void;
}) {
  const docs = useQuery({
    queryKey: ['gen-sessions'], enabled: has(Perm.Generate),
    queryFn: () => get<SessionRowT[]>('/api/generate/sessions'),
  });
  const open = (docs.data ?? []).filter((d) => d.status !== 'Completed');
  const recent = (docs.data ?? []).slice(0, 3);
  return (
    <Home fallbackName={name} onPick={onPick}
      drafts={open.length} onDrafts={() => { if (open[0]) onOpenDoc(open[0].id); }}
      composer={composer}
      resume={recent.length > 0 ? (
        <div className="hm-resume">
          <span>接着上次：</span>
          {recent.map((d) => {
            const finished = d.status === 'Completed';
            return (
              <button key={d.id} type="button" onClick={() => onOpenDoc(d.id)} title={ago(d.updatedAt)}>
                {d.templateName}<i>{finished ? '已生成' : `${d.done}/${d.total}`}</i>
              </button>
            );
          })}
        </div>
      ) : null} />
  );
}

function UserMessage() {
  const id = useAuiState((s) => s.message.id);
  const { byId } = useContext(MsgCtx);
  const files = byId.get(id)?.files;
  const skill = byId.get(id)?.skill;
  return (
    <MessagePrimitive.Root className="chat-user">
      {skill && <span className="sess-file" title="这一问点名用的技能"><Icon name="book" size={13} />技能：{skill}</span>}
      {files && files.length > 0 && (
        <div className="chat-user-files">
          {files.map((n) => <span key={n} className="sess-file"><Icon name="file" size={13} />{n}</span>)}
        </div>
      )}
      <div className="chat-user-bubble"><MessagePrimitive.Parts /></div>
    </MessagePrimitive.Root>
  );
}

const HIDDEN_STEPS = new Set(['plan', 'use_skill']);
const OFF_KEY = 'htlab.plugins.off';

/** 助手一条：正文按标记语法排版，后面挂这一轮长出来的结果件。 */
function AssistantMessage() {
  const id = useAuiState((s) => s.message.id);
  const { byId, busy, focusSource, setFocusSource, pickTemplate, genTurn, correctIntent, genSessionId, refs, showRefs, sendText, openPreview, deepOk, deepAgain } = useContext(MsgCtx);
  const m = byId.get(id);
  if (!m) return null;
  const isLast = [...byId.keys()].pop() === id;
  const active = isLast && !busy;

  // 正在回答：小人跳进这条回答里干活（左边的头像先空着，免得两个小人）
  const staging = !!m.streaming && !m.agentSteps;
  // 「列计划」「翻看技能」不算查了几步，也不摆给用户看（技能是助手自己的答题指南）
  const plainSteps = (m.steps ?? []).filter((s) => !HIDDEN_STEPS.has(s.tool));
  return (
    <MessagePrimitive.Root className={'chat-assistant' + (staging ? ' staging' : '')}>
      <BrandMark />
      <div className="chat-bubble">
        {m.intent === 'general' && (
          <div className="hint" style={{ marginBottom: 7 }}>通用回答 · 未引用企业资料</div>
        )}
        {m.notice && <div className="hint" style={{ marginBottom: 7 }}>{m.notice}</div>}
        {m.agentSteps && <AgentProgressCard steps={m.agentSteps} streaming={m.streaming} />}
        {m.plan && m.plan.items.length > 0
          ? <PlanCard plan={m.plan} steps={m.steps ?? []} streaming={!!m.streaming} seconds={m.seconds} />
          : <>
            {staging && <ThinkingStage steps={plainSteps} hasText={!!m.text} />}
            {m.steps && !staging && <StepsCard steps={plainSteps} streaming={m.streaming} seconds={m.seconds} />}
          </>}
        {m.agents?.map((c) => <AgentsCard key={c.id} card={c} live={!!m.streaming} />)}
        {m.artifacts && m.artifacts.length > 0 && <ArtifactCards items={m.artifacts} onSend={active ? sendText : undefined} />}
        {m.text && <Prose text={m.text} live={m.streaming}
          flags={m.verify?.issues.map((i) => i.value)}
          focus={refs?.msgId === id ? refs.focus : null}
          onCite={m.evidence ? (n) => {
            // 网上的来源直接在右侧打开那个网页；公司资料照旧看参考资料
            const web = webItem(m.evidence!.used, n);
            if (web) { showRefs(id, n); openPreview(web); } else showRefs(id, n);
          } : m.sources ? (n) => setFocusSource(n) : undefined} />}
        {m.text && !m.streaming && isLongDoc(m.text) && (
          <button type="button" className="as-refs-btn" onClick={() => openPreview({ kind: 'doc', title: (lastQuestion(byId, id) || '助手回答').slice(0, 30), markdown: m.text })}>
            在面板里按文档看（有目录，可导出 Word / PDF）<Icon name="right" size={12} className="gi" />
          </button>
        )}
        {m.ask && <AskCard ask={m.ask} active={active && !m.ask.answered} onAnswer={sendText} />}
        {m.verify && !m.streaming && <VerifyBar report={m.verify} onCite={(n) => showRefs(id, n)} />}
        {m.evidence && !m.streaming && m.text && (
          <ExportBar text={m.text} sources={m.evidence.used} title={(lastQuestion(byId, id) || '助手回答').slice(0, 30)} />
        )}
        {m.evidence && m.evidence.used.length > 0 && !m.streaming && refs?.msgId !== id && (
          <button type="button" className="as-refs-btn" onClick={() => showRefs(id)}>
            参考资料 {new Set(m.evidence.used.map((c) => c.docId ?? c.kind + (c.fileName ?? ''))).size} 份 · {m.evidence.used.length} 段原文<Icon name="right" size={12} className="gi" />
          </button>
        )}
        {m.truncated && !m.streaming && (
          <div className="as-trunc" role="status">
            <Icon name="warn" size={15} stroke={2} /><b style={{ fontWeight: 500 }}>回答太长，写到一半停了</b>
            {deepOk && m.level !== 'deep'
              ? <button type="button" className="gbtn" disabled={busy} onClick={() => deepAgain(lastQuestion(byId, id))}>
                  <Icon name="refresh" size={13} style={{ verticalAlign: '-2px', marginRight: 4 }} />用深度再答一遍</button>
              : <span className="hint">可以把问题拆小一点，分几次问</span>}
          </div>
        )}
        {m.messageId && !m.streaming && (m.text || m.noResult) && (
          <FeedbackBar key={m.messageId} messageId={m.messageId} helpful={m.helpful} reason={m.feedbackReason}
            extra={deepOk && m.steps && m.level !== 'deep' && !m.truncated && lastQuestion(byId, id) ? (
              <button type="button" className="fb-btn" disabled={busy} title="同一个问题，多想、多查几轮再答一遍"
                onClick={() => deepAgain(lastQuestion(byId, id))}><Icon name="layers" size={14} />深度再答</button>
            ) : null} />
        )}
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
        {active && m.gen?.canForce && !m.gen.rendered && (
          <div style={{ marginTop: 12, display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            <button className="pbtn" onClick={() => void genTurn({ render: true, forceRender: true })}>
              仍然生成（空项留白）
            </button>
            <span className="hint">先出一版看整体，回来接着补，再生成一版</span>
          </div>
        )}
        {m.gen?.rendered && m.genSessionId && (
          <div style={{ marginTop: 9 }}>
            <button className="pbtn"
              onClick={() => void download(`/api/generate/sessions/${m.genSessionId}/output`, m.gen!.rendered!.fileName)}>
              下载《{m.gen.rendered.fileName}》<Icon name="download" size={13} className="gi" />
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
      case 'assistant': {
        const a = parsed as unknown as { steps?: AssistantStep[]; evidence?: Msg['evidence']; verify?: VerifyReport; seconds?: number;
          ask?: AskSpec; answered?: string; artifacts?: ToolArtifact[]; level?: string; truncated?: boolean; agents?: AgentsCardData[]; plan?: PlanState | null };
        return {
          steps: a.steps ?? [], evidence: a.evidence, verify: a.verify, seconds: a.seconds ?? null, artifacts: a.artifacts,
          level: a.level, truncated: a.truncated, agents: a.agents, plan: a.plan,
          ...(a.ask ? { ask: { ...a.ask, answered: a.answered ?? null }, text: '' } : {}),
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
