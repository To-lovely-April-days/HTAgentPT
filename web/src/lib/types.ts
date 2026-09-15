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
  deliveryStatus: string | null; updatedAt: string;
}

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
