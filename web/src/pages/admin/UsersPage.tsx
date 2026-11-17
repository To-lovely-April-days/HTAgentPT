// 用户与权限：三个页签。
// · 组织架构：部门树和主管——决定「谁审谁」；
// · 账号：开通、改角色、调部门、重置口令（全系统唯一的改密通道）、停用与导出记录（离职回收）；
// · 角色与功能权限：每个角色能用哪些功能、能看哪些密级。
import { useState } from 'react';
import type React from 'react';
import OrgTreeTab from './OrgTreeTab';
import AccountsTab from './AccountsTab';
import RolesTab from './RolesTab';

type Tab = 'org' | 'accounts' | 'roles';

export default function UsersPage() {
  const [tab, setTab] = useState<Tab>('org');
  const tabStyle = (on: boolean): React.CSSProperties => ({
    height: 48, display: 'inline-flex', alignItems: 'center', fontSize: 14, cursor: 'pointer', background: 'none', border: 'none',
    fontFamily: 'var(--font)', padding: 0, borderBottom: on ? '2px solid var(--ink)' : '2px solid transparent',
    color: on ? 'var(--ink)' : '#5a5a5a', fontWeight: on ? 600 : 400,
  });
  const tabs: [Tab, string][] = [['org', '组织架构'], ['accounts', '账号'], ['roles', '角色与功能权限']];
  return (
    <>
      <div role="tablist" style={{ flexShrink: 0, display: 'flex', gap: 28, padding: '0 40px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        {tabs.map(([k, label]) => (
          <button key={k} type="button" role="tab" aria-selected={tab === k} style={tabStyle(tab === k)} onClick={() => setTab(k)}>{label}</button>
        ))}
      </div>
      {tab === 'org' && <OrgTreeTab />}
      {tab === 'accounts' && <AccountsTab />}
      {tab === 'roles' && <RolesTab />}
    </>
  );
}
