import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { createContext, Fragment, useEffect, useState } from 'react';
import { ADMIN_NAV } from './pages/admin/AdminLayout';
import { useAuth } from './lib/auth';
import { ClsBadge } from './components/Common';
import { Icon } from './components/Icons';
import { Perm } from './lib/types';

interface NavItem { to: string; label: string; icon: string; }

/** 侧栏导航按角色与权限给：业务角色以对话为首页，其余入口是对话里卡片深链过去的那几页，
 * 摆出来让人也能直接进；管理后台与总部审核台仍是各自独立的管理界面（表 9-1、表 3-1）。 */
function navFor(roleCode: string, has: (p: string) => boolean): NavItem[] {
  switch (roleCode) {
    case 'admin':
      return [{ to: '/admin', label: '管理后台', icon: 'settings' }];
    case 'hq_reviewer':
      return [{ to: '/review', label: '审核台', icon: 'shield' }, { to: '/chat', label: '共享库问答', icon: 'chat' },
        ...(has(Perm.CorpusUpload) ? [{ to: '/files', label: '文件管理', icon: 'upload' }] : [])];
    default: {
      const items: NavItem[] = [];
      if (has(Perm.QaInternal) || has(Perm.QaPublic) || has(Perm.CaseRead)) items.push({ to: '/chat', label: '首页', icon: 'home' });
      if (has(Perm.ProjectSearch)) items.push({ to: '/projects', label: '历史项目', icon: 'folder' });
      if (has(Perm.Generate)) items.push({ to: '/generate', label: '我的文档', icon: 'file' });
      if (has(Perm.CaseRead)) items.push({ to: '/cases', label: '故障案例', icon: 'wrench' });
      if (has(Perm.TicketHandle)) items.push({ to: '/tickets', label: '报修工单', icon: 'ticket' });
      if (has(Perm.CorpusUpload)) items.push({ to: '/files', label: '文件管理', icon: 'upload' });
      return items;
    }
  }
}

/** 侧栏里留给当前页面的一块地方：对话页把「最近的对话」放进来（经 portal），
 * 对话的状态仍归对话页自己管，外框只出位置。 */
export const SideSlotCtx = createContext<HTMLElement | null>(null);

const COLLAPSE_KEY = 'htlab.side.min';

export default function Shell() {
  const { profile, logout } = useAuth();
  const nav = useNavigate();
  const location = useLocation();
  const [menuOpen, setMenuOpen] = useState(false);
  const [slot, setSlot] = useState<HTMLElement | null>(null);
  const [min, setMin] = useState(() => {
    try { return localStorage.getItem(COLLAPSE_KEY) === '1'; } catch { return false; }
  });
  useEffect(() => {
    try { localStorage.setItem(COLLAPSE_KEY, min ? '1' : '0'); } catch { /* 存不了就只在本页生效 */ }
  }, [min]);
  if (!profile) return null;
  const items = navFor(profile.roleCode, (p) => profile.permissions.includes(p));
  const canChat = items.some((i) => i.to === '/chat');
  // 在管理后台里：侧栏直接换成后台的分组菜单
  const inAdmin = location.pathname === '/admin' || location.pathname.startsWith('/admin/');

  return (
    <div className="shell">
      <aside className={'side' + (min ? ' min' : '')} aria-label="侧栏">
        <div className="side-top">
          {!min && <img className="brand-logo side-logo" src="/htlab-logo.png" alt="HTLAB 企业智能协作平台" />}
          <button className="side-icon side-collapse" aria-label={min ? '展开侧栏' : '收起侧栏'} title={min ? '展开侧栏' : '收起侧栏'}
            onClick={() => setMin((v) => !v)}>
            <Icon name="panel" size={18} />
          </button>
        </div>

        {canChat && (
          <button className="side-new" title="新建对话"
            onClick={() => nav('/chat', { state: { fresh: Date.now() } })}>
            <Icon name="plus" size={16} stroke={2.2} />
            {!min && <span className="side-label">新建对话</span>}
          </button>
        )}

        <nav className="side-nav" aria-label="主导航">
          {inAdmin ? ADMIN_NAV.map((g) => (
            <Fragment key={g.label}>
              {!min && <div className="side-grp side-label">{g.label}</div>}
              {g.items.map((it) => (
                <NavLink key={it.to} to={it.to} end={it.end} title={it.name}
                  className={({ isActive }) => 'side-link' + (isActive ? ' on' : '')}>
                  <Icon name={it.icon} />
                  {!min && <span className="side-label">{it.name}</span>}
                </NavLink>
              ))}
            </Fragment>
          )) : items.map((i) => (
            <NavLink key={i.to} to={i.to} title={i.label}
              className={({ isActive }) => 'side-link' + (isActive ? ' on' : '')}>
              <Icon name={i.icon} />
              {!min && <span className="side-label">{i.label}</span>}
            </NavLink>
          ))}
        </nav>

        <div className="side-slot" ref={setSlot} />

        <div className="side-me">
          <button className="side-me-btn" aria-haspopup="menu" aria-expanded={menuOpen}
            onClick={() => setMenuOpen((v) => !v)}>
            <div className="side-ava" aria-hidden="true">{profile.displayName.slice(0, 1)}</div>
            <div className="side-me-txt" style={{ minWidth: 0 }}>
              <div className="side-me-n">{profile.displayName}</div>
              <div className="side-me-r">{profile.roleName}{profile.companyName ? ` · ${profile.companyName}` : ''}</div>
            </div>
          </button>
          {menuOpen && (
            <div className="side-menu" role="menu" onMouseLeave={() => setMenuOpen(false)}>
              <div className="side-cls">
                可访问密级 {profile.classifications.map((c) => <ClsBadge key={c} cls={c} />)}
              </div>
              <button className="item" role="menuitem"
                onClick={() => { setMenuOpen(false); nav('/profile'); }}>个人中心</button>
              <button className="item" role="menuitem"
                onClick={() => { void logout().then(() => nav('/login')); }}>退出登录</button>
            </div>
          )}
        </div>
      </aside>

      <main className="shell-main">
        <SideSlotCtx.Provider value={slot}>
          <Outlet />
        </SideSlotCtx.Provider>
      </main>
    </div>
  );
}
