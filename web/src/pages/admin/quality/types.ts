// 后台「回答质量」用到的数据形状和文案。

export interface QualitySummary {
  days: number; answers: number; users: number; helpful: number; notHelpful: number; prevNotHelpful: number;
  noEvidence: number; unverified: number; open: number; oldestOpen: string | null;
}

export type IssueSource = 'Feedback' | 'NoEvidence' | 'Unverified';
export type IssueStatus = 'Open' | 'Resolved' | 'Dismissed';

export interface IssueRow {
  id: string; source: IssueSource; reason: string | null; category: string | null; status: IssueStatus;
  createdAt: string; evalQuestionId: string | null; question: string; person: string | null; dept: string | null;
}

export interface PayloadStep { id: string; tool: string; label: string; detail?: string | null; result?: string | null; status: string; seconds?: number | null }
export interface PayloadCard { n: number; kind: string; title: string; docId?: string | null; section?: string | null; pageNo?: number | null; fileName?: string | null; projectNo?: string | null; score?: number | null }
export interface PayloadSearch { query: string; top: number; found: number; hints?: string[] }
export interface IssuePayload {
  steps?: PayloadStep[];
  evidence?: { used: PayloadCard[]; other: PayloadCard[] };
  searches?: PayloadSearch[];
  seconds?: number;
}

export interface IssueDetail extends Omit<IssueRow, 'question'> {
  comment: string | null; notify: boolean; resolveNote: string | null; resolvedAt: string | null;
  messageId: string; question: string; answer: string | null; answeredAt: string;
  payload: IssuePayload | null; threshold: number; facts: string[];
}

export interface EvalQuestion {
  id: string; code: string; question: string; expectDocs: string[]; expectProjects: string[]; expectPoints: string[];
  expectAsk: boolean; dept: string | null; source: string; issueId: string | null; createdAt: string;
  history: { retrievalOk: boolean | null; answerOk: boolean | null }[];
}

export type RunStatus = 'Running' | 'Done' | 'Failed';
export interface EvalRunRow {
  id: string; status: RunStatus; total: number; finished: number; retrievalTotal: number; retrievalOk: number;
  answerTotal: number; answerOk: number; runAs: string; changes: string | null; error: string | null;
  startedAt: string; finishedAt: string | null;
}
export interface EvalResultRow {
  questionId: string; code: string; question: string; dept: string | null; retrievalOk: boolean | null; answerOk: boolean | null;
  askOk: boolean; missingPoints: string[]; foundDocs: string[]; answer: string | null; error: string | null; seconds: number;
  trend: 'new' | 'same' | 'better' | 'worse';
}
export interface EvalRunDetail extends EvalRunRow { previousRunId: string | null; results: EvalResultRow[] }

export const SOURCE_LABEL: Record<IssueSource, string> = { Feedback: '员工反馈', NoEvidence: '查了没用上', Unverified: '数字对不上' };
export const STATUS_LABEL: Record<IssueStatus, string> = { Open: '待处理', Resolved: '已处理', Dismissed: '不是问题' };

/** 「错在哪一步」，代码和服务端 QualityController.Categories 一致。 */
export const CATEGORIES = [
  { code: 'missing', label: '资料里没有' },
  { code: 'parse', label: '解析错了' },
  { code: 'alias', label: '叫法不同，没搜到' },
  { code: 'crowded', label: '搜到了被挤掉' },
  { code: 'model', label: '模型答错了' },
  { code: 'not_issue', label: '不是问题' },
] as const;

export const pct = (ok: number, total: number) => (total > 0 ? Math.round((ok / total) * 100) : null);

/** 和小人冒汗、服务端 IssueFacts 同一个判断：这一步其实没查到东西。 */
export const looksEmpty = (s: PayloadStep) => s.status === 'error' || /^(0\s|没|无|空)|不对/.test(s.result ?? '');

export function when(iso: string): string {
  const d = new Date(iso);
  const now = new Date();
  const hm = `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  if (d.toDateString() === now.toDateString()) return `今天 ${hm}`;
  const y = new Date(now); y.setDate(now.getDate() - 1);
  if (d.toDateString() === y.toDateString()) return `昨天 ${hm}`;
  return `${d.getMonth() + 1}-${String(d.getDate()).padStart(2, '0')}`;
}
