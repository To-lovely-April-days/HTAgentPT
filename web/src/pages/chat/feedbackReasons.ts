/** 「哪里不对」的选项。代码和服务端 QaSessionsController.Reasons 一致。 */
export const FEEDBACK_REASONS = [
  { code: 'no_docs', label: '没找到资料' },
  { code: 'wrong_docs', label: '找错了资料' },
  { code: 'wrong', label: '答错了' },
  { code: 'incomplete', label: '不完整' },
  { code: 'other', label: '其他' },
] as const;

export const REASON_LABEL: Record<string, string> = Object.fromEntries(FEEDBACK_REASONS.map((r) => [r.code, r.label]));
