// API 客户端：令牌、终端标识（决策 4）、统一错误、SSE 解析。

export class ApiError extends Error {
  code: string;
  status: number;
  constructor(code: string, message: string, status: number) {
    super(message);
    this.code = code;
    this.status = status;
  }
}

const TOKEN_KEY = 'ht.token';
const TERMINAL_KEY = 'ht.terminal';

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY);
}
export function setToken(t: string | null) {
  if (t) localStorage.setItem(TOKEN_KEY, t);
  else localStorage.removeItem(TOKEN_KEY);
}

/** 终端标识：浏览器首次生成后固定——同账号多终端互斥（决策 4）以它区分「哪一处在线」。 */
export function terminalId(): string {
  let id = localStorage.getItem(TERMINAL_KEY);
  if (!id) {
    id = 'web-' + crypto.randomUUID().slice(0, 12);
    localStorage.setItem(TERMINAL_KEY, id);
  }
  return id;
}

/** 会话被判无效的原因码（C4 系列异常态）——被顶下线要给人话，不是笼统的过期。 */
export const SESSION_END_CODES = ['SESSION_SUPERSEDED', 'SESSION_EXPIRED', 'ACCOUNT_DISABLED'] as const;

let onSessionEnd: ((code: string, message: string) => void) | null = null;
export function setSessionEndHandler(fn: typeof onSessionEnd) {
  onSessionEnd = fn;
}

async function parseError(resp: Response): Promise<ApiError> {
  let code = 'HTTP_' + resp.status;
  let message = `请求失败（HTTP ${resp.status}）`;
  try {
    const body = await resp.json();
    if (body?.code) code = body.code;
    if (body?.message) message = body.message;
  } catch { /* 空体 */ }
  return new ApiError(code, message, resp.status);
}

export async function api<T = unknown>(
  path: string,
  init?: RequestInit & { raw?: boolean },
): Promise<T> {
  const headers = new Headers(init?.headers);
  const token = getToken();
  if (token) headers.set('Authorization', `Bearer ${token}`);
  headers.set('X-Terminal-Id', terminalId());
  if (init?.body && typeof init.body === 'string') headers.set('Content-Type', 'application/json');

  const resp = await fetch(path, { ...init, headers });
  if (resp.status === 401) {
    const err = await parseError(resp);
    if ((SESSION_END_CODES as readonly string[]).includes(err.code)) {
      setToken(null);
      onSessionEnd?.(err.code, err.message);
    }
    throw err;
  }
  if (!resp.ok) throw await parseError(resp);
  if (init?.raw) return resp as unknown as T;
  if (resp.status === 204) return undefined as T;
  return (await resp.json()) as T;
}

export const get = <T>(path: string) => api<T>(path);
export const post = <T>(path: string, body?: unknown) =>
  api<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });
export const put = <T>(path: string, body?: unknown) =>
  api<T>(path, { method: 'PUT', body: JSON.stringify(body) });

/** SSE 事件（问答流）：event/data 帧解析，POST 携带体。 */
export interface SseEvent { kind: string; payload: unknown; }

export async function* sse(path: string, body: unknown, signal?: AbortSignal): AsyncGenerator<SseEvent> {
  const headers = new Headers({ 'Content-Type': 'application/json' });
  const token = getToken();
  if (token) headers.set('Authorization', `Bearer ${token}`);
  headers.set('X-Terminal-Id', terminalId());
  const resp = await fetch(path, { method: 'POST', headers, body: JSON.stringify(body), signal });
  if (!resp.ok) {
    if (resp.status === 401) {
      const err = await parseError(resp);
      if ((SESSION_END_CODES as readonly string[]).includes(err.code)) {
        setToken(null);
        onSessionEnd?.(err.code, err.message);
      }
      throw err;
    }
    throw await parseError(resp);
  }
  const reader = resp.body!.getReader();
  const decoder = new TextDecoder();
  let buf = '';
  let event = 'message';
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buf += decoder.decode(value, { stream: true });
    let idx: number;
    while ((idx = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, idx).trimEnd();
      buf = buf.slice(idx + 1);
      if (line.startsWith('event:')) event = line.slice(6).trim();
      else if (line.startsWith('data:')) {
        const data = line.slice(5).trim();
        try { yield { kind: event, payload: JSON.parse(data) }; }
        catch { yield { kind: event, payload: data }; }
      }
    }
  }
}
