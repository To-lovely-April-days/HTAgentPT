// 用户与权限 › 账号：搜索、筛选、批量改角色/调部门/停用；⋯ 菜单里改角色、调部门、重置口令、导出记录、停用/恢复；
// 开通账号在右侧抽屉里：先选员工还是外部客户，部门用树选，选完角色马上告诉你他能做什么、能看哪些密级。
// 重置口令是全系统唯一的改密通道（不做自助改密）——生成后当面或电话告诉本人。
import { useEffect, useMemo, useRef, useState } from 'react';
import type React from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { download, get, post, put, ApiError } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import type { Classification, DeptRow, RoleRow, UserRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';
import { CLS_ORDER, CLS_TAG, permLabel } from './permCatalog';
import { Icon } from '../../components/Icons';

/** 部门树拍平成「销售中心 › 售前部 › 华东组」这样的选项，顺序按树。 */
function deptOptions(depts: DeptRow[]): { id: string; path: string; depth: number }[] {
  const kids = new Map<string | null, DeptRow[]>();
  for (const d of depts) {
    const p = d.parentId && depts.some((x) => x.id === d.parentId) ? d.parentId : null;
    kids.set(p, [...(kids.get(p) ?? []), d]);
  }
  const out: { id: string; path: string; depth: number }[] = [];
  const walk = (parent: string | null, prefix: string[], depth: number) => {
    for (const d of (kids.get(parent) ?? []).sort((a, b) => a.sortOrder - b.sortOrder)) {
      const path = [...prefix, d.name];
      out.push({ id: d.id, path: path.join(' / '), depth });
      walk(d.id, path, depth + 1);
    }
  };
  walk(null, [], 0);
  return out;
}

/** 一个部门及它全部下级部门的 id。 */
function subtree(depts: DeptRow[], id: string): Set<string> {
  const set = new Set<string>([id]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const d of depts) if (d.parentId && set.has(d.parentId) && !set.has(d.id)) { set.add(d.id); grew = true; }
  }
  return set;
}

/** 生成初始口令：12 位，去掉容易看错的字符（0/O、1/l/I）。 */
function genPassword(): string {
  const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789';
  const buf = new Uint32Array(12);
  crypto.getRandomValues(buf);
  return Array.from(buf, (n) => chars[n % chars.length]).join('');
}

function ClsTags({ cls }: { cls: Classification[] }) {
  return (
    <span style={{ display: 'flex', gap: 4 }}>
      {CLS_ORDER.filter((c) => cls.includes(c)).map((c) => (
        <span key={c} className="acc-cls" style={{ background: CLS_TAG[c][1], color: CLS_TAG[c][2] }}>{CLS_TAG[c][0]}</span>
      ))}
    </span>
  );
}

type Dialog =
  | { kind: 'role'; users: UserRow[] }
  | { kind: 'dept'; users: UserRow[] }
  | { kind: 'reset'; user: UserRow };

export default function AccountsTab() {
  const qc = useQueryClient();
  const users = useQuery({ queryKey: ['users'], queryFn: () => get<UserRow[]>('/api/users') });
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => get<RoleRow[]>('/api/roles') });
  const depts = useQuery({ queryKey: ['departments'], queryFn: () => get<DeptRow[]>('/api/departments') });
  const [search, setSearch] = useState('');
  const [fDept, setFDept] = useState('');
  const [fRole, setFRole] = useState('');
  const [fKind, setFKind] = useState('');
  const [fStatus, setFStatus] = useState('active');
  const [sel, setSel] = useState<Set<string>>(new Set());
  const [menu, setMenu] = useState<string | null>(null);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const all = useMemo(() => users.data ?? [], [users.data]);
  const deptList = useMemo(() => depts.data ?? [], [depts.data]);
  const opts = useMemo(() => deptOptions(deptList), [deptList]);
  const deptName = (id: string | null | undefined) => (id ? deptList.find((d) => d.id === id)?.name ?? '—' : null);
  const heads = new Set(deptList.filter((d) => d.headUserId).map((d) => d.headUserId!));
  const roleOf = (u: UserRow) => (roles.data ?? []).find((r) => r.code === u.roleCode);

  const k = search.trim().toLowerCase();
  const inDept = fDept && fDept !== '__none__' ? subtree(deptList, fDept) : null;
  const rows = all.filter((u) =>
    (!k || u.displayName.toLowerCase().includes(k) || u.username.toLowerCase().includes(k))
    && (!fDept || (fDept === '__none__' ? !u.departmentId && u.kind === 'Employee' : !!u.departmentId && inDept!.has(u.departmentId)))
    && (!fRole || u.roleCode === fRole)
    && (!fKind || u.kind === fKind)
    && (!fStatus || (fStatus === 'active' ? u.isActive : !u.isActive)));
  const picked = rows.filter((u) => sel.has(u.id));
  const counts = { all: all.length, emp: all.filter((u) => u.kind === 'Employee').length, cus: all.filter((u) => u.kind === 'Customer').length, off: all.filter((u) => !u.isActive).length };

  const refresh = () => { void qc.invalidateQueries({ queryKey: ['users'] }); void qc.invalidateQueries({ queryKey: ['departments'] }); };
  const act = async (fn: () => Promise<unknown>, ok?: string) => {
    setError(null); setDone(null);
    try { await fn(); refresh(); if (ok) setDone(ok); return true; }
    catch (err) { setError(err instanceof ApiError ? err.message : '操作失败'); return false; }
  };
  const toggle = (id: string) => setSel((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const allOn = rows.length > 0 && rows.every((u) => sel.has(u.id));

  const exportOps = (u: UserRow) => void download(`/api/users/${u.id}/audit-export`, `${u.username}-operations.csv`)
    .catch((e) => setError(e instanceof ApiError ? e.message : '导出失败'));

  const sel_ = (value: string, set: (v: string) => void, label: string, options: [string, string][]) => (
    <select className={'acc-filter' + (value ? ' on' : '')} value={value} onChange={(e) => set(e.target.value)} aria-label={label}>
      {options.map(([v, t]) => <option key={v} value={v}>{t}</option>)}
    </select>
  );

  return (
    <div className="acc">
      <div className="acc-main sc">
        <div className="dash-head">
          <div>
            <h1 className="dash-title" style={{ fontSize: 24 }}>账号</h1>
            <p className="dash-sub">{counts.all} 个账号 · 员工 {counts.emp} · 外部客户 {counts.cus} · 已停用 {counts.off}</p>
          </div>
          <button type="button" className="pbtn dash-btn" onClick={() => setCreating(true)}><Icon name="plus" size={13} className="gi l" />开通账号</button>
        </div>

        <div className="acc-filters">
          <input className="inp" style={{ width: 220, height: 36 }} placeholder="搜姓名、账号" value={search} onChange={(e) => setSearch(e.target.value)} />
          {sel_(fDept, setFDept, '按部门筛选', [['', '部门：全部'], ['__none__', '未分配部门'], ...opts.map((o) => [o.id, `${'　'.repeat(o.depth)}${o.path.split(' / ').slice(-1)[0]}`] as [string, string])])}
          {sel_(fRole, setFRole, '按角色筛选', [['', '角色：全部'], ...(roles.data ?? []).map((r) => [r.code, r.name] as [string, string])])}
          {sel_(fKind, setFKind, '按类型筛选', [['', '类型：全部'], ['Employee', '公司员工'], ['Customer', '外部客户']])}
          {sel_(fStatus, setFStatus, '按状态筛选', [['active', '状态：正常'], ['off', '状态：已停用'], ['', '状态：全部']])}
        </div>

        {error && <ErrorBox message={error} />}
        {done && <div className="dash-card acc-done">{done}</div>}

        {picked.length > 0 && (
          <div className="rv-batch">
            <span style={{ fontWeight: 600 }}>已选 {picked.length} 人</span>
            <div style={{ flexGrow: 1 }} />
            <button type="button" className="ghost" onClick={() => setDialog({ kind: 'role', users: picked })}>改角色…</button>
            <button type="button" className="ghost" onClick={() => setDialog({ kind: 'dept', users: picked.filter((u) => u.kind === 'Employee') })}>调部门…</button>
            <button type="button" className="solid" style={{ color: 'var(--brand-fg)' }}
              onClick={() => { if (window.confirm(`停用 ${picked.length} 个账号？停用后立即失去全部访问权。`)) void act(() => post('/api/users/deactivate-batch', { userIds: picked.map((u) => u.id) }), `已停用 ${picked.length} 个账号`).then(() => setSel(new Set())); }}>停用</button>
          </div>
        )}

        <section className="dash-card" style={{ padding: 0, overflow: 'visible' }}>
          {users.isLoading && <div style={{ padding: 24 }}><Spinner text="载入…" /></div>}
          {!users.isLoading && (
            <table className="us-tbl fm-tbl acc-tbl">
              <thead>
                <tr>
                  <th style={{ width: 28 }}><input type="checkbox" aria-label="全选" checked={allOn} onChange={() => setSel(allOn ? new Set() : new Set(rows.map((u) => u.id)))} /></th>
                  <th>姓名 / 账号</th><th>部门</th><th>角色</th><th>能看的密级</th><th>最近登录</th><th>状态</th><th style={{ width: 40 }} />
                </tr>
              </thead>
              <tbody>
                {rows.map((u) => {
                  const r = roleOf(u);
                  const isHead = heads.has(u.id);
                  return (
                    <tr key={u.id} style={{ opacity: u.isActive ? 1 : 0.55 }}>
                      <td><input type="checkbox" aria-label={`选择 ${u.displayName}`} checked={sel.has(u.id)} onChange={() => toggle(u.id)} /></td>
                      <td>
                        <span className="us-who">
                          <i aria-hidden="true" style={{ background: isHead ? 'var(--brand)' : u.kind === 'Customer' ? '#8a5a00' : undefined }}>{u.displayName.slice(0, 1)}</i>
                          <span style={{ display: 'flex', flexDirection: 'column' }}><b>{u.displayName}</b><span className="dash-muted" style={{ fontSize: 12 }}>{u.username}</span></span>
                        </span>
                      </td>
                      <td style={{ color: 'var(--ink-2)' }}>
                        {u.kind === 'Customer' ? `客户编号 ${u.customerNo ?? '—'}` : (deptName(u.departmentId) ?? <span style={{ color: '#8a5a00' }}>未分配</span>)}
                        {isHead && <span className="org-tag" style={{ marginLeft: 6 }}>主管</span>}
                      </td>
                      <td>{u.roleName ? <span className="acc-role">{u.roleName}</span> : <span style={{ color: 'var(--brand-fg)' }}>未指派</span>}</td>
                      <td>{r ? <ClsTags cls={r.classifications} /> : '—'}</td>
                      <td style={{ color: 'var(--ink-3)' }}>{u.lastLoginAt ? u.lastLoginAt.slice(5, 16).replace('T', ' ') : '从未登录'}</td>
                      <td style={{ fontSize: 12, color: u.isActive ? '#1c6b45' : 'var(--ink-4)' }}><Icon name="dot" size={12} style={{ verticalAlign: '-2px' }} /> {u.isActive ? '正常' : '已停用'}</td>
                      <td style={{ position: 'relative', textAlign: 'right' }}>
                        <button type="button" className="acc-more" aria-label={`${u.displayName} 的更多操作`} aria-expanded={menu === u.id}
                          onClick={() => setMenu(menu === u.id ? null : u.id)}>⋯</button>
                        {menu === u.id && (
                          <RowMenu onClose={() => setMenu(null)} items={[
                            { label: '改角色', on: () => setDialog({ kind: 'role', users: [u] }), hide: !u.isActive },
                            { label: '调部门', on: () => setDialog({ kind: 'dept', users: [u] }), hide: u.kind !== 'Employee' },
                            { label: '重置口令', on: () => setDialog({ kind: 'reset', user: u }), hide: !u.isActive },
                            { label: '导出操作记录', on: () => exportOps(u) },
                            u.isActive
                              ? { label: '停用', danger: true, on: () => { if (window.confirm(`停用「${u.displayName}」？停用后立即失去全部访问权。离职的话，记得先导出操作记录归档。`)) void act(() => post(`/api/users/${u.id}/deactivate`), `已停用 ${u.displayName}`); } }
                              : { label: '恢复', on: () => void act(() => post(`/api/users/${u.id}/reactivate`), `已恢复 ${u.displayName}`) },
                          ]} />
                        )}
                      </td>
                    </tr>
                  );
                })}
                {rows.length === 0 && <tr><td colSpan={8} className="dash-empty" style={{ padding: '24px 22px' }}>没有符合条件的账号。</td></tr>}
              </tbody>
            </table>
          )}
        </section>
        <p className="dash-muted" style={{ margin: 0 }}>离职回收 = 停用 + 导出该用户的全部操作记录归档（和本人在个人中心导出的是同一份）。</p>
      </div>

      {creating && (
        <CreateDrawer roles={roles.data ?? []} depts={deptList} opts={opts}
          onClose={() => setCreating(false)} onCreated={(msg) => { setCreating(false); setDone(msg); refresh(); }} />
      )}
      {dialog?.kind === 'role' && (
        <PickDialog title={`改角色（${dialog.users.length} 人）`} label="新角色"
          options={(roles.data ?? []).map((r) => [r.id, r.name])}
          onClose={() => setDialog(null)}
          onOk={(roleId) => void act(async () => { for (const u of dialog.users) await post(`/api/users/${u.id}/role`, { roleId }); },
            `已改 ${dialog.users.length} 人的角色`).then((ok) => { if (ok) { setDialog(null); setSel(new Set()); } })} />
      )}
      {dialog?.kind === 'dept' && (
        <PickDialog title={`调部门（${dialog.users.length} 人）`} label="调到"
          options={[['', '未分配部门'], ...opts.map((o) => [o.id, o.path] as [string, string])]}
          note="调离原部门时，如果他是原部门主管，主管位置会空出来。"
          onClose={() => setDialog(null)}
          onOk={(deptId) => void act(async () => { for (const u of dialog.users) await put(`/api/departments/members/${u.id}`, { departmentId: deptId || null }); },
            `已调整 ${dialog.users.length} 人的部门`).then((ok) => { if (ok) { setDialog(null); setSel(new Set()); } })} />
      )}
      {dialog?.kind === 'reset' && <ResetDialog user={dialog.user} onClose={() => setDialog(null)} />}
    </div>
  );
}

function RowMenu({ items, onClose }: { items: { label: string; on: () => void; danger?: boolean; hide?: boolean }[]; onClose: () => void }) {
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const close = (e: MouseEvent) => { if (!box.current?.contains(e.target as Node)) onClose(); };
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', esc);
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', esc); };
  }, [onClose]);
  return (
    <div className="acc-menu" ref={box} role="menu">
      {items.filter((i) => !i.hide).map((i) => (
        <button key={i.label} type="button" role="menuitem" className={i.danger ? 'danger' : ''} onClick={() => { onClose(); i.on(); }}>{i.label}</button>
      ))}
    </div>
  );
}

function Modal({ title, children, onClose, foot }: { title: string; children: React.ReactNode; onClose: () => void; foot: React.ReactNode }) {
  useEffect(() => {
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', esc);
    return () => window.removeEventListener('keydown', esc);
  }, [onClose]);
  return (
    <div className="modal-mask" onClick={onClose}>
      <div className="acc-modal" role="dialog" aria-label={title} onClick={(e) => e.stopPropagation()}>
        <b className="acc-modal-t">{title}</b>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>{children}</div>
        <div className="acc-modal-f">{foot}</div>
      </div>
    </div>
  );
}

function PickDialog({ title, label, options, note, onClose, onOk }: {
  title: string; label: string; options: [string, string][]; note?: string; onClose: () => void; onOk: (v: string) => void;
}) {
  const [v, setV] = useState(options[0]?.[0] ?? '');
  return (
    <Modal title={title} onClose={onClose} foot={<>
      <button type="button" className="gbtn" onClick={onClose}>取消</button>
      <button type="button" className="pbtn" onClick={() => onOk(v)}>确定</button>
    </>}>
      <label className="acc-field"><span>{label}</span>
        <select className="inp" style={{ height: 38 }} value={v} onChange={(e) => setV(e.target.value)} autoFocus>
          {options.map(([id, t]) => <option key={id} value={id}>{t}</option>)}
        </select>
      </label>
      {note && <span className="dash-muted" style={{ fontSize: 12 }}>{note}</span>}
    </Modal>
  );
}

function ResetDialog({ user, onClose }: { user: UserRow; onClose: () => void }) {
  const [pwd, setPwd] = useState(genPassword);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [ok, setOk] = useState(false);
  const save = async () => {
    setBusy(true); setError(null);
    try { await post(`/api/users/${user.id}/reset-password`, { newPassword: pwd }); setOk(true); }
    catch (err) { setError(err instanceof ApiError ? err.message : '重置失败'); }
    finally { setBusy(false); }
  };
  return (
    <Modal title={`重置口令 · ${user.displayName}`} onClose={onClose} foot={ok
      ? <button type="button" className="pbtn" onClick={onClose}>好了</button>
      : <>
        <button type="button" className="gbtn" onClick={onClose}>取消</button>
        <button type="button" className="pbtn" disabled={busy || pwd.length < 8} onClick={() => void save()}>{busy ? '重置中…' : '重置'}</button>
      </>}>
      <label className="acc-field"><span>新口令（至少 8 位）</span>
        <span style={{ display: 'flex', gap: 6 }}>
          <input className="inp m" style={{ height: 38, flex: 1 }} value={pwd} onChange={(e) => setPwd(e.target.value)} disabled={ok} />
          {!ok && <button type="button" className="gbtn" onClick={() => setPwd(genPassword())}>重新生成</button>}
          <button type="button" className="gbtn" onClick={() => void navigator.clipboard?.writeText(pwd)}>复制</button>
        </span>
      </label>
      {error && <ErrorBox message={error} />}
      <span className="dash-muted" style={{ fontSize: 12, lineHeight: 1.7 }}>
        {ok ? '已重置，他原来的登录已失效。请当面或电话把新口令告诉本人。' : '系统里没有自助改密，这是唯一的改口令方式。重置后请当面或电话告诉本人；这个动作会记入审计。'}
      </span>
    </Modal>
  );
}

function CreateDrawer({ roles, depts, opts, onClose, onCreated }: {
  roles: RoleRow[]; depts: DeptRow[]; opts: { id: string; path: string }[]; onClose: () => void; onCreated: (msg: string) => void;
}) {
  const { profile } = useAuth();
  const [kind, setKind] = useState<'Employee' | 'Customer'>('Employee');
  const [name, setName] = useState('');
  const [username, setUsername] = useState('');
  const [pwd, setPwd] = useState(genPassword);
  const [deptId, setDeptId] = useState('');
  const [roleId, setRoleId] = useState('');
  const [customerNo, setCustomerNo] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const role = roles.find((r) => r.id === roleId);
  // 客户账号默认配客户角色
  const customerRole = roles.find((r) => r.code === 'customer');
  const effRoleId = roleId || (kind === 'Customer' ? customerRole?.id ?? '' : '');
  const effRole = role ?? (kind === 'Customer' ? customerRole : undefined);
  const head = (() => {
    // 这个部门（往上找）由谁审批
    let cur = depts.find((d) => d.id === deptId);
    const seen = new Set<string>();
    while (cur && !seen.has(cur.id)) { seen.add(cur.id); if (cur.headName) return cur.headName; cur = depts.find((d) => d.id === cur!.parentId); }
    return null;
  })();
  const ready = name.trim() && /^[A-Za-z0-9_.-]{2,32}$/.test(username.trim()) && pwd.length >= 8 && effRoleId && (kind === 'Employee' || customerNo.trim());

  const save = async () => {
    if (!profile || !ready) return;
    setBusy(true); setError(null);
    try {
      const r = await post<{ id: string }>('/api/users', {
        username: username.trim(), displayName: name.trim(), initialPassword: pwd, roleId: effRoleId, companyId: profile.companyId,
        kind, employeeNo: null, department: null, customerNo: kind === 'Customer' ? customerNo.trim() : null,
      });
      if (kind === 'Employee' && deptId) await put(`/api/departments/members/${r.id}`, { departmentId: deptId });
      onCreated(`已开通 ${name.trim()}（${username.trim()}）。初始口令请当面或电话告诉本人。`);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '开通失败');
    } finally { setBusy(false); }
  };

  return (
    <aside className="acc-drawer" aria-label="开通账号">
      <div className="acc-drawer-hd"><b>开通账号</b><button type="button" className="refs-x" aria-label="关闭" onClick={onClose}><Icon name="x" size={14} style={{ display: 'block' }} /></button></div>
      <div className="acc-drawer-body sc">
        <div className="us-seg" role="group" aria-label="账号类型" style={{ alignSelf: 'flex-start' }}>
          <button type="button" className={kind === 'Employee' ? 'on' : ''} aria-pressed={kind === 'Employee'} onClick={() => { setKind('Employee'); setRoleId(''); }}>公司员工</button>
          <button type="button" className={kind === 'Customer' ? 'on' : ''} aria-pressed={kind === 'Customer'} onClick={() => { setKind('Customer'); setRoleId(''); }}>外部客户</button>
        </div>
        <label className="acc-field"><span>{kind === 'Customer' ? '客户名称' : '姓名'} <em>*</em></span>
          <input className="inp" style={{ height: 38 }} value={name} onChange={(e) => setName(e.target.value)} autoFocus /></label>
        <label className="acc-field"><span>登录账号 <em>*</em></span>
          <input className="inp m" style={{ height: 38 }} value={username} onChange={(e) => setUsername(e.target.value)} placeholder="字母、数字" />
          <small>字母数字，开通后不能改</small></label>
        <label className="acc-field"><span>初始口令 <em>*</em></span>
          <span style={{ display: 'flex', gap: 6 }}>
            <input className="inp m" style={{ height: 38, flex: 1 }} value={pwd} onChange={(e) => setPwd(e.target.value)} />
            <button type="button" className="gbtn" onClick={() => setPwd(genPassword())}>生成</button>
            <button type="button" className="gbtn" onClick={() => void navigator.clipboard?.writeText(pwd)}>复制</button>
          </span>
          <small>当面或电话告诉本人；系统里没有自助改密，以后只能由管理员重置</small></label>
        {kind === 'Employee' ? (
          <label className="acc-field"><span>所在部门</span>
            <select className="inp" style={{ height: 38 }} value={deptId} onChange={(e) => setDeptId(e.target.value)}>
              <option value="">先不分配</option>
              {opts.map((o) => <option key={o.id} value={o.id}>{o.path}</option>)}
            </select>
            <small>{deptId ? (head ? `他提交的文件和客户申请由 ${head} 审批` : '这个部门一路往上都没设主管，由系统管理员审批') : '不分配部门的话，他提交的东西由系统管理员审批'}</small></label>
        ) : (
          <label className="acc-field"><span>客户编号 <em>*</em></span>
            <input className="inp m" style={{ height: 38 }} value={customerNo} onChange={(e) => setCustomerNo(e.target.value)} placeholder="如 C-0012" />
            <small>客户只看得到自己名下的设备和报修</small></label>
        )}
        <label className="acc-field"><span>角色 <em>*</em></span>
          <select className="inp" style={{ height: 38 }} value={effRoleId} onChange={(e) => setRoleId(e.target.value)}>
            <option value="">请选择</option>
            {roles.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
          </select>
          <small>决定能用哪些功能、能看哪些密级</small></label>
        {effRole && (
          <div className="acc-preview">
            <b>{effRole.name} 能做：</b>{effRole.permissions.length ? effRole.permissions.map(permLabel).join(' · ') : '（还没开任何功能）'}<br />
            <b>能看的密级：</b><ClsTags cls={effRole.classifications} />
          </div>
        )}
        {error && <ErrorBox message={error} />}
      </div>
      <div className="acc-drawer-f">
        <button type="button" className="gbtn" onClick={onClose}>取消</button>
        <button type="button" className="pbtn" disabled={!ready || busy} onClick={() => void save()}>{busy ? '开通中…' : '开通'}</button>
      </div>
    </aside>
  );
}
