// E 系管理后台框架：左栏按域分组（落地 / 语料域 / 能力域 / 治理域）。
// 管理后台七项 + 审核台归总部的划分见决策 1；本壳只承载公司节点的七项。
import React from 'react';
import { NavLink, Outlet } from 'react-router-dom';

const GROUPS: { label: string; items: { to: string; name: string; end?: boolean }[] }[] = [
  { label: '落地', items: [{ to: '/admin', name: '后台首页', end: true }] },
  {
    label: '语料域',
    items: [
      { to: '/admin/corpus', name: '语料管理' },
      { to: '/admin/kbs', name: '知识库管理' },
      { to: '/admin/meta', name: '元数据与词表' },
    ],
  },
  { label: '能力域', items: [{ to: '/admin/templates', name: '模板管理' }] },
  {
    label: '治理域',
    items: [
      { to: '/admin/users', name: '用户与权限' },
      { to: '/admin/settings', name: '系统设置' },
      { to: '/admin/audit', name: '审计与统计' },
    ],
  },
];

export default function AdminLayout() {
  return (
    <div style={{ flexGrow: 1, display: 'flex', minHeight: 0, minWidth: 0 }}>
      <div style={{ width: 240, flexShrink: 0, background: 'var(--panel)', borderRight: '1px solid var(--line)', overflowY: 'auto' }}>
        {GROUPS.map((g) => (
          <React.Fragment key={g.label}>
            <div className="grp">{g.label}</div>
            {g.items.map((it) => (
              <NavLink key={it.to} to={it.to} end={it.end} className={({ isActive }) => `item${isActive ? ' on' : ''}`}
                style={{ display: 'block', textDecoration: 'none', color: 'inherit' }}>
                {it.name}
              </NavLink>
            ))}
          </React.Fragment>
        ))}
      </div>
      <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0 }}>
        <Outlet />
      </div>
    </div>
  );
}

/** 后台首页：管理员落地页（决策 3：带告警条的首页在运维批次接入——先给模块导览）。 */
export function AdminHome() {
  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '22px 26px' }}>
      <div style={{ fontSize: 15, fontWeight: 600, marginBottom: 6 }}>管理后台</div>
      <div className="hint" style={{ marginBottom: 18 }}>
        运行状态告警条（备份 / 解析引擎 / 同步链路）在运维批次接入本页——告警知悉后不撤除，恢复才撤。
      </div>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 12, maxWidth: 900 }}>
        {[
          ['语料管理', '上传登记、提交解析、失败处理', '/admin/corpus'],
          ['知识库管理', '分层（共享/私有/公开）与切分策略', '/admin/kbs'],
          ['元数据与词表', '受控词表维护、批量修正', '/admin/meta'],
          ['模板管理', '生成模板与槽位', '/admin/templates'],
          ['用户与权限', '账号、角色、重置口令', '/admin/users'],
          ['系统设置', '阈值、并发、节点与备份配置', '/admin/settings'],
          ['审计与统计', '只写不改的操作留痕与使用统计', '/admin/audit'],
        ].map(([name, desc, to]) => (
          <NavLink key={to} to={to} className="card" style={{ width: 272, padding: '14px 16px', textDecoration: 'none', color: 'inherit' }}>
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 4 }}>{name}</div>
            <div className="hint" style={{ lineHeight: 1.6 }}>{desc}</div>
          </NavLink>
        ))}
      </div>
    </div>
  );
}

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
