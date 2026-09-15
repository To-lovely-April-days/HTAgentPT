// E14 用户与权限（FR-7.1/7.2/7.5）。重置口令是决策 2 之后全系统唯一的改密通道——
// 两个后果写在面板上：须当面或电话告知本人；该动作属权限相关操作，记入操作留痕。
// 离职回收：停用 + 导出该用户全量操作记录（与本人自助导出同一份数据）。
import React, { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, download, ApiError } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { CLS_LABEL } from '../../lib/types';
import type { RoleRow, UserRow } from '../../lib/types';
import { ErrorBox, InfoBox, Spinner } from '../../components/Common';

const fi: React.CSSProperties = { height: 28, padding: '0 9px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };

export default function UsersPage() {
  const qc = useQueryClient();
  const { profile } = useAuth();
  const users = useQuery({ queryKey: ['users'], queryFn: () => get<UserRow[]>('/api/users') });
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => get<RoleRow[]>('/api/roles') });
  const [creating, setCreating] = useState(false);
  const [resetFor, setResetFor] = useState<UserRow | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = () => void qc.invalidateQueries({ queryKey: ['users'] });
  const act = async (fn: () => Promise<unknown>) => {
    setError(null);
    try {
      await fn();
      refresh();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '操作失败');
    }
  };

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
      <div style={{ maxWidth: 900 }}>
        <div className="hint" style={{ lineHeight: 1.7, marginBottom: 12 }}>
          账号按最小权限指派角色；角色决定可访问密级与模块。重置口令是全系统唯一的改密通道（不做自助改密）。
        </div>
        {error && <div style={{ marginBottom: 10 }}><ErrorBox message={error} /></div>}
        {!creating && <button className="pbtn" style={{ height: 28, padding: '0 14px', marginBottom: 12 }} onClick={() => setCreating(true)}>开通新账号</button>}
        {creating && profile && (
          <CreateUserPanel roles={roles.data ?? []} companyId={profile.companyId}
            onDone={() => { setCreating(false); refresh(); }} onError={setError} />
        )}
        {resetFor && (
          <ResetPasswordPanel user={resetFor} onDone={() => { setResetFor(null); refresh(); }} onError={setError} />
        )}

        {users.isLoading && <Spinner text="载入用户…" />}
        {(users.data ?? []).map((u) => (
          <div key={u.id} style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '9px 0', borderBottom: '1px solid var(--line-soft)', fontSize: 12.5, opacity: u.isActive ? 1 : .55 }}>
            <div style={{ width: 150 }}>
              <div style={{ fontWeight: 500 }}>{u.displayName}</div>
              <div className="m hint">{u.username}</div>
            </div>
            <select style={{ ...fi, width: 140 }} value={(roles.data ?? []).find((r) => r.code === u.roleCode)?.id ?? ''}
              disabled={!u.isActive}
              onChange={(e) => { if (e.target.value) void act(() => post(`/api/users/${u.id}/role`, { roleId: e.target.value })); }}>
              <option value="">未指派角色</option>
              {(roles.data ?? []).map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
            </select>
            <span className="pill pill-neutral">{u.kind === 'Customer' ? `客户 ${u.customerNo ?? ''}` : (u.department ?? '员工')}</span>
            {!u.isActive && <span className="pill" style={{ background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' }}>已停用</span>}
            <span className="m hint" style={{ flexGrow: 1, textAlign: 'right' }}>
              {u.lastLoginAt ? `上次登录 ${u.lastLoginAt.slice(0, 16).replace('T', ' ')}` : '从未登录'}
            </span>
            {u.isActive && <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => setResetFor(u)}>重置口令</button>}
            <button className="gbtn" style={{ height: 24, fontSize: 11.5 }}
              onClick={() => void download(`/api/users/${u.id}/audit-export`, `${u.username}-operations.csv`).catch((e) => setError(e instanceof ApiError ? e.message : '导出失败'))}>
              导出记录
            </button>
            {u.isActive
              ? <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => void act(() => post(`/api/users/${u.id}/deactivate`))}>停用</button>
              : <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => void act(() => post(`/api/users/${u.id}/reactivate`))}>恢复</button>}
          </div>
        ))}

        <div style={{ marginTop: 22 }}>
          <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 8 }}>角色与密级</div>
          {(roles.data ?? []).map((r) => (
            <div key={r.id} style={{ display: 'flex', alignItems: 'baseline', gap: 10, padding: '6px 0', borderBottom: '1px solid var(--line-soft)', fontSize: 12.5 }}>
              <span style={{ width: 110, fontWeight: 500 }}>{r.name}</span>
              <span className="m hint" style={{ width: 90 }}>{r.code}</span>
              <span style={{ width: 150 }}>{r.classifications.map((c) => CLS_LABEL[c]).join(' / ')}</span>
              <span className="hint" style={{ flexGrow: 1, minWidth: 0 }}>{r.permissions.join('、')}</span>
            </div>
          ))}
          <div className="hint" style={{ marginTop: 8 }}>离职回收：停用账号 + 导出该用户全量操作记录归档——与本人在个人中心自助导出的是同一份数据、同一套字段。</div>
        </div>
      </div>
    </div>
  );
}

function CreateUserPanel({ roles, companyId, onDone, onError }: {
  roles: RoleRow[]; companyId: string; onDone: () => void; onError: (m: string | null) => void;
}) {
  const [f, setF] = useState({ username: '', displayName: '', password: '', roleId: '', kind: 'Employee', department: '', customerNo: '' });
  const [busy, setBusy] = useState(false);

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    onError(null);
    try {
      await post('/api/users', {
        username: f.username.trim(), displayName: f.displayName.trim(), initialPassword: f.password,
        roleId: f.roleId || null, companyId, kind: f.kind,
        department: f.department.trim() || null, customerNo: f.kind === 'Customer' ? f.customerNo.trim() || null : null,
        employeeNo: null,
      });
      onDone();
    } catch (err) {
      onError(err instanceof ApiError ? err.message : '开通失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={save} className="card" style={{ padding: '12px 14px', marginBottom: 14 }}>
      <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 8 }}>开通新账号（初始口令当面或电话告知本人）</div>
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginBottom: 8 }}>
        <input style={{ ...fi, width: 120 }} className="m" placeholder="账号" value={f.username} onChange={(e) => setF({ ...f, username: e.target.value })} autoFocus />
        <input style={{ ...fi, width: 100 }} placeholder="姓名" value={f.displayName} onChange={(e) => setF({ ...f, displayName: e.target.value })} />
        <input style={{ ...fi, width: 150 }} className="m" type="text" placeholder="初始口令" value={f.password} onChange={(e) => setF({ ...f, password: e.target.value })} />
        <select style={fi} value={f.roleId} onChange={(e) => setF({ ...f, roleId: e.target.value })}>
          <option value="">选择角色</option>
          {roles.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
        </select>
        <select style={fi} value={f.kind} onChange={(e) => setF({ ...f, kind: e.target.value })}>
          <option value="Employee">员工</option>
          <option value="Customer">客户</option>
        </select>
        {f.kind === 'Employee'
          ? <input style={{ ...fi, width: 100 }} placeholder="部门" value={f.department} onChange={(e) => setF({ ...f, department: e.target.value })} />
          : <input style={{ ...fi, width: 110 }} className="m" placeholder="客户编号" value={f.customerNo} onChange={(e) => setF({ ...f, customerNo: e.target.value })} />}
      </div>
      <div style={{ display: 'flex', gap: 8 }}>
        <button type="submit" className="pbtn" style={{ height: 28, padding: '0 13px' }}
          disabled={busy || !f.username.trim() || !f.displayName.trim() || f.password.length < 8}>开通</button>
        <button type="button" className="gbtn" onClick={onDone}>取消</button>
        {f.password.length > 0 && f.password.length < 8 && <span className="hint" style={{ alignSelf: 'center' }}>口令至少 8 位</span>}
      </div>
    </form>
  );
}

function ResetPasswordPanel({ user, onDone, onError }: { user: UserRow; onDone: () => void; onError: (m: string | null) => void }) {
  const [pwd, setPwd] = useState('');
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);

  const run = async () => {
    setBusy(true);
    onError(null);
    try {
      await post(`/api/users/${user.id}/reset-password`, { newPassword: pwd });
      setDone(true);
    } catch (err) {
      onError(err instanceof ApiError ? err.message : '重置失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card" style={{ padding: '12px 14px', marginBottom: 14, borderColor: 'var(--cls-int-line)', background: 'var(--cls-int-bg)' }}>
      <div style={{ fontSize: 12.5, fontWeight: 600, color: 'var(--cls-int-fg)', marginBottom: 6 }}>
        重置 {user.displayName}（{user.username}）的口令
      </div>
      {done ? (
        <>
          <InfoBox>已重置并使其现有会话失效。请当面或电话告知本人新口令——不要用消息软件发送。本次重置已记入操作留痕。</InfoBox>
          <button className="gbtn" style={{ marginTop: 10 }} onClick={onDone}>关闭</button>
        </>
      ) : (
        <>
          <div style={{ fontSize: 11.5, lineHeight: 1.7, color: 'var(--cls-int-fg)', marginBottom: 8 }}>
            这是全系统唯一的改密通道（不做自助改密）。重置后：①须当面或电话告知本人；②该动作属权限相关操作，记入操作留痕；③对方当前会话立即失效。
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <input style={{ ...fi, width: 180 }} className="m" placeholder="新口令（至少 8 位）" value={pwd} onChange={(e) => setPwd(e.target.value)} autoFocus />
            <button className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={busy || pwd.length < 8} onClick={() => void run()}>确认重置</button>
            <button className="gbtn" onClick={onDone}>取消</button>
          </div>
        </>
      )}
    </div>
  );
}
