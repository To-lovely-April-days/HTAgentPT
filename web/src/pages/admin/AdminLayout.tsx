// E 系管理后台框架：左栏按域分组（落地 / 语料域 / 能力域 / 治理域）。
// 管理后台七项 + 审核台归总部的划分见决策 1；本壳只承载公司节点的七项。
import { Outlet } from 'react-router-dom';

/** 后台菜单（按域分组）。由外框侧栏在 /admin 下渲染——不再另起一列，免得「外框侧栏 + 后台菜单列」叠成三栏。 */
export const ADMIN_NAV: { label: string; items: { to: string; name: string; icon: string; end?: boolean }[] }[] = [
  { label: '落地', items: [{ to: '/admin', name: '后台首页', icon: 'home', end: true }] },
  {
    label: '语料域',
    items: [
      { to: '/admin/corpus', name: '语料管理', icon: 'file' },
      { to: '/admin/kbs', name: '知识库管理', icon: 'folder' },
      { to: '/admin/prices', name: '价格库', icon: 'ticket' },
      { to: '/admin/meta', name: '元数据与词表', icon: 'ticket' },
      { to: '/admin/quality', name: '回答质量', icon: 'check' },
    ],
  },
  { label: '能力域', items: [{ to: '/admin/templates', name: '模板管理', icon: 'chat' }] },
  {
    label: '治理域',
    items: [
      { to: '/admin/users', name: '用户与权限', icon: 'user' },
      { to: '/admin/settings', name: '系统设置', icon: 'settings' },
      { to: '/admin/audit', name: '审计与统计', icon: 'shield' },
    ],
  },
];

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
