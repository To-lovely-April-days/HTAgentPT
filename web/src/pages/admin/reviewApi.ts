/** 同一套界面两处用：后台语料管理（管理员，看全部）和主界面「审批」（部门主管，只看本部门及下级）。 */
export interface ReviewApi { key: string; list: string; approve: string; reject: string }
export const ADMIN_REVIEW_API: ReviewApi = {
  key: 'docs-review', list: '/api/documents?review=Pending', approve: '/api/documents/review/approve', reject: '/api/documents/review/reject',
};
export const HEAD_REVIEW_API: ReviewApi = {
  key: 'approvals-files', list: '/api/approvals/files', approve: '/api/approvals/files/approve', reject: '/api/approvals/files/reject',
};

