// 用户与权限 › 角色与功能权限：左边角色列表，右边勾这个角色能看哪些密级、能用哪些功能（按组勾，勾组即全选）。
// 底部改动条列出这次新开/去掉了什么、影响几个人，确认后保存并记审计。内置角色能改权限、不能删；可以复制出新角色。
// 角色管「能用什么、能看什么」；「谁审谁」由组织架构决定，和角色无关。
import { useMemo, useRef, useState } from 'react';
import type React from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put, ApiError } from '../../lib/api';
import type { Classification, RoleRow, UserRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';
import { CLS_DESC, CLS_ORDER, CLS_TAG, PERM_GROUPS, permLabel } from './permCatalog';
import { Icon } from '../../components/Icons';

interface Draft { id: string | null; code: string; name: string; classifications: Classification[]; permissions: string[]; isSystem: boolean }

const toDraft = (r: RoleRow): Draft => ({ id: r.id, code: r.code, name: r.name, classifications: [...r.classifications], permissions: [...r.permissions], isSystem: r.isSystem });

/** 三态勾选框：全选 ✓、部分 −、没选。 */
function Tri({ state }: { state: 'all' | 'some' | 'none' }) {
  return <span className={'rl-cb ' + state} aria-hidden="true">{state === 'all' ? <Icon name="check" size={12} stroke={2.6} /> : state === 'some' ? '−' : ''}</span>;
}

export default function RolesTab() {
  const qc = useQueryClient();
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => get<RoleRow[]>('/api/roles') });
  const users = useQuery({ queryKey: ['users'], queryFn: () => get<UserRow[]>('/api/users') });
  const [selId, setSelId] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [showUsers, setShowUsers] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const codeSeq = useRef(1);

  const list = useMemo(() => roles.data ?? [], [roles.data]);
  const userCount = (code: string) => (users.data ?? []).filter((u) => u.roleCode === code && u.isActive).length;
  const current = draft?.id === null ? null : list.find((r) => r.id === (selId ?? list[0]?.id)) ?? null;
  // 编辑中的那份：新建/复制时是 draft；否则选中角色的副本（没改动时 draft 为空）
  const edit: Draft | null = draft ?? (current ? toDraft(current) : null);
  const original = draft?.id ? list.find((r) => r.id === draft.id) : current;
  const added = edit ? edit.permissions.filter((p) => !(original?.permissions ?? []).includes(p)) : [];
  const removed = edit && original ? original.permissions.filter((p) => !edit.permissions.includes(p)) : [];
  const clsChanged = !!edit && !!original && (edit.classifications.length !== original.classifications.length
    || edit.classifications.some((c) => !original.classifications.includes(c)));
  const nameChanged = !!edit && !!original && edit.name !== original.name;
  const dirty = !!draft && (draft.id === null || added.length > 0 || removed.length > 0 || clsChanged || nameChanged);
  const affected = original ? userCount(original.code) : 0;

  const change = (fn: (d: Draft) => Draft) => { if (edit) { setDraft(fn(edit)); setDone(null); } };
  const togglePerm = (key: string) => change((d) => ({ ...d, permissions: d.permissions.includes(key) ? d.permissions.filter((p) => p !== key) : [...d.permissions, key] }));
  const toggleGroup = (keys: string[], on: boolean) => change((d) => ({
    ...d, permissions: on ? [...new Set([...d.permissions, ...keys])] : d.permissions.filter((p) => !keys.includes(p)),
  }));
  const toggleCls = (c: Classification) => change((d) => ({ ...d, classifications: d.classifications.includes(c) ? d.classifications.filter((x) => x !== c) : [...d.classifications, c] }));

  const pick = (id: string) => {
    if (dirty && !window.confirm('这个角色的改动还没保存，确定要离开吗？')) return;
    setSelId(id); setDraft(null); setShowUsers(false); setError(null); setDone(null);
  };
  const startNew = (from?: Draft) => {
    if (dirty && !window.confirm('这个角色的改动还没保存，确定要离开吗？')) return;
    setDraft({ id: null, code: `role_${Date.now().toString(36).slice(-4)}${codeSeq.current++}`, name: from ? `${from.name}（副本）` : '新角色',
      classifications: from ? [...from.classifications] : ['Internal', 'Public'], permissions: from ? [...from.permissions] : [], isSystem: false });
    setError(null); setDone(null);
  };

  const save = async () => {
    if (!edit) return;
    setBusy(true); setError(null);
    const body = { code: edit.code.trim(), name: edit.name.trim(), classifications: edit.classifications, permissions: edit.permissions };
    try {
      if (edit.id === null) {
        const r = await post<{ id: string }>('/api/roles', body);
        setSelId(r.id);
        setDone(`已新建角色「${body.name}」`);
      } else {
        await put(`/api/roles/${edit.id}`, body);
        setDone(`已保存，对 ${affected} 人立即生效`);
      }
      setDraft(null);
      void qc.invalidateQueries({ queryKey: ['roles'] });
      void qc.invalidateQueries({ queryKey: ['users'] });
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '保存失败');
    } finally { setBusy(false); }
  };

  const known = new Set(PERM_GROUPS.flatMap((g) => g.items.map((i) => i.key)));
  const others = (edit?.permissions ?? []).filter((p) => !known.has(p));

  return (
    <div className="org">
      <section className="org-tree" style={{ padding: '18px 12px' }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '0 8px 10px' }}>
          <b style={{ fontSize: 15, fontWeight: 600 }}>角色</b>
          <button type="button" className="gbtn" onClick={() => startNew()}><Icon name="plus" size={13} className="gi l" />新建角色</button>
        </div>
        {roles.isLoading && <Spinner text="载入…" />}
        <div className="sc" style={{ display: 'flex', flexDirection: 'column', gap: 2, overflowY: 'auto' }}>
          {draft?.id === null && (
            <div className="rl-item on"><span><b>{draft.name || '新角色'}</b><small>未保存</small></span></div>
          )}
          {list.map((r) => {
            const on = draft?.id !== null && (selId ?? list[0]?.id) === r.id;
            return (
              <button key={r.id} type="button" className={'rl-item' + (on ? ' on' : '')} onClick={() => pick(r.id)}>
                <span>
                  <b>{r.name}</b>
                  <small>{r.isSystem ? '内置' : '自建'} · {CLS_ORDER.filter((c) => r.classifications.includes(c)).map((c) => CLS_TAG[c][0]).join('/') || '无密级'} · {r.permissions.length} 项功能</small>
                </span>
                <i>{userCount(r.code)} 人</i>
              </button>
            );
          })}
        </div>
        <p className="dash-muted" style={{ margin: '12px 8px 0', fontSize: 12, lineHeight: 1.7 }}>
          「内置」角色可以调整权限，不能删除。角色决定能用哪些功能、能看哪些密级；谁审批谁由组织架构决定，和角色无关。
        </p>
      </section>

      <section className="org-main sc">
        {!edit ? <div className="dash-empty">左边选一个角色。</div> : (
          <>
            <div className="dash-head">
              <div style={{ minWidth: 0 }}>
                <div className="dash-muted">
                  {edit.id === null ? '新角色' : edit.isSystem ? '内置角色' : '自建角色'} · 代码{' '}
                  {edit.id === null
                    ? <input className="inp m" style={{ height: 26, width: 160, display: 'inline-block' }} value={edit.code} aria-label="角色代码"
                        onChange={(e) => change((d) => ({ ...d, code: e.target.value.toLowerCase() }))} />
                    : <span className="m">{edit.code}</span>}
                  {edit.id !== null && <> · {affected} 人在用</>}
                </div>
                <input className="rl-name" value={edit.name} aria-label="角色名称" onChange={(e) => change((d) => ({ ...d, name: e.target.value }))} />
              </div>
              {edit.id !== null && (
                <div style={{ display: 'flex', gap: 8 }}>
                  <button type="button" className="gbtn" onClick={() => startNew(edit)}>复制为新角色</button>
                  <button type="button" className="gbtn" aria-expanded={showUsers} onClick={() => setShowUsers(!showUsers)}>看谁在用</button>
                </div>
              )}
            </div>

            {showUsers && original && (
              <div className="dash-card" style={{ padding: '12px 18px', fontSize: 13, lineHeight: 1.9 }}>
                {(users.data ?? []).filter((u) => u.roleCode === original.code).map((u) => (
                  <span key={u.id} className="rl-user" style={{ opacity: u.isActive ? 1 : 0.5 }}>{u.displayName}</span>
                ))}
                {affected === 0 && <span className="dash-muted">还没有人用这个角色。</span>}
              </div>
            )}

            {error && <ErrorBox message={error} />}
            {done && <div className="dash-card acc-done">{done}</div>}

            <div className="dash-card rl-cls">
              <div style={{ display: 'flex', flexDirection: 'column', gap: 3, width: 200 }}>
                <b style={{ fontWeight: 600 }}>能看的密级</b>
                <span className="dash-muted" style={{ fontSize: 12 }}>问答、搜索、下载都按这个挡</span>
              </div>
              {CLS_ORDER.map((c) => {
                const on = edit.classifications.includes(c);
                return (
                  <button key={c} type="button" className={'rl-clsb' + (on ? ' on' : '')} aria-pressed={on} onClick={() => toggleCls(c)}>
                    <Tri state={on ? 'all' : 'none'} /><b>{CLS_TAG[c][0]}</b><span>{CLS_DESC[c]}</span>
                  </button>
                );
              })}
            </div>

            <div className="dash-card" style={{ padding: 0, overflow: 'hidden' }}>
              <div style={{ padding: '14px 22px', borderBottom: '1px solid var(--line)', display: 'flex', alignItems: 'baseline', gap: 12 }}>
                <b style={{ fontSize: 15, fontWeight: 600 }}>功能权限</b>
                <span className="dash-muted">勾分组就是整组全选；半勾表示组里部分开了</span>
              </div>
              <div className="rl-grid">
                {PERM_GROUPS.map((g) => {
                  const keys = g.items.map((i) => i.key);
                  const n = keys.filter((k) => edit.permissions.includes(k)).length;
                  const state = n === keys.length ? 'all' : n === 0 ? 'none' : 'some';
                  return (
                    <div key={g.title} className="rl-group">
                      <button type="button" className="rl-row rl-gh" aria-pressed={state === 'all'} onClick={() => toggleGroup(keys, state !== 'all')}>
                        <Tri state={state} /><b>{g.title}</b><small>{n} / {keys.length}</small>
                      </button>
                      {g.items.map((i) => {
                        const on = edit.permissions.includes(i.key);
                        const was = (original?.permissions ?? []).includes(i.key);
                        const mark = edit.id !== null && on !== was ? (on ? 'add' : 'del') : '';
                        return (
                          <button key={i.key} type="button" className={'rl-row rl-it ' + mark} aria-pressed={on} onClick={() => togglePerm(i.key)}>
                            <Tri state={on ? 'all' : 'none'} />
                            <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0, textAlign: 'left' }}>
                              <span>{i.label}{' '}
                                {mark === 'add' && <em className="rl-tag add">新开</em>}
                                {mark === 'del' && <em className="rl-tag del">去掉</em>}
                                {!mark && i.admin && <em className="rl-tag">后台</em>}
                              </span>
                              {i.desc && <small>{i.desc}</small>}
                            </span>
                          </button>
                        );
                      })}
                    </div>
                  );
                })}
              </div>
              {others.length > 0 && <div className="dash-muted" style={{ padding: '10px 22px' }}>另有：{others.join('、')}（保留不动）</div>}
            </div>

            {dirty && (
              <div className="rl-save">
                <span>
                  {edit.id === null ? <b>新角色，还没保存</b> : <>
                    <b>改动：</b>
                    {[...added.map((p) => <span key={`a${p}`} style={{ color: '#1c6b45' }}><Icon name="plus" size={12} className="gi l" />{permLabel(p)}</span>),
                      ...removed.map((p) => <span key={`r${p}`} style={{ color: 'var(--brand-fg)' }}>－ {permLabel(p)}</span>),
                      ...(clsChanged ? [<span key="cls">密级改为 {CLS_ORDER.filter((c) => edit.classifications.includes(c)).map((c) => CLS_TAG[c][0]).join('/') || '无'}</span>] : []),
                      ...(nameChanged ? [<span key="name">改名</span>] : [])]
                      .reduce<React.ReactNode[]>((acc, el, i) => (i ? [...acc, ' · ', el] : [el]), [])}
                  </>}
                </span>
                <span className="dash-muted">{edit.id === null ? '保存后就能在开通账号时选它' : `保存后对 ${affected} 人立即生效，记入审计`}</span>
                <div style={{ flex: 1 }} />
                <button type="button" className="gbtn" onClick={() => { setDraft(null); setError(null); }}>撤销</button>
                <button type="button" className="pbtn" disabled={busy || !edit.name.trim()} onClick={() => void save()}>{busy ? '保存中…' : '保存'}</button>
              </div>
            )}
          </>
        )}
      </section>
    </div>
  );
}
