// PPT 模板库：能选的模板清单、封面图地址（选择窗、换个风格、输入框「＋」都用）。
import { useQuery } from '@tanstack/react-query';
import { get } from '../../lib/api';
import type { PptTemplateRow } from '../../lib/types';

export const coverOf = (t: { id: string }) => `/api/ppt-templates/${t.id}/preview/1`;

export function usePptTemplates(enabled = true) {
  return useQuery({ queryKey: ['ppt-templates'], staleTime: 300_000, enabled, queryFn: () => get<PptTemplateRow[]>('/api/ppt-templates') });
}
