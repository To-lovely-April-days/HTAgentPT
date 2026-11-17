// E18 使用统计（见 UsageTab）+ E17 审计日志（只读，无编辑删除——能改的日志不是日志）。
import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get } from '../../lib/api';
import { Spinner } from '../../components/Common';
import UsageTab from './UsageTab';

interface AuditRow {
  id: number; at: string; username: string | null; action: string; targetType: string | null;
  targetId: string | null; result: string; ip: string | null; terminalId: string | null; detail: string | null;
}

export default function StatsPage() {
  const [tab, setTab] = useState<'stats' | 'audit'>('stats');
  const tabStyle = (on: boolean): React.CSSProperties => ({
    height: 48, display: 'inline-flex', alignItems: 'center', fontSize: 14, cursor: 'pointer',
    background: 'none', border: 'none', fontFamily: 'var(--font)', padding: 0,
    borderBottom: on ? '2px solid var(--ink)' : '2px solid transparent',
    color: on ? 'var(--ink)' : '#5a5a5a', fontWeight: on ? 600 : 400,
  });
  return (
    <>
      <div role="tablist" style={{ flexShrink: 0, display: 'flex', gap: 28, padding: '0 40px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <button type="button" role="tab" aria-selected={tab === 'stats'} style={tabStyle(tab === 'stats')} onClick={() => setTab('stats')}>使用统计</button>
        <button type="button" role="tab" aria-selected={tab === 'audit'} style={tabStyle(tab === 'audit')} onClick={() => setTab('audit')}>审计日志</button>
      </div>
      {tab === 'stats' ? <UsageTab /> : <AuditTab />}
    </>
  );
}

// ─── E17 审计（只写不改：本页没有也不会有编辑与删除动作）──────
function AuditTab() {
  const [username, setUsername] = useState('');
  const [action, setAction] = useState('');
  const q = useQuery({
    queryKey: ['audit-logs', username, action],
    queryFn: () => {
      const p = new URLSearchParams();
      if (username.trim()) p.set('username', username.trim());
      if (action.trim()) p.set('action', action.trim());
      p.set('limit', '200');
      return get<AuditRow[]>(`/api/audit/logs?${p}`);
    },
  });
  const fi: React.CSSProperties = { height: 28, padding: '0 9px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };
  return (
    <>
      <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '10px 40px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <input style={{ ...fi, width: 140 }} placeholder="按账号过滤" value={username} onChange={(e) => setUsername(e.target.value)} />
        <input style={{ ...fi, width: 180 }} className="m" placeholder="按操作类型过滤" value={action} onChange={(e) => setAction(e.target.value)} />
        <div style={{ flexGrow: 1 }} />
        <span className="hint">只写不改，按时间分区留存不少于十二个月；越权拒绝独立留痕、不含对象元数据</span>
      </div>
      <div className="sc" style={{ flexGrow: 1, minHeight: 0, background: 'var(--panel)' }}>
        {q.isLoading && <div style={{ padding: 24 }}><Spinner text="载入…" /></div>}
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <tbody>
            {(q.data ?? []).map((a) => (
              <tr key={a.id}>
                <td className="m" style={{ ...tdStat, width: 130, color: 'var(--ink-3)' }}>{a.at.slice(0, 16).replace('T', ' ')}</td>
                <td style={{ ...tdStat, width: 110 }}>{a.username ?? '—'}</td>
                <td className="m" style={{ ...tdStat, width: 170 }}>{a.action}</td>
                <td style={{ ...tdStat, width: 80 }}>
                  <span className="pill" style={a.result === 'Denied'
                    ? { background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' }
                    : { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' }}>
                    {a.result === 'Denied' ? '拒绝' : '成功'}
                  </span>
                </td>
                <td className="m" style={{ ...tdStat, color: 'var(--ink-2)' }}>{a.targetType ? `${a.targetType} ${a.targetId ?? ''}` : '—'}</td>
                <td className="m" style={{ ...tdStat, color: 'var(--ink-3)', maxWidth: 300, overflow: 'hidden', textOverflow: 'ellipsis' }}>{a.detail ?? ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

const tdStat: React.CSSProperties = { padding: '7px 10px', fontSize: 12, borderBottom: '1px solid var(--line-soft)', whiteSpace: 'nowrap' };
