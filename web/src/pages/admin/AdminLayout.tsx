// E 系管理后台框架：左栏按域分组（落地 / 语料域 / 能力域 / 治理域）。
// 管理后台七项 + 审核台归总部的划分见决策 1；本壳只承载公司节点的七项。
import React, { useState } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post } from '../../lib/api';
import type { DocRow, TermRow, ClauseRow } from '../../lib/types';

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

interface OpsAlert {
  kind: string; runId: string; startedAt: string; error: string | null;
  consecutiveFailures: number; acknowledgedBy: string | null; acknowledgedAt: string | null;
}
const BACKUP_KIND_LABEL: Record<string, string> = {
  LocalIncremental: '本地增量备份', LocalFull: '本地全量备份', RemoteFull: '异地全量备份',
};

/** E0 后台首页：告警条 +「不处理就会一直卡着」的待办。
 * 「我已知悉」只写审计并追加知悉行，告警条保持显示——同类型备份真正成功一次才撤（FR-9.3）。 */
export function AdminHome() {
  const qc = useQueryClient();
  const [acking, setAcking] = useState(false);
  const alerts = useQuery({ queryKey: ['ops-alerts'], queryFn: () => get<OpsAlert[]>('/api/ops/alerts') });
  const notParsed = useQuery({ queryKey: ['docs', '', 'NotParsed', ''], queryFn: () => get<DocRow[]>('/api/documents?status=NotParsed') });
  const failed = useQuery({ queryKey: ['docs', '', 'Failed', ''], queryFn: () => get<DocRow[]>('/api/documents?status=Failed') });
  const terms = useQuery({ queryKey: ['terms-pending'], queryFn: () => get<TermRow[]>('/api/terms?status=Pending') });
  const clauses = useQuery({ queryKey: ['clauses-pending'], queryFn: () => get<ClauseRow[]>('/api/clauses?status=Pending') });

  const ack = async (runId: string) => {
    setAcking(true);
    try {
      await post(`/api/ops/backup/${runId}/ack`);
      void qc.invalidateQueries({ queryKey: ['ops-alerts'] });
    } finally {
      setAcking(false);
    }
  };

  const todo: [string, number | undefined, string][] = [
    ['待解析文档', notParsed.data?.length, '/admin/corpus'],
    ['解析失败待处理', failed.data?.length, '/admin/corpus'],
    ['待确认术语', terms.data?.length, '/admin/meta'],
    ['待审定条款', clauses.data?.length, '/admin/templates'],
  ];

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '22px 26px' }}>
      {/* 内容列居中：宽屏下两侧留白均衡，而不是全部内容堆在左栏旁边 */}
      <div style={{ maxWidth: 1080, margin: '0 auto' }}>
      <div style={{ fontSize: 15, fontWeight: 600, marginBottom: 6 }}>管理后台</div>
      <div className="hint" style={{ marginBottom: 14 }}>
        告警知悉后不撤除——要等同类型任务真正成功一次才消。首屏只放不处理就会一直卡着的事。
      </div>

      {/* 告警条（FR-9.3：任务失败须告警，不得仅写日志） */}
      {(alerts.data ?? []).map((a) => (
        <div key={a.runId} style={{ display: 'flex', alignItems: 'flex-start', gap: 12, padding: '12px 15px', marginBottom: 10, background: 'var(--cls-conf-bg)', border: '1px solid var(--cls-conf-line)', borderRadius: 6 }}>
          <div style={{ flexGrow: 1, minWidth: 0 }}>
            <div style={{ fontSize: 13, fontWeight: 600, color: 'var(--cls-conf-fg)' }}>
              {BACKUP_KIND_LABEL[a.kind] ?? a.kind}失败{a.consecutiveFailures > 1 ? `（连续 ${a.consecutiveFailures} 次）` : ''}
            </div>
            <div style={{ fontSize: 12, lineHeight: 1.65, color: 'var(--cls-conf-fg)', marginTop: 3 }}>
              {a.error ?? '未记录具体原因'}　·　{a.startedAt.slice(0, 16).replace('T', ' ')}
            </div>
            {a.acknowledgedAt && (
              <div style={{ fontSize: 11.5, color: 'var(--cls-conf-fg)', opacity: .8, marginTop: 3 }}>
                已知悉 · {a.acknowledgedAt.slice(0, 16).replace('T', ' ')}——告警保持显示，直到该类备份成功一次
              </div>
            )}
          </div>
          {!a.acknowledgedAt && (
            <button className="gbtn" style={{ flexShrink: 0 }} disabled={acking} onClick={() => void ack(a.runId)}>我已知悉</button>
          )}
        </div>
      ))}
      {alerts.data && alerts.data.length === 0 && (
        <div style={{ padding: '10px 15px', marginBottom: 10, background: '#e6f2ec', border: '1px solid #c2ded1', borderRadius: 6, fontSize: 12.5, color: '#1c6b45' }}>
          备份任务无未消告警。
        </div>
      )}

      {/* 待办：不处理就会一直卡着的事——四卡等分撑满内容列 */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: 10, margin: '4px 0 22px' }}>
        {todo.map(([label, n, to]) => (
          <NavLink key={label} to={to} className="card" style={{ padding: '12px 14px', textDecoration: 'none', color: 'inherit' }}>
            <div className="m" style={{ fontSize: 20, fontWeight: 600, color: (n ?? 0) > 0 ? 'var(--cls-int-fg)' : 'var(--ink-3)' }}>{n ?? '…'}</div>
            <div style={{ fontSize: 12, color: 'var(--ink-2)', marginTop: 2 }}>{label}</div>
          </NavLink>
        ))}
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(300px, 1fr))', gap: 12 }}>
        {[
          ['语料管理', '上传登记、提交解析、失败处理', '/admin/corpus'],
          ['知识库管理', '分层（共享/私有/公开）与切分策略', '/admin/kbs'],
          ['元数据与词表', '受控词表维护、批量修正', '/admin/meta'],
          ['模板管理', '生成模板与槽位', '/admin/templates'],
          ['用户与权限', '账号、角色、重置口令', '/admin/users'],
          ['系统设置', '阈值、并发、节点与备份配置', '/admin/settings'],
          ['审计与统计', '只写不改的操作留痕与使用统计', '/admin/audit'],
        ].map(([name, desc, to]) => (
          <NavLink key={to} to={to} className="card" style={{ padding: '14px 16px', textDecoration: 'none', color: 'inherit' }}>
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 4 }}>{name}</div>
            <div className="hint" style={{ lineHeight: 1.6 }}>{desc}</div>
          </NavLink>
        ))}
      </div>
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
