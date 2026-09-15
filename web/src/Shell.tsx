import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useState } from 'react';
import { useAuth } from './lib/auth';
import { ClsBadge } from './components/Common';
import { Perm } from './lib/types';

interface NavItem { to: string; label: string; }

/** C2 框架：导航按角色成形（表 3-2），权限再兜一层——菜单少画不是安全边界，接口才是。 */
function navFor(roleCode: string, has: (p: string) => boolean): NavItem[] {
  switch (roleCode) {
    case 'presales':
      return [
        { to: '/qa', label: '问答' },
        { to: '/projects', label: '项目查询' },
        { to: '/generate', label: '方案生成' },
        { to: '/contract', label: '报价合同' },
        { to: '/translate', label: '翻译' },
      ].filter((i) => ({
        '/qa': has(Perm.QaInternal) || has(Perm.QaPublic),
        '/projects': has(Perm.ProjectSearch),
        '/generate': has(Perm.Generate),
        '/contract': has(Perm.GenerateContract),
        '/translate': has(Perm.Translate),
      })[i.to]);
    case 'aftersales':
      return [
        { to: '/qa', label: '问答' },
        { to: '/cases', label: '案例检索' },
        { to: '/cases/new', label: '案例录入' },
        { to: '/tickets', label: '报修工单' },
        { to: '/translate', label: '翻译' },
      ];
    case 'hq_reviewer':
      return [
        { to: '/review', label: '审核台' },
        { to: '/qa', label: '共享库问答' },
      ];
    case 'admin':
      return [{ to: '/admin', label: '管理后台' }];
    default:
      return [{ to: '/qa', label: '自助查询' }];
  }
}

export default function Shell() {
  const { profile, logout } = useAuth();
  const nav = useNavigate();
  const [menuOpen, setMenuOpen] = useState(false);
  if (!profile) return null;
  const items = navFor(profile.roleCode, (p) => profile.permissions.includes(p));
  const isHq = profile.roleCode === 'hq_reviewer';

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
      <header style={{
        height: 48, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 28,
        padding: '0 16px', background: 'var(--panel)', borderBottom: '1px solid var(--line)',
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 9 }}>
          <div style={{
            width: 24, height: 24, borderRadius: 4, color: '#fff', fontSize: 11, fontWeight: 600,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            background: isHq ? 'var(--ink-2)' : 'var(--accent)', letterSpacing: '.03em',
          }}>{isHq ? '集团' : 'HT'}</div>
          <div style={{ fontSize: 14, fontWeight: 600, letterSpacing: '.01em' }}>
            {isHq ? '集团共享库 · 总部节点' : `${profile.companyName || '霍桐实验仪器'} · 企业智能体`}
          </div>
        </div>
        <nav style={{ display: 'flex', alignItems: 'center', gap: 2 }}>
          {items.map((i) => (
            <NavLink key={i.to} to={i.to} end={i.to === '/cases'}
              className={({ isActive }) => 'nav-item' + (isActive ? ' on' : '')}>
              {i.label}
            </NavLink>
          ))}
        </nav>
        <div style={{ flexGrow: 1 }} />
        <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <span style={{ fontSize: 12, color: 'var(--ink-3)' }}>可访问密级</span>
          {profile.classifications.map((c) => <ClsBadge key={c} cls={c} />)}
        </div>
        <div style={{ width: 1, height: 20, background: 'var(--line)' }} />
        <div style={{ position: 'relative' }}>
          <button onClick={() => setMenuOpen((v) => !v)} style={{
            display: 'flex', alignItems: 'center', gap: 8, background: 'none', border: 'none',
            cursor: 'pointer', fontFamily: 'var(--font)', padding: '3px 4px', borderRadius: 5,
          }}>
            <div style={{
              width: 26, height: 26, borderRadius: '50%', background: '#e6ebf2', color: 'var(--ink-2)',
              display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: 12, fontWeight: 500,
            }}>{profile.displayName.slice(0, 1)}</div>
            <div style={{ lineHeight: 1.25, textAlign: 'left' }}>
              <div style={{ fontSize: 12, fontWeight: 500, color: 'var(--ink)' }}>{profile.displayName}</div>
              <div style={{ fontSize: 11, color: 'var(--ink-3)' }}>{profile.roleName}</div>
            </div>
          </button>
          {menuOpen && (
            <div className="card" style={{
              position: 'absolute', right: 0, top: 38, width: 168, padding: 6, zIndex: 20,
              boxShadow: '0 4px 16px rgba(28,37,48,.08)',
            }} onMouseLeave={() => setMenuOpen(false)}>
              <button className="item" style={{ margin: 0, width: '100%' }}
                onClick={() => { setMenuOpen(false); nav('/profile'); }}>个人中心</button>
              <button className="item" style={{ margin: 0, width: '100%' }}
                onClick={() => { void logout().then(() => nav('/login')); }}>退出登录</button>
            </div>
          )}
        </div>
      </header>
      <main style={{ flexGrow: 1, minHeight: 0, display: 'flex' }}>
        <Outlet />
      </main>
    </div>
  );
}
