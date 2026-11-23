// 用户与权限 › 组织架构：公司 → 部门 → 下级部门 → 人员。每个部门可指定一位主管。
// 功能权限仍按角色给（另一个页签）；部门只决定「谁审、谁管」：员工上传的文件和新增客户申请交给所在部门的主管，
// 本部门没设主管就往上找，到顶都没有就交系统管理员。
import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put, ApiError } from '../../lib/api';
import type { DeptRow, UserRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';
import { Icon } from '../../components/Icons';

const UNASSIGNED = '__none__';

interface TreeNode { d: DeptRow; depth: number; hasChildren: boolean }

function flatten(depts: DeptRow[], collapsed: Set<string>): TreeNode[] {
  const kids = new Map<string | null, DeptRow[]>();
  for (const d of depts) {
    const k = d.parentId && depts.some((x) => x.id === d.parentId) ? d.parentId : null;
    kids.set(k, [...(kids.get(k) ?? []), d]);
  }
  const out: TreeNode[] = [];
  const walk = (parent: string | null, depth: number) => {
    for (const d of (kids.get(parent) ?? []).sort((a, b) => a.sortOrder - b.sortOrder)) {
      const hasChildren = (kids.get(d.id) ?? []).length > 0;
      out.push({ d, depth, hasChildren });
      if (!collapsed.has(d.id)) walk(d.id, depth + 1);
    }
  };
  walk(null, 0);
  return out;
}

/** 从 id 往上一路的部门名（面包屑）。 */
function pathOf(depts: DeptRow[], id: string): string[] {
  const byId = new Map(depts.map((d) => [d.id, d]));
  const names: string[] = [];
  let cur = byId.get(id);
  const seen = new Set<string>();
  while (cur && !seen.has(cur.id)) { seen.add(cur.id); names.unshift(cur.name); cur = cur.parentId ? byId.get(cur.parentId) : undefined; }
  return names;
}

export default function OrgTreeTab() {
  const qc = useQueryClient();
  const depts = useQuery({ queryKey: ['departments'], queryFn: () => get<DeptRow[]>('/api/departments') });
  const users = useQuery({ queryKey: ['users'], queryFn: () => get<UserRow[]>('/api/users') });
  const [sel, setSel] = useState<string | null>(null);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [search, setSearch] = useState('');
  const [naming, setNaming] = useState<{ mode: 'new' | 'child' | 'rename'; value: string } | null>(null);
  const [error, setError] = useState<string | null>(null);

  const list = useMemo(() => depts.data ?? [], [depts.data]);
  const employees = (users.data ?? []).filter((u) => u.kind === 'Employee');
  const current = sel ?? list[0]?.id ?? null;
  const cur = current && current !== UNASSIGNED ? list.find((d) => d.id === current) ?? null : null;
  const unassigned = employees.filter((u) => !u.departmentId && u.isActive);
  const members = current === UNASSIGNED ? unassigned : employees.filter((u) => u.departmentId === current);
  const k = search.trim();
  const tree = flatten(list, k ? new Set() : collapsed).filter((n) =>
    !k || n.d.name.includes(k) || employees.some((u) => u.departmentId === n.d.id && (u.displayName.includes(k) || u.username.includes(k))));

  const refresh = () => { void qc.invalidateQueries({ queryKey: ['departments'] }); void qc.invalidateQueries({ queryKey: ['users'] }); };
  const act = async (fn: () => Promise<unknown>) => {
    setError(null);
    try { await fn(); refresh(); } catch (err) { setError(err instanceof ApiError ? err.message : '操作失败'); }
  };
  const saveName = async () => {
    if (!naming || !naming.value.trim()) return;
    const name = naming.value.trim();
    if (naming.mode === 'rename' && cur) await act(() => put(`/api/departments/${cur.id}`, { name, parentId: null, moveParent: false }));
    else {
      setError(null);
      try {
        const created = await post<DeptRow>('/api/departments', { name, parentId: naming.mode === 'child' ? cur?.id ?? null : null });
        setSel(created.id); refresh();
      } catch (err) { setError(err instanceof ApiError ? err.message : '新建失败'); return; }
    }
    setNaming(null);
  };

  return (
    <div className="org">
      <section className="org-tree">
        <div className="org-tree-hd">
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
            <b style={{ fontSize: 15, fontWeight: 600 }}>部门</b>
            <button type="button" className="gbtn" onClick={() => setNaming({ mode: 'new', value: '' })}><Icon name="plus" size={13} className="gi l" />新建部门</button>
          </div>
          <input className="inp" style={{ height: 34 }} placeholder="搜人或部门" value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
        <div className="org-tree-list sc">
          {depts.isLoading && <Spinner text="载入…" />}
          {!depts.isLoading && list.length === 0 && <div className="dash-empty" style={{ padding: '6px 10px' }}>还没有部门，先新建一个。</div>}
          {tree.map(({ d, depth, hasChildren }) => (
            <div key={d.id} role="treeitem" aria-selected={current === d.id} aria-expanded={hasChildren ? !collapsed.has(d.id) : undefined}
              className={'org-node' + (current === d.id ? ' on' : '')} style={{ paddingLeft: 8 + depth * 18 }}
              onClick={() => { setSel(d.id); setNaming(null); }}>
              <button type="button" className="org-caret" aria-label={collapsed.has(d.id) ? '展开' : '收起'} disabled={!hasChildren}
                onClick={(e) => { e.stopPropagation(); setCollapsed((c) => { const n = new Set(c); if (n.has(d.id)) n.delete(d.id); else n.add(d.id); return n; }); }}>
                {hasChildren && <Icon name={collapsed.has(d.id) ? 'right' : 'down'} size={12} style={{ display: 'block' }} />}
              </button>
              <span className="org-node-n">{d.name}</span>
              <span className="org-node-h">{d.headName ?? '—'}</span>
              <span className="org-node-c">{d.memberCount}</span>
            </div>
          ))}
          <div role="treeitem" aria-selected={current === UNASSIGNED} className={'org-node org-none' + (current === UNASSIGNED ? ' on' : '')}
            onClick={() => setSel(UNASSIGNED)}>
            <i aria-hidden="true" />未分配部门 {unassigned.length} 人
          </div>
        </div>
      </section>

      <section className="org-main sc">
        {error && <ErrorBox message={error} />}
        {naming && naming.mode !== 'rename' && (
          <form className="dash-card org-form" onSubmit={(e) => { e.preventDefault(); void saveName(); }}>
            <b>{naming.mode === 'child' ? `在「${cur?.name}」下新建下级部门` : '新建部门（最上层）'}</b>
            <input className="inp" autoFocus style={{ height: 34, maxWidth: 320 }} placeholder="部门名称，如 售前部" value={naming.value}
              onChange={(e) => setNaming({ ...naming, value: e.target.value })} />
            <button type="submit" className="pbtn" disabled={!naming.value.trim()}>建好</button>
            <button type="button" className="gbtn" onClick={() => setNaming(null)}>取消</button>
          </form>
        )}

        {current === UNASSIGNED ? (
          <>
            <h1 className="dash-title" style={{ fontSize: 24 }}>未分配部门</h1>
            <p className="dash-sub" style={{ marginTop: -8 }}>这些人提交的文件和客户申请只能由系统管理员审批。给他们选好部门，就交给部门主管了。</p>
            <Members rows={members} depts={list} deptId={null} onMove={(u, d) => void act(() => put(`/api/departments/members/${u}`, { departmentId: d }))} />
          </>
        ) : cur ? (
          <>
            <div className="dash-head">
              <div>
                <div className="dash-muted">{pathOf(list, cur.id).slice(0, -1).join(' / ') || '最上层'}</div>
                {naming?.mode === 'rename' ? (
                  <form style={{ display: 'flex', gap: 8, marginTop: 4 }} onSubmit={(e) => { e.preventDefault(); void saveName(); }}>
                    <input className="inp" autoFocus style={{ height: 36, width: 260 }} value={naming.value} onChange={(e) => setNaming({ ...naming, value: e.target.value })} />
                    <button type="submit" className="pbtn">保存</button>
                    <button type="button" className="gbtn" onClick={() => setNaming(null)}>取消</button>
                  </form>
                ) : <h1 className="dash-title" style={{ fontSize: 24, marginTop: 4 }}>{cur.name}</h1>}
              </div>
              <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
                <button type="button" className="gbtn" onClick={() => setNaming({ mode: 'rename', value: cur.name })}>改名</button>
                <label className="org-move">
                  <span className="dash-muted">上级</span>
                  <select className="inp" style={{ height: 32, width: 160 }} value={cur.parentId ?? ''}
                    onChange={(e) => void act(() => put(`/api/departments/${cur.id}`, { name: null, parentId: e.target.value || null, moveParent: true }))}>
                    <option value="">（最上层）</option>
                    {list.filter((d) => d.id !== cur.id).map((d) => <option key={d.id} value={d.id}>{pathOf(list, d.id).join(' / ')}</option>)}
                  </select>
                </label>
                <button type="button" className="gbtn" onClick={() => setNaming({ mode: 'child', value: '' })}><Icon name="plus" size={13} className="gi l" />下级部门</button>
                <button type="button" className="gbtn" style={{ color: 'var(--cls-conf-fg)' }}
                  onClick={() => { if (window.confirm(`删除部门「${cur.name}」？部门里要没有人、没有下级部门才能删。`)) void act(() => del(`/api/departments/${cur.id}`)).then(() => setSel(null)); }}>删除</button>
              </div>
            </div>

            <div className="org-head dash-card">
              <span className={'org-av' + (cur.headUserId ? ' head' : '')} aria-hidden="true">{cur.headName?.slice(0, 1) ?? '?'}</span>
              <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 3 }}>
                <span className="dash-muted" style={{ fontSize: 12 }}>部门主管</span>
                <b style={{ fontSize: 16, fontWeight: 600 }}>{cur.headName ?? '还没指定'}</b>
                <span style={{ fontSize: 12.5, color: '#5a5a5a' }}>
                  {cur.headUserId ? '审批本部门（含下级部门）同事上传的文件和新增客户申请；主管自己上传的直接通过' : '没指定主管时，本部门的事项交给上级部门的主管；一路都没有就交系统管理员'}
                </span>
              </div>
              <select className="inp" style={{ height: 34, width: 200 }} value={cur.headUserId ?? ''} aria-label="更换主管"
                onChange={(e) => void act(() => put(`/api/departments/${cur.id}/head`, { userId: e.target.value || null }))}>
                <option value="">（不设主管）</option>
                {employees.filter((u) => u.isActive).map((u) => <option key={u.id} value={u.id}>{u.displayName}（{u.username}）</option>)}
              </select>
            </div>

            <div className="dash-card" style={{ padding: 0, overflow: 'hidden' }}>
              <div className="dash-sec-hd" style={{ padding: '16px 22px 8px' }}>
                <h2 className="dash-h2">成员 · {members.length} 人</h2>
                <select className="inp" style={{ height: 32, width: 220 }} value="" aria-label="加人到本部门"
                  onChange={(e) => { if (e.target.value) void act(() => put(`/api/departments/members/${e.target.value}`, { departmentId: cur.id })); }}>
                  <option value="">加人到本部门…</option>
                  {employees.filter((u) => u.isActive && u.departmentId !== cur.id).map((u) => (
                    <option key={u.id} value={u.id}>{u.displayName}（{u.departmentId ? pathOf(list, u.departmentId).slice(-1)[0] : '未分配'}）</option>
                  ))}
                </select>
              </div>
              <Members rows={members} depts={list} deptId={cur.id} headId={cur.headUserId}
                onMove={(u, d) => void act(() => put(`/api/departments/members/${u}`, { departmentId: d }))}
                onHead={(u) => void act(() => put(`/api/departments/${cur.id}/head`, { userId: u }))} />
            </div>
          </>
        ) : !depts.isLoading && <div className="dash-empty">左边先新建一个部门。</div>}
      </section>
    </div>
  );
}

function Members({ rows, depts, deptId, headId, onMove, onHead }: {
  rows: UserRow[]; depts: DeptRow[]; deptId: string | null; headId?: string | null;
  onMove: (userId: string, deptId: string | null) => void; onHead?: (userId: string) => void;
}) {
  if (rows.length === 0) return <div className="dash-empty" style={{ padding: '10px 22px 20px' }}>这里还没有人。</div>;
  return (
    <div style={{ overflowX: 'auto' }}>
      <table className="us-tbl fm-tbl">
        <thead><tr><th>姓名</th><th>账号</th><th>角色（功能权限）</th><th>状态</th><th>调到</th><th style={{ textAlign: 'right' }}>操作</th></tr></thead>
        <tbody>
          {rows.map((u) => (
            <tr key={u.id} style={{ opacity: u.isActive ? 1 : 0.55 }}>
              <td>
                <span className="us-who">
                  <i aria-hidden="true" style={u.id === headId ? { background: 'var(--brand)' } : undefined}>{u.displayName.slice(0, 1)}</i>
                  <b>{u.displayName}</b>
                  {u.id === headId && <span className="org-tag">主管</span>}
                </span>
              </td>
              <td style={{ color: 'var(--ink-2)' }}>{u.username}</td>
              <td>{u.roleName ?? '未指派'}</td>
              <td style={{ color: u.isActive ? '#1c6b45' : 'var(--ink-4)', fontSize: 12 }}><Icon name="dot" size={12} style={{ verticalAlign: '-2px' }} /> {u.isActive ? '正常' : '已停用'}</td>
              <td>
                <select className="inp" style={{ height: 30, width: 170 }} value={deptId ?? ''} aria-label={`把 ${u.displayName} 调到`}
                  onChange={(e) => onMove(u.id, e.target.value || null)}>
                  <option value="">未分配部门</option>
                  {depts.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
                </select>
              </td>
              <td style={{ textAlign: 'right' }}>
                {onHead && u.id !== headId && u.isActive && <button type="button" className="fm-act" onClick={() => onHead(u.id)}>设为主管</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
