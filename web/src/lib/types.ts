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
}

export interface Source {
  index: number; chunkId: number; docId: string; docTitle: string;
  section: string | null; pageNo: number | null; score: number;
  classification: Classification; excerpt: string;
}

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
