// 管理后台框架：谁能进、看到哪些菜单都按权限给（不认角色名）。部门主管不是角色，按组织架构认（审批接口说他是不是主管）。
import { Outlet } from 'react-router-dom';
import { Perm } from '../../lib/types';

/** 能进后台的管理类权限：有任何一项，侧栏就有「管理后台」。部门主管另算。 */
export const ADMIN_PERMS: string[] = [
  Perm.CorpusManage, Perm.KbManage, Perm.MetaManage, Perm.TemplateManage, Perm.UserManage,
  Perm.SystemConfig, Perm.AuditRead, Perm.PriceManage, Perm.CaseReview,
];

interface AdminNavItem { to: string; name: string; icon: string; end?: boolean; perms?: string[]; head?: boolean }

/** 后台菜单（按块分组）。每项写明要哪些权限（有其一即可）；head=部门主管才有。由外框侧栏在 /admin 下渲染。 */
const ADMIN_NAV: { label: string; items: AdminNavItem[] }[] = [
  { label: '', items: [{ to: '/admin', name: '后台首页', icon: 'home', end: true }] },
  {
    label: '资料',
    items: [
      { to: '/admin/corpus', name: '资料管理', icon: 'file', perms: [Perm.CorpusManage] },
      { to: '/admin/kbs', name: '知识库', icon: 'folder', perms: [Perm.KbManage, Perm.CorpusManage] },
      { to: '/admin/prices', name: '价格库', icon: 'ticket', perms: [Perm.PriceManage, Perm.PriceView] },
      { to: '/admin/meta', name: '元数据与词表', icon: 'ticket', perms: [Perm.MetaManage] },
      { to: '/admin/quality', name: '回答质量', icon: 'check', perms: [Perm.CorpusManage] },
    ],
  },
  {
    label: '模板与技能',
    items: [
      { to: '/admin/templates', name: '模板', icon: 'chat', perms: [Perm.TemplateManage] },
      { to: '/admin/skills', name: '技能', icon: 'book', perms: [Perm.TemplateManage] },
      { to: '/admin/hazard-data', name: '国家目录', icon: 'shield', perms: [Perm.TemplateManage] },
      { to: '/admin/ppt-templates', name: 'PPT 模板', icon: 'slides', perms: [Perm.TemplateManage] },
    ],
  },
  {
    label: '售后',
    items: [
      { to: '/tickets', name: '报修工单', icon: 'ticket', perms: [Perm.TicketHandle] },
      { to: '/review', name: '审定案例', icon: 'shield', perms: [Perm.CaseReview] },
    ],
  },
  { label: '部门', items: [{ to: '/approvals', name: '审批', icon: 'shield', head: true }] },
  {
    label: '账号与系统',
    items: [
      { to: '/admin/users', name: '用户与权限', icon: 'user', perms: [Perm.UserManage] },
      { to: '/admin/settings', name: '系统设置', icon: 'settings', perms: [Perm.SystemConfig] },
      { to: '/admin/audit', name: '审计与统计', icon: 'shield', perms: [Perm.AuditRead] },
    ],
  },
];

/** 这个人在后台能看到的菜单：没权限的项不出现，整组都没有就整组不出现。
 * 系统管理员（能管全部资料）审批走「资料管理」，不再单列「审批」。 */
export function adminNavFor(has: (p: string) => boolean, isHead: boolean) {
  return ADMIN_NAV
    .map((g) => ({ ...g, items: g.items.filter((it) => (it.head ? isHead && !has(Perm.CorpusManage) : !it.perms || it.perms.some(has))) }))
    .filter((g) => g.items.length > 0);
}

/** 能不能进后台：有管理类权限，或者是部门主管。 */
export const canEnterAdmin = (has: (p: string) => boolean, isHead: boolean) => isHead || ADMIN_PERMS.some(has);

export default function AdminLayout() {
  return (
    <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0, background: 'var(--panel)' }}>
      <Outlet />
    </div>
  );
}

export { AdminHome } from './AdminHome';

/** 后台内尚未接入的模块占位。 */
export function AdminStub({ name, api }: { name: string; api: string }) {
  return (
    <div style={{ flexGrow: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
      <div className="card" style={{ maxWidth: 460, padding: '20px 24px' }}>
        <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 6 }}>「{name}」在后续前端批次接入</div>
        <div className="hint" style={{ lineHeight: 1.7 }}>服务端能力已就绪（{api}），高保真原型见 design/ 目录对应画布——原型即本页的实现规格。</div>
      </div>
    </div>
  );
}
