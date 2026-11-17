import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../lib/auth';
import { ApiError } from '../lib/api';
import { ErrorBox, InfoBox } from '../components/Common';

/** C1 登录。异常态按原因码给对应的话（prototype-c1c4）：
 * 凭据错 / 账号停用 / 未指派角色 / 终端未绑定；被顶下线的一端回到这里时看到明确原因（决策 4）。 */
export default function Login() {
  const { login, endedReason } = useAuth();
  const nav = useNavigate();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<{ code: string; message: string } | null>(null);
  const [superseded, setSuperseded] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const resp = await login(username.trim(), password);
      if (resp.supersededOther) setSuperseded(true); // 提示后再进入，让人知道另一处被顶掉了
      else nav('/', { replace: true });
    } catch (err) {
      if (err instanceof ApiError) setError({ code: err.code, message: err.message });
      else setError({ code: 'NETWORK', message: '网络不可达，请检查连接后重试' });
    } finally { setBusy(false); }
  }

  if (superseded) {
    return (
      <Frame>
        <InfoBox>
          登录成功。你的账号在另一处的会话已被终止——同一账号同一时刻只允许一处在线。
        </InfoBox>
        <button className="pbtn" style={{ width: '100%', height: 36, marginTop: 14 }}
          onClick={() => nav('/', { replace: true })}>进入工作台</button>
      </Frame>
    );
  }

  return (
    <Frame>
      {endedReason && (
        <div style={{ marginBottom: 14 }}>
          <InfoBox>{endedReason.message}</InfoBox>
        </div>
      )}
      <form onSubmit={submit}>
        <div className="sect" style={{ fontSize: 13, color: "var(--ink-2)", marginBottom: 6 }}>账号</div>
        <input className="inp" name="username" style={{ height: 42, fontSize: 15, marginBottom: 14, borderRadius: 10 }}
          value={username} onChange={(e) => setUsername(e.target.value)} autoFocus autoComplete="username" />
        <div className="sect" style={{ fontSize: 13, color: "var(--ink-2)", marginBottom: 6 }}>口令</div>
        <input className="inp" name="password" type="password" style={{ height: 42, fontSize: 15, marginBottom: 8, borderRadius: 10 }}
          value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" />
        <div className="hint" style={{ marginBottom: 14 }}>
          忘记口令请联系系统管理员重置——本系统不提供自助改密。
        </div>
        {error && <div style={{ marginBottom: 12 }}><ErrorBox message={error.message} /></div>}
        <button className="pbtn" type="submit" disabled={busy || !username || !password}
          style={{ width: '100%', height: 42, fontSize: 15, borderRadius: 10 }}>
          {busy ? '登录中…' : '登录'}
        </button>
      </form>
    </Frame>
  );
}

function Frame({ children }: { children: React.ReactNode }) {
  return (
    <div style={{ minHeight: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'var(--side)', padding: '24px 16px', boxSizing: 'border-box' }}>
      <div style={{ width: '100%', maxWidth: 400 }}>
        <div style={{ textAlign: 'center', marginBottom: 26 }}>
          <img className="brand-logo" src="/htlab-logo.png" alt="HTLAB 企业智能协作平台"
            style={{ width: 280, maxWidth: '80%', margin: '0 auto 12px' }} />
          <div style={{ fontSize: 13, color: 'var(--ink-3)' }}>内部系统，账号由管理员开通</div>
        </div>
        <div className="card" style={{ padding: '28px 28px 30px', borderRadius: 18, boxShadow: '0 10px 40px rgba(20,20,20,.07)' }}>{children}</div>
      </div>
    </div>
  );
}
