// 这个人在预览里能用哪些：读网页（阅读模式）、运行 Python。跟助手的工具开关、角色名单是同一套。
import { useQuery } from '@tanstack/react-query';
import { get } from '../../lib/api';

export interface Caps { web: boolean; python: boolean }

export function useCaps(): Caps {
  const q = useQuery({ queryKey: ['web-caps'], queryFn: () => get<Caps>('/api/web/caps'), staleTime: 5 * 60_000, retry: false });
  return q.data ?? { web: false, python: false };
}
