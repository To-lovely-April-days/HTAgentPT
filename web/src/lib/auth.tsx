import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { get, post, setToken, getToken, setSessionEndHandler, terminalId } from './api';
import type { LoginResponse, UserProfile } from './types';

interface AuthState {
  profile: UserProfile | null;
  booting: boolean;
  /** 会话终止原因（决策 4：被顶下线要看到明确原因，不是笼统的过期）。 */
  endedReason: { code: string; message: string } | null;
  login: (username: string, password: string) => Promise<LoginResponse>;
  logout: () => Promise<void>;
  has: (perm: string) => boolean;
}

const Ctx = createContext<AuthState>(null!);
export const useAuth = () => useContext(Ctx);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [booting, setBooting] = useState(!!getToken());
  const [endedReason, setEndedReason] = useState<AuthState['endedReason']>(null);

  useEffect(() => {
    setSessionEndHandler((code, message) => {
      setProfile(null);
      setEndedReason({ code, message });
    });
    return () => setSessionEndHandler(null);
  }, []);

  useEffect(() => {
    if (!getToken()) return;
    get<UserProfile & { userId: string }>('/api/auth/me')
      .then((me) => setProfile({ ...me, id: me.userId ?? me.id, displayName: me.displayName ?? me.username,
        roleName: me.roleName ?? me.roleCode, companyName: me.companyName ?? '' }))
      .catch(() => setToken(null))
      .finally(() => setBooting(false));
  }, []);

  const login = useCallback(async (username: string, password: string) => {
    const resp = await post<LoginResponse>('/api/auth/login', {
      username, password, terminalId: terminalId(), terminalName: navigator.userAgent.slice(0, 60),
    });
    setToken(resp.token);
    setProfile(resp.profile);
    setEndedReason(null);
    return resp;
  }, []);

  const logout = useCallback(async () => {
    try { await post('/api/auth/logout'); } catch { /* 已失效也照常清理 */ }
    setToken(null);
    setProfile(null);
  }, []);

  const has = useCallback((perm: string) => profile?.permissions.includes(perm) ?? false, [profile]);

  const value = useMemo(() => ({ profile, booting, endedReason, login, logout, has }),
    [profile, booting, endedReason, login, logout, has]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
