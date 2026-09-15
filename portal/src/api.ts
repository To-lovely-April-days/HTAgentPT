// 门户 API 客户端。只触达 /api/portal/*（匿名）与 /api/auth、/api/customer（登录后）。
// 不引用内网任何代码——两套独立构建产物。

const TOKEN_KEY = 'ht.portal.token';

export function getToken(): string | null {
  try { return localStorage.getItem(TOKEN_KEY); } catch { return null; }
}
export function setToken(t: string | null) {
  try {
    if (t) localStorage.setItem(TOKEN_KEY, t);
    else localStorage.removeItem(TOKEN_KEY);
  } catch { /* 隐私模式下静默 */ }
}

function terminalId(): string {
  try {
    let id = localStorage.getItem('ht.portal.terminal');
    if (!id) {
      id = 'portal-' + Math.random().toString(36).slice(2, 10);
      localStorage.setItem('ht.portal.terminal', id);
    }
    return id;
  } catch { return 'portal-unknown'; }
}

export class ApiError extends Error {
  code: string;
  status: number;
  constructor(code: string, message: string, status: number) {
    super(message);
    this.code = code;
    this.status = status;
  }
}

async function parseError(resp: Response): Promise<ApiError> {
  try {
    const body = await resp.json() as { code?: string; message?: string };
    return new ApiError(body.code ?? 'ERROR', body.message ?? '请求失败，请稍后重试', resp.status);
  } catch {
    return new ApiError('ERROR', '网络不稳定，请稍后重试', resp.status);
  }
}

export async function api<T>(path: string, init?: { method?: string; body?: unknown; auth?: boolean }): Promise<T> {
  const headers = new Headers();
  if (init?.body !== undefined) headers.set('Content-Type', 'application/json');
  if (init?.auth) {
    const token = getToken();
    if (token) headers.set('Authorization', `Bearer ${token}`);
    headers.set('X-Terminal-Id', terminalId());
  }
  const resp = await fetch(path, {
    method: init?.method ?? (init?.body !== undefined ? 'POST' : 'GET'),
    headers,
    body: init?.body !== undefined ? JSON.stringify(init.body) : undefined,
  });
  if (resp.status === 401 && init?.auth) {
    setToken(null);
    throw await parseError(resp);
  }
  if (!resp.ok) throw await parseError(resp);
  if (resp.status === 204) return undefined as T;
  return await resp.json() as T;
}

// ── 类型 ────────────────────────────────────────────────
export interface PortalHit { docTitle: string; section: string | null; pageNo: number | null; excerpt: string; }
export interface PortalTicketCreated { ticketNo: string; createdAt: string; }
export interface PortalTicketNode { at: string; status: string; message: string | null; }
export interface PortalTicketView { ticketNo: string; status: string; deviceNo: string; createdAt: string; nodes: PortalTicketNode[]; }
export interface DeviceRow { id: string; customerNo: string; deviceNo: string; model: string; projectNo: string | null; deliveredAt: string | null; deliveryStatus: string | null; }
export interface TicketRow {
  id: string; ticketNo: string; deviceNo: string; description: string; status: string;
  trail: { at: string; by: string; status: string; note: string | null; forCustomer: boolean }[];
  createdAt: string; updatedAt: string;
}

export const STATUS_LABEL: Record<string, string> = {
  Submitted: '已提交', Assigned: '已受理', InProgress: '处理中', Resolved: '已解决', Closed: '已关闭',
};
