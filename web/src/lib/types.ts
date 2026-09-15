// 与后端契约对应的最小类型集（第 1 批用到的部分）。

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
