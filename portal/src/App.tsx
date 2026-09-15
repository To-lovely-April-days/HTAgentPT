// K 组客户门户：K1 自助查询 / K2 结果 / K4 报修 / K5 进度 / K6 登录 / K3 我的设备。
// 登录不是前置门——前四项匿名可用；报修单号是提交到开户之间唯一的凭据。
import { useEffect, useState } from 'react';
import {
  api, ApiError, getToken, setToken, STATUS_LABEL,
} from './api';
import type { DeviceRow, PortalHit, PortalTicketCreated, PortalTicketView, TicketRow } from './api';

type View = 'home' | 'result' | 'report' | 'track' | 'signin' | 'mine';

const COMMON_QUESTIONS = ['加热不升温怎么排查', '真空度达不到设定值', '搅拌停机报警怎么处理', '密封件多久更换一次'];

export default function App() {
  const [view, setView] = useState<View>('home');
  const [query, setQuery] = useState('');
  const [signedIn, setSignedIn] = useState(!!getToken());

  const go = (v: View) => { window.scrollTo(0, 0); setView(v); };

  return (
    <>
      <div className="topbar">
        <div className="brand-mark" onClick={() => go('home')}>HT</div>
        <div style={{ flexGrow: 1, minWidth: 0 }}>
          <div style={{ fontSize: 16, fontWeight: 700, lineHeight: 1.3 }}>霍桐实验仪器 · 售后服务</div>
          <div className="hint">自助查询 · 报修 · 进度</div>
        </div>
        {view !== 'home' && (
          <button className="btn-ghost" style={{ width: 'auto', padding: '0 14px' }} onClick={() => go('home')}>首页</button>
        )}
      </div>
      <div className="wrap">
        {view === 'home' && <Home query={query} setQuery={setQuery} onSearch={() => go('result')} onGo={go} signedIn={signedIn} />}
        {view === 'result' && <Result query={query} setQuery={setQuery} onGo={go} />}
        {view === 'report' && <Report onGo={go} />}
        {view === 'track' && <Track />}
        {view === 'signin' && <SignIn onDone={() => { setSignedIn(true); go('mine'); }} />}
        {view === 'mine' && (signedIn
          ? <Mine onSignOut={() => { setToken(null); setSignedIn(false); go('home'); }} />
          : <SignIn onDone={() => { setSignedIn(true); go('mine'); }} />)}
      </div>
    </>
  );
}

// ── K1 自助查询 ───────────────────────────────────────────
function Home({ query, setQuery, onSearch, onGo, signedIn }: {
  query: string; setQuery: (q: string) => void; onSearch: () => void; onGo: (v: View) => void; signedIn: boolean;
}) {
  return (
    <>
      <form style={{ margin: '20px 0 10px' }} onSubmit={(e) => { e.preventDefault(); if (query.trim()) onSearch(); }}>
        <input className="inp" value={query} onChange={(e) => setQuery(e.target.value)}
          placeholder="描述设备情况，如：加热不升温" />
        <button className="btn" style={{ marginTop: 10 }} disabled={!query.trim()}>查询</button>
      </form>
      <div className="hint" style={{ marginBottom: 18 }}>查询公开的使用与保养资料。查不到的问题可直接提交报修，工程师会联系你。</div>

      <div className="label">大家常查</div>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, marginBottom: 26 }}>
        {COMMON_QUESTIONS.map((q) => (
          <button key={q} className="chip" onClick={() => { setQuery(q); onSearch(); }}>{q}</button>
        ))}
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        <button className="btn" onClick={() => onGo('report')}>提交报修</button>
        <button className="btn-ghost" onClick={() => onGo('track')}>凭单号查询报修进度</button>
        <button className="btn-ghost" onClick={() => onGo('mine')}>{signedIn ? '我的设备与报修' : '登录查看我的设备'}</button>
      </div>
      <div className="hint" style={{ marginTop: 14 }}>
        账号由售后工程师在处理报修时为你开通——没有账号也能报修和查进度。
      </div>
    </>
  );
}

// ── K2 结果 ──────────────────────────────────────────────
function Result({ query, setQuery, onGo }: { query: string; setQuery: (q: string) => void; onGo: (v: View) => void }) {
  const [hits, setHits] = useState<PortalHit[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState(query);

  useEffect(() => {
    let gone = false;
    setHits(null);
    setError(null);
    api<PortalHit[]>('/api/portal/search', { body: { query } })
      .then((r) => { if (!gone) setHits(r); })
      .catch((e) => { if (!gone) setError(e instanceof ApiError ? e.message : '查询失败，请稍后重试'); });
    return () => { gone = true; };
  }, [query]);

  return (
    <>
      <form style={{ margin: '16px 0 14px' }} onSubmit={(e) => { e.preventDefault(); if (draft.trim()) setQuery(draft.trim()); }}>
        <input className="inp" value={draft} onChange={(e) => setDraft(e.target.value)} />
      </form>
      {error && <div className="card" style={{ borderColor: '#f5cdc9', color: '#b3261e', marginBottom: 12 }}>{error}</div>}
      {!hits && !error && <div className="hint">正在查询…</div>}
      {hits && hits.length === 0 && (
        <div className="card">
          <div style={{ fontWeight: 600, marginBottom: 6 }}>没有找到与「{query}」相关的公开资料</div>
          <div className="hint" style={{ marginBottom: 14 }}>可以换个说法再试；也可以直接提交报修，工程师会联系你。</div>
          <button className="btn" onClick={() => onGo('report')}>提交报修</button>
        </div>
      )}
      {hits && hits.map((h, i) => (
        <div key={i} className="card" style={{ marginBottom: 10 }}>
          <div style={{ whiteSpace: 'pre-wrap' }}>{h.excerpt}{h.excerpt.length >= 240 ? '…' : ''}</div>
          <div className="hint" style={{ marginTop: 10, paddingTop: 10, borderTop: '1px solid var(--line)' }}>
            资料来源：《{h.docTitle}》{h.section ? ` · ${h.section}` : ''}{h.pageNo != null ? ` · 第 ${h.pageNo} 页` : ''}
          </div>
        </div>
      ))}
      {hits && hits.length > 0 && (
        <div className="hint" style={{ margin: '14px 0' }}>
          按资料排查后仍未解决？<a className="plain" href="#report" onClick={(e) => { e.preventDefault(); onGo('report'); }}>提交报修</a>，工程师会联系你。
        </div>
      )}
    </>
  );
}

// ── K4 提交报修（含成功态）────────────────────────────────
function Report({ onGo }: { onGo: (v: View) => void }) {
  const [deviceNo, setDeviceNo] = useState('');
  const [model, setModel] = useState('');
  const [company, setCompany] = useState('');
  const [contact, setContact] = useState('');
  const [desc, setDesc] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<PortalTicketCreated | null>(null);
  const [copied, setCopied] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      setDone(await api<PortalTicketCreated>('/api/portal/tickets', {
        body: {
          deviceNo: deviceNo.trim() || null, model: model.trim() || null,
          customerName: company.trim() || null, contact: contact.trim(), description: desc.trim(),
        },
      }));
      window.scrollTo(0, 0);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '提交失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  };

  if (done) {
    return (
      <div className="card" style={{ marginTop: 20, textAlign: 'center', padding: '28px 18px' }}>
        <div style={{ fontSize: 17, fontWeight: 700, marginBottom: 4 }}>报修已提交</div>
        <div className="hint" style={{ marginBottom: 18 }}>工程师会尽快电话联系你</div>
        <div className="label" style={{ textAlign: 'center' }}>报修单号——查询进度的唯一凭据，请务必保存</div>
        <div className="mono" style={{ fontSize: 26, fontWeight: 700, letterSpacing: '.04em', margin: '6px 0 14px' }}>{done.ticketNo}</div>
        <button className="btn" onClick={() => {
          navigator.clipboard?.writeText(done.ticketNo).then(() => setCopied(true)).catch(() => setCopied(false));
        }}>{copied ? '已复制' : '复制单号'}</button>
        <button className="btn-ghost" style={{ marginTop: 10 }} onClick={() => onGo('track')}>查询进度</button>
        <div className="hint" style={{ marginTop: 14, textAlign: 'left' }}>
          查询进度时需要单号和你刚才留下的联系电话。工程师处理报修时会为你开通账号，之后登录即可直接查看名下设备与全部报修。
        </div>
      </div>
    );
  }

  return (
    <form onSubmit={submit} style={{ marginTop: 18 }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <div>
          <div className="label">设备出现了什么情况 <span style={{ color: '#b3261e' }}>*</span></div>
          <textarea className="inp" value={desc} onChange={(e) => setDesc(e.target.value)}
            placeholder="如：设定 80 ℃，加热圈通电但釜温不升，控制面板报 E-22" />
        </div>
        <div>
          <div className="label">联系电话 <span style={{ color: '#b3261e' }}>*</span>（查询进度时也用它核对身份）</div>
          <input className="inp" inputMode="tel" value={contact} onChange={(e) => setContact(e.target.value)} placeholder="手机号或座机" />
        </div>
        <div>
          <div className="label">设备编号（选填，见设备铭牌）</div>
          <input className="inp" value={deviceNo} onChange={(e) => setDeviceNo(e.target.value)} placeholder="如 DEV-1007-01" />
        </div>
        <div>
          <div className="label">设备型号（选填）</div>
          <input className="inp" value={model} onChange={(e) => setModel(e.target.value)} placeholder="如 CJF-5L" />
        </div>
        <div>
          <div className="label">单位名称（选填）</div>
          <input className="inp" value={company} onChange={(e) => setCompany(e.target.value)} />
        </div>
        {error && <div className="card" style={{ borderColor: '#f5cdc9', color: '#b3261e' }}>{error}</div>}
        <button className="btn" disabled={busy || !desc.trim() || contact.trim().length < 6}>
          {busy ? '提交中…' : '提交报修'}
        </button>
      </div>
    </form>
  );
}

// ── K5 报修进度 ──────────────────────────────────────────
function Track() {
  const [ticketNo, setTicketNo] = useState('');
  const [contact, setContact] = useState('');
  const [result, setResult] = useState<PortalTicketView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const run = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      setResult(await api<PortalTicketView>(`/api/portal/tickets/${encodeURIComponent(ticketNo.trim())}?contact=${encodeURIComponent(contact.trim())}`));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '查询失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <form onSubmit={run} style={{ marginTop: 18, display: 'flex', flexDirection: 'column', gap: 12 }}>
        <div>
          <div className="label">报修单号</div>
          <input className="inp mono" value={ticketNo} onChange={(e) => setTicketNo(e.target.value)} placeholder="如 RT-2026-0001" />
        </div>
        <div>
          <div className="label">提交报修时留下的联系电话</div>
          <input className="inp" inputMode="tel" value={contact} onChange={(e) => setContact(e.target.value)} />
        </div>
        <button className="btn" disabled={busy || !ticketNo.trim() || !contact.trim()}>{busy ? '查询中…' : '查询进度'}</button>
      </form>
      {error && <div className="card" style={{ marginTop: 14, borderColor: '#f0dcb4', background: 'var(--warn-bg)', color: 'var(--warn)' }}>{error}</div>}
      {result && <TicketTimeline ticketNo={result.ticketNo} status={result.status} nodes={result.nodes} />}
    </>
  );
}

function TicketTimeline({ ticketNo, status, nodes }: { ticketNo: string; status: string; nodes: { at: string; status: string; message: string | null }[] }) {
  return (
    <div className="card" style={{ marginTop: 14 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
        <span className="mono" style={{ fontWeight: 700 }}>{ticketNo}</span>
        <span style={{ marginLeft: 'auto', fontSize: 13, fontWeight: 600, color: status === 'Resolved' || status === 'Closed' ? 'var(--ok)' : 'var(--accent)' }}>
          {STATUS_LABEL[status] ?? status}
        </span>
      </div>
      {nodes.map((n, i) => (
        <div key={i} style={{ display: 'flex', gap: 12 }}>
          <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
            <div style={{ width: 10, height: 10, borderRadius: '50%', marginTop: 6, background: i === nodes.length - 1 ? 'var(--accent)' : '#c2ded1' }} />
            {i < nodes.length - 1 && <div style={{ width: 2, flexGrow: 1, background: 'var(--line)' }} />}
          </div>
          <div style={{ paddingBottom: i < nodes.length - 1 ? 14 : 0 }}>
            <div style={{ fontSize: 14, fontWeight: 600 }}>{STATUS_LABEL[n.status] ?? n.status}</div>
            <div className="hint mono">{n.at.slice(0, 16).replace('T', ' ')}</div>
            {n.message && <div style={{ fontSize: 14, marginTop: 4, padding: '8px 11px', background: 'var(--accent-bg)', borderRadius: 6 }}>{n.message}</div>}
          </div>
        </div>
      ))}
    </div>
  );
}

// ── K6 客户登录 ──────────────────────────────────────────
function SignIn({ onDone }: { onDone: () => void }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const r = await api<{ token: string }>('/api/auth/login', {
        body: { username: username.trim(), password, terminalName: '客户门户' },
        auth: true,
      });
      setToken(r.token);
      onDone();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '登录失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} style={{ marginTop: 22, display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div style={{ fontSize: 16, fontWeight: 700 }}>登录</div>
      <div className="hint">账号由售后工程师在处理报修时为你开通并告知。忘记口令请联系售后重置。</div>
      <input className="inp" value={username} onChange={(e) => setUsername(e.target.value)} placeholder="账号" autoComplete="username" />
      <input className="inp" type="password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder="口令" autoComplete="current-password" />
      {error && <div className="card" style={{ borderColor: '#f5cdc9', color: '#b3261e' }}>{error}</div>}
      <button className="btn" disabled={busy || !username || !password}>{busy ? '登录中…' : '登录'}</button>
    </form>
  );
}

// ── K3 我的设备 + 我的报修（登录后）───────────────────────
function Mine({ onSignOut }: { onSignOut: () => void }) {
  const [devices, setDevices] = useState<DeviceRow[] | null>(null);
  const [tickets, setTickets] = useState<TicketRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([
      api<DeviceRow[]>('/api/customer/my-devices', { auth: true }),
      api<TicketRow[]>('/api/customer/tickets', { auth: true }),
    ]).then(([d, t]) => { setDevices(d); setTickets(t); })
      .catch((e) => {
        setError(e instanceof ApiError ? e.message : '载入失败，请稍后重试');
        if (e instanceof ApiError && e.status === 401) onSignOut();
      });
  }, [onSignOut]);

  return (
    <>
      <div style={{ display: 'flex', alignItems: 'center', margin: '18px 0 10px' }}>
        <div style={{ fontSize: 16, fontWeight: 700 }}>我的设备</div>
        <button className="btn-ghost" style={{ width: 'auto', marginLeft: 'auto', padding: '0 14px' }} onClick={onSignOut}>退出登录</button>
      </div>
      {error && <div className="card" style={{ borderColor: '#f5cdc9', color: '#b3261e', marginBottom: 12 }}>{error}</div>}
      {!devices && !error && <div className="hint">载入中…</div>}
      {devices && devices.length === 0 && <div className="hint">名下暂无登记设备。</div>}
      {devices?.map((d) => (
        <div key={d.id} className="card" style={{ marginBottom: 10 }}>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 10 }}>
            <span className="mono" style={{ fontWeight: 700 }}>{d.deviceNo}</span>
            <span style={{ fontSize: 14 }}>{d.model}</span>
          </div>
          <div className="hint" style={{ marginTop: 3 }}>{d.deliveredAt ? `交付 ${d.deliveredAt}` : '交付日期未登记'}</div>
        </div>
      ))}

      <div style={{ fontSize: 16, fontWeight: 700, margin: '22px 0 10px' }}>我的报修</div>
      {tickets && tickets.length === 0 && <div className="hint">还没有报修记录。</div>}
      {tickets?.map((t) => (
        <TicketTimeline key={t.id} ticketNo={t.ticketNo} status={t.status}
          nodes={t.trail.filter((e) => e.forCustomer || !e.note).map((e) => ({ at: e.at, status: e.status, message: e.forCustomer ? e.note : null }))} />
      ))}
    </>
  );
}
