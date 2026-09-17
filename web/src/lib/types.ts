// 与后端契约对应的最小类型集。
import type React from 'react';

export type Classification = 'Confidential' | 'Internal' | 'Public';

export interface UserProfile {
  id: string;
  username: string;
  displayName: string;
  roleCode: string;
  roleName: string;
  companyId: string;
  companyName: string;
  classifications: Classification[];
  permissions: string[];
  customerNo: string | null;
}

export interface LoginResponse {
  token: string;
  profile: UserProfile;
  supersededOther: boolean;
}

export interface QaSessionRow { id: string; title: string; createdAt: string; updatedAt: string; }

export interface QaMessageRow {
  id: string; question: string; rewrittenQuery: string | null; answer: string | null;
  sources: string | null; noResultHints: string | null; helpful: boolean | null; at: string;
  /** 这一轮长出来的结果件（jsonb 字符串）：{kind,data}，打开历史对话时照着重建 */
  payload?: string | null;
  intent?: string | null;
}

export interface Source {
  index: number; chunkId: number; docId: string; docTitle: string;
  section: string | null; pageNo: number | null; score: number;
  classification: Classification; excerpt: string;
  /** 该来源页上的解析图片（FR-4.9 来源出图）；内容经 /api/files/{docId}/images/{id} 鉴权取。 */
  images?: SourceImage[];
}

export interface SourceImage { id: number; caption: string | null; pageNo: number | null; }

export interface ProjectRow {
  projectNo: string; customerName: string; year: number; deviceType: string;
  deviceModel: string | null; specParams: string | null; contractAmount: number | null;
  deliveryStatus: string | null; ownerId: string | null; ownerName: string | null; updatedAt: string;
}

export interface ProjectSearchResult {
  rows: ProjectRow[];
  /** 金额列是否在结果中——由服务端按角色裁剪决定，前端只是照着渲染。 */
  amountVisible: boolean;
  /** 带了金额筛选但无金额可见权限：条件被忽略，需如实告知。 */
  amountFilterIgnored: boolean;
}

export interface ProjectDocRow {
  docId: string; title: string; docCategory: string;
  classification: Classification; parseStatus: string; uploadedAt: string;
}

export interface ProjectDetail { row: ProjectRow; documents: ProjectDocRow[]; }

export const DELIVERY_LABEL: Record<string, string> = {
  InProgress: '进行中', Delivered: '已交付', Closed: '已结项',
};

// ── 故障案例（B 组）───────────────────────────────────────────
export type CaseSyncStatus = 'Local' | 'Pending' | 'Shared' | 'Rejected';

export interface CaseRow {
  id: string; caseNo: string; deviceModel: string; alarmCode: string | null;
  phenomenon: string; result: string; syncStatus: CaseSyncStatus;
  sourceCompany: string | null; createdAt: string; updatedAt: string;
}

export interface CaseModelGroup {
  deviceModel: string; count: number; ownCount: number; sharedCount: number; top: CaseRow[];
}

export interface CaseDetailData {
  id: string; caseNo: string; deviceModel: string; alarmCode: string | null;
  phenomenon: string; causeAnalysis: string; steps: string; spareParts: string | null;
  result: string; extra: Record<string, string>; syncStatus: CaseSyncStatus;
  rejectReason: string | null; sourceCompany: string | null; createdById: string;
  createdAt: string; updatedAt: string; chunkId: number | null;
}

export interface SensitiveHit { field: string; kind: string; match: string; }

// ── 语料管理（E 组）───────────────────────────────────────────
export type KbTier = 'Shared' | 'Private' | 'Public';
export type ParseStatus = 'NotParsed' | 'Queued' | 'Parsing' | 'Parsed' | 'Failed' | 'Waiting' | 'Reparsing';
export type ChunkStrategy = 'ByHeading' | 'ByClause' | 'ByRow' | 'BySemantic' | 'General';

export interface KbRow {
  id: string; name: string; tier: KbTier; defaultChunkStrategy: ChunkStrategy;
  description: string | null; isActive: boolean; docCount: number; chunkCount: number;
  lastUpdatedAt: string | null;
}

export interface DocRow {
  id: string; title: string; fileName: string; kbId: string; kbName: string;
  classification: Classification; parseStatus: ParseStatus; parseError: string | null;
  fileSize: number; uploadedAt: string; docCategory: string | null;
  customerName: string | null; projectNo: string | null;
}

export interface ParseJobRow {
  id: string; docId: string; docTitle: string; kind: string; status: string;
  attempts: number; lastError: string | null;
  queuedAt: string; startedAt: string | null; finishedAt: string | null;
}

export const KB_TIER_LABEL: Record<KbTier, string> = {
  Shared: '集团共享库', Private: '本公司私有库', Public: '对外公开库',
};
export const PARSE_LABEL: Record<ParseStatus, string> = {
  NotParsed: '待解析', Queued: '排队中', Parsing: '解析中', Parsed: '已完成', Failed: '失败',
  // 「等待」不是「失败」（10.3）：引擎不可用时任务挂起自动重试，须与失败分开呈现
  Waiting: '等待中', Reparsing: '重解析中',
};
export const STRATEGY_LABEL: Record<string, string> = {
  ByHeading: '按章节层级', ByClause: '按条款', ByRow: '按行（表头作前缀）', BySemantic: '按语义段落', General: '通用',
};
/** 表 4-5：这些类别的文档必须关联项目编号（条件必填）。 */
export const PROJECT_LINKED_CATEGORIES = ['方案', '合同', '报价', '图纸', '交付报告', '故障记录'];

// ── 报修工单（B5/B6，FR-8.8 最简流转）─────────────────────────
export type TicketStatus = 'Submitted' | 'Assigned' | 'InProgress' | 'Resolved' | 'Closed';
export interface TicketTrailEntry { at: string; by: string; status: string; note: string | null; forCustomer: boolean; }
export interface TicketRow {
  id: string; ticketNo: string; customerNo: string; deviceNo: string; description: string;
  contact: string; status: TicketStatus; assigneeId: string | null; assigneeName: string | null;
  trail: TicketTrailEntry[]; createdAt: string; updatedAt: string;
}
export const TICKET_LABEL: Record<TicketStatus, string> = {
  Submitted: '已提交', Assigned: '已受理', InProgress: '处理中', Resolved: '已解决', Closed: '已关闭',
};

// ── 方案生成（A4-A12）与条款库（FR-5.18）───────────────────────
export type SlotFillSource = 'Template' | 'Inherited' | 'AiSuggested' | 'Confirmed';

export interface TemplateRowT {
  id: string; name: string; docType: string; isEnabled: boolean;
  slotCount: number; incompleteSlots: number; updatedAt: string; lastUsedAt: string | null;
}

export interface SlotDef {
  id: string; tag: string; name: string; section: string; dataType: string;
  unit: string | null; choices: string | null; required: boolean; stage: 'Current' | 'Later';
  forbidInherit: boolean; prompt: string | null; suggestSource: string | null;
  subFields: string | null; sortOrder: number;
}
export interface SlotState {
  tag: string; value: string | null; source: SlotFillSource | null; confirmed: boolean;
  userTouched: boolean; origin: string | null; updatedAt: string | null;
}
export interface SlotView { def: SlotDef; state: SlotState; }

export interface SessionView {
  id: string; templateId: string; templateName: string; projectHint: string | null;
  baseProjectNo: string | null; status: 'Draft' | 'Completed'; slots: SlotView[];
  updatedAt: string; outputFileName: string | null;
}
export interface SessionRowT {
  id: string; templateName: string; baseProjectNo: string | null; status: 'Draft' | 'Completed';
  total: number; done: number; updatedAt: string;
}
export interface BaseCandidate {
  projectNo: string; customerName: string; year: number; deviceType: string;
  deviceModel: string | null; specParams: string | null; deliveryStatus: string | null;
  inheritableSlots: number; linkedDocs: number;
}
export interface SuggestEvidence { sourceTitle: string; section: string | null; pageNo: number | null; excerpt: string; chunkId: number | null; }
export interface SlotSuggestion { tag: string; value: string | null; evidence: SuggestEvidence[]; hasEvidence: boolean; note: string | null; }
export interface CompletenessView {
  canRender: boolean; total: number; done: number;
  incomplete: { tag: string; name: string; section: string; reason: string }[];
}
export interface PreviewView {
  sections: { section: string; items: { tag: string; name: string; value: string | null; sourceLabel: string; origin: string | null }[] }[];
  pdfFileKey: string | null;
}
export interface RenderResult { sessionId: string; outputFileName: string; slotsFilled: number; leftBlank: number; }

// ── 对话式生成（A4 聊天形态）──────────────────────────────────
export interface GenChatMsg { id: number; role: 'user' | 'assistant'; content: string; payload: string | null; at: string; }
export interface GenChatProgress { total: number; done: number; canRender: boolean; outputFileName: string | null; }
export interface GenChatStateView { messages: GenChatMsg[]; progress: GenChatProgress; }
export interface GenChatTurnResult { newMessages: GenChatMsg[]; progress: GenChatProgress; }
export interface GenAsk {
  tag: string; name: string; section: string; dataType: string;
  choices: string[] | null; prompt: string | null; unit: string | null; required: boolean;
  /** 有 AI 建议待确认时的建议值 */
  suggested: string | null;
}
export interface GenSuggestion {
  tag: string; name: string; value: string | null; hasEvidence: boolean; note: string | null;
  evidence: { sourceTitle: string; section: string | null; pageNo: number | null }[];
}
/** 工况顾问的建议：模型按工艺常识给的判断，没有文档依据，界面上与资料建议分开。 */
export interface GenAdvice {
  tag: string; name: string; value: string;
  current: string | null; reason: string | null; risk: string | null;
  /** 要紧程度：high=安全相关/不改会出事，界面标红并排在前面 */
  level: 'high' | 'normal';
  /** 相对当前填写：change 要改、fill 待补、keep 与现值一致 */
  kind: 'change' | 'fill' | 'keep';
}
/** 助手消息 payload（jsonb 字符串反序列化后）：都是可选段，前端有则渲染对应交互件。 */
export interface GenChatPayload {
  asks?: GenAsk[];
  baseCandidates?: { projectNo: string; customerName: string; year: number; deviceType: string; deviceModel: string | null; inheritableSlots: number }[] | null;
  suggestions?: GenSuggestion[] | null;
  advices?: GenAdvice[] | null;
  summary?: { section: string; items: { name: string; value: string | null }[] }[] | null;
  canRender?: boolean | null;
  rendered?: { fileName: string; filled: number; blank: number } | null;
}
/** 问答分流事件里携带的模板推荐（点选即开聊）。 */
/** 台账查询的结构化结果（FR-4.1）：不经模型生成，筛选条件由提问解析而来。 */
export interface LedgerTable {
  filters: { customer: string | null; deviceType: string | null; yearFrom: number | null; yearTo: number | null };
  rows: ProjectRow[]; amountVisible: boolean; note: string;
  /** 只命中一条时服务端直接带上项目档案，不用再点一次 */
  detail?: ProjectDetail | null;
}


export interface QaTemplateRec { id: string; name: string; docType: string; slotCount: number; }

export interface ClauseRow {
  id: string; category: string; code: string; title: string; text: string; status: string;
  approvedByName: string | null; effectiveDate: string | null; supersedesId: string | null; createdAt: string;
}
export interface ClauseAssembly { clauses: ClauseRow[]; assembledText: string; }

/** 四种填充来源的标记（5.2.5）：紫色一档为「待人工确认的未决状态」，不借密级三色。 */
export const SOURCE_LABEL: Record<SlotFillSource, string> = {
  Template: '模板固定', Inherited: '继承', AiSuggested: 'AI 建议待确认', Confirmed: '已确认',
};
export const SOURCE_PILL_STYLE: Record<SlotFillSource, React.CSSProperties> = {
  Template: { background: '#eef1f5', color: '#5a6673', border: '1px solid #dde3ea' },
  Inherited: { background: 'var(--accent-bg)', color: 'var(--accent)', border: '1px solid var(--accent-line)' },
  AiSuggested: { background: '#fbeff7', color: '#8f3d74', border: '1px solid #edd2e3' },
  Confirmed: { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' },
};

// ── 治理域（E5-E16）──────────────────────────────────────────
export interface UserRow {
  id: string; username: string; displayName: string; employeeNo: string | null;
  department: string | null; roleCode: string | null; roleName: string | null;
  companyName: string; kind: 'Employee' | 'Customer'; customerNo: string | null;
  isActive: boolean; lastLoginAt: string | null; createdAt: string;
}
export interface RoleRow {
  id: string; code: string; name: string; classifications: Classification[];
  permissions: string[]; isSystem: boolean;
}
export interface PublishRow {
  recordId: string; sourceDocId: string; sourceTitle: string; publicDocId: string;
  publicDocStatus: string | null; status: string; operatorName: string;
  createdAt: string; confirmedAt: string | null; withdrawnAt: string | null; withdrawReason: string | null;
}
export interface SyncResult {
  batchNo: string; docsUpserted: number; chunksWritten: number; vectorsReused: number;
  docsQueuedForEmbedding: number; withdrawn: number; warnings: string[];
}

// ── 翻译（D 组）──────────────────────────────────────────────
export interface TermHit { zh: string; en: string; domain: string; }
export interface BilingualPair { source: string; target: string; }
export interface TextTranslationResult {
  translation: string;
  pairs: BilingualPair[];
  termsApplied: TermHit[];
  contractNotice: string | null;
}
export interface DocxReport { paragraphs: number; translated: number; unfillable: string[]; }
export interface FileTranslationResult {
  taskId: string;
  outputFileName: string;
  report: DocxReport;
  termsApplied: TermHit[];
  contractNotice: string | null;
}
export interface TermRow {
  id: string; domain: string; zh: string; en: string; note: string | null;
  status: string; submittedBy: string | null; createdAt: string;
}

export const SYNC_LABEL: Record<CaseSyncStatus, string> = {
  Local: '本地生效', Pending: '待总部审核', Shared: '已并入共享库', Rejected: '已驳回',
};
/** 四态徽章配色与 B3 原型一致：local 灰 / pending 琥珀 / shared 绿 / rejected 红。 */
export const SYNC_PILL_STYLE: Record<CaseSyncStatus, React.CSSProperties> = {
  Local: { background: '#eef1f5', color: '#5a6673', border: '1px solid #dde3ea' },
  Pending: { background: '#fdf4e3', color: '#8a5a00', border: '1px solid #f0dcb4' },
  Shared: { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' },
  Rejected: { background: '#fdeceb', color: '#b3261e', border: '1px solid #f5cdc9' },
};

export interface VocabRow { id: string; vocabKey: string; value: string; aliases: string | null; isActive: boolean; }

export const CLS_LABEL: Record<Classification, string> = {
  Confidential: '机密', Internal: '内部', Public: '公开',
};
export const CLS_PILL: Record<Classification, string> = {
  Confidential: 'pill pill-conf', Internal: 'pill pill-int', Public: 'pill pill-pub',
};

export const Perm = {
  QaInternal: 'qa.internal', QaPublic: 'qa.public', ProjectSearch: 'project.search',
  Generate: 'generate.doc', GenerateContract: 'generate.contract', Translate: 'translate',
  CaseRead: 'case.read', CaseWrite: 'case.write', CaseReview: 'case.review',
  TicketHandle: 'ticket.handle', CustomerSelf: 'customer.self',
  CorpusManage: 'corpus.manage', KbManage: 'kb.manage', MetaManage: 'meta.manage',
  TemplateManage: 'template.manage', UserManage: 'user.manage',
  SystemConfig: 'system.config', AuditRead: 'audit.read',
} as const;
