// E18 使用统计 + E17 审计日志（只读，无编辑删除——能改的日志不是日志）。
// 统计的图表决策：六模块六张小图（每张单序列，不需要分类色板，避开双轴）；
// 深浅只表示时间远近（本周深蓝/此前浅蓝），不表示分类；无结果比例不是故障率。
import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get } from '../../lib/api';
import { Spinner } from '../../components/Common';

interface WeekRow {
  weekStart: string; modules: Record<string, number>; totalCalls: number;
  activeUsers: number; hitRate: number | null; noResultRate: number | null;
}
interface NoResultRow { question: string; count: number; lastAt: string; hints: string | null; }
interface AuditRow {
  id: number; at: string; username: string | null; action: string; targetType: string | null;
  targetId: string | null; result: string; ip: string | null; terminalId: string | null; detail: string | null;
}

export default function StatsPage() {
  const [tab, setTab] = useState<'stats' | 'audit'>('stats');
  const tabStyle = (on: boolean): React.CSSProperties => ({
    height: 40, padding: '0 13px', display: 'inline-flex', alignItems: 'center', fontSize: 12.5, cursor: 'pointer',
    borderBottom: on ? '2px solid var(--accent)' : '2px solid transparent',
    color: on ? 'var(--accent)' : 'var(--ink-2)', fontWeight: on ? 600 : 400,
  });
  return (
    <>
      <div style={{ flexShrink: 0, display: 'flex', gap: 2, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <span style={tabStyle(tab === 'stats')} onClick={() => setTab('stats')}>使用统计</span>
        <span style={tabStyle(tab === 'audit')} onClick={() => setTab('audit')}>审计日志</span>
      </div>
      {tab === 'stats' ? <StatsTab /> : <AuditTab />}
    </>
  );
}

// ─── E18 ────────────────────────────────────────────────
function StatsTab() {
  const [view, setView] = useState<'chart' | 'table'>('chart');
  const usage = useQuery({ queryKey: ['stats-usage'], queryFn: () => get<WeekRow[]>('/api/stats/usage?weeks=8') });
  const noResult = useQuery({ queryKey: ['stats-noresult'], queryFn: () => get<NoResultRow[]>('/api/stats/no-result?days=7') });

  const weeks = usage.data ?? [];
  const modules = weeks.length > 0 ? Object.keys(weeks[0].modules) : [];

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
      <div style={{ maxWidth: 1120, margin: '0 auto' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
          <span style={{ fontSize: 13, fontWeight: 600 }}>近 8 周使用情况</span>
          <div style={{ flexGrow: 1 }} />
          <span className="hint">深浅表示时间远近（本周深、此前浅），不表示分类</span>
          <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => setView(view === 'chart' ? 'table' : 'chart')}>
            {view === 'chart' ? '数据表' : '图'}
          </button>
        </div>
        {usage.isLoading && <Spinner text="统计中…" />}

        {view === 'chart' && weeks.length > 0 && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 12, marginBottom: 8 }}>
            {modules.map((m) => (
              <MiniBar key={m} title={m} values={weeks.map((w) => ({ label: w.weekStart.slice(5), v: w.modules[m] ?? 0 }))} />
            ))}
            <MiniBar title="周活跃用户" values={weeks.map((w) => ({ label: w.weekStart.slice(5), v: w.activeUsers }))} />
            <MiniLine title="问答命中率 %" values={weeks.map((w) => ({ label: w.weekStart.slice(5), v: w.hitRate }))} />
            <MiniLine title="如实返回未找到 %" values={weeks.map((w) => ({ label: w.weekStart.slice(5), v: w.noResultRate }))} />
          </div>
        )}
        {view === 'table' && (
          <div className="card" style={{ padding: 0, overflow: 'auto', marginBottom: 8 }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
              <thead>
                <tr>{['周', ...modules, '总调用', '活跃', '命中率', '未找到率'].map((h) => (
                  <th key={h} style={{ padding: '7px 10px', textAlign: 'left', fontSize: 11.5, color: 'var(--ink-2)', background: 'var(--bg-soft)', borderBottom: '1px solid var(--line)', whiteSpace: 'nowrap' }}>{h}</th>
                ))}</tr>
              </thead>
              <tbody>
                {weeks.map((w) => (
                  <tr key={w.weekStart}>
                    <td className="m" style={tdStat}>{w.weekStart}</td>
                    {modules.map((m) => <td key={m} className="m" style={tdStat}>{w.modules[m] ?? 0}</td>)}
                    <td className="m" style={tdStat}>{w.totalCalls}</td>
                    <td className="m" style={tdStat}>{w.activeUsers}</td>
                    <td className="m" style={tdStat}>{w.hitRate != null ? `${w.hitRate}%` : '—'}</td>
                    <td className="m" style={tdStat}>{w.noResultRate != null ? `${w.noResultRate}%` : '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <div className="hint" style={{ lineHeight: 1.7, margin: '4px 0 20px' }}>
          「如实返回未找到」是设计要的行为，不是故障率。压这个数只有两条路：调低阈值放行低相关答案（把问题藏起来），或真把资料补上。
        </div>

        <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 8 }}>本周返回未找到的问题——按「库里缺什么」处置</div>
        {noResult.data && noResult.data.length === 0 && <div className="hint">本周没有未找到的问题。</div>}
        {(noResult.data ?? []).map((r, i) => (
          <div key={i} style={{ display: 'flex', alignItems: 'baseline', gap: 10, padding: '8px 0', borderBottom: '1px solid var(--line-soft)' }}>
            <span style={{ fontSize: 12.5, flexGrow: 1, minWidth: 0 }}>{r.question}</span>
            <span className="m hint">{r.count} 次</span>
            <span className="m hint">{r.lastAt.slice(5, 10)}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

/** 单序列小柱图：柱贴基线圆角 3px 3px 0 0，柱间 3px 透底色；末柱本周深蓝、其余浅蓝。 */
function MiniBar({ title, values }: { title: string; values: { label: string; v: number }[] }) {
  const max = Math.max(1, ...values.map((x) => x.v));
  return (
    <div className="card" style={{ width: 218, padding: '12px 14px' }}>
      <div style={{ fontSize: 12, fontWeight: 600, marginBottom: 8 }}>{title}</div>
      <div style={{ display: 'flex', alignItems: 'flex-end', gap: 3, height: 64 }}>
        {values.map((x, i) => (
          <div key={i} title={`${x.label}：${x.v}`} style={{
            flexGrow: 1, minWidth: 0, height: `${Math.max(3, (x.v / max) * 100)}%`,
            background: i === values.length - 1 ? '#2f5ccc' : '#b9c9ee',
            borderRadius: '3px 3px 0 0',
          }} />
        ))}
      </div>
      <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 5 }}>
        <span className="m hint" style={{ fontSize: 10 }}>{values[0]?.label}</span>
        <span className="m" style={{ fontSize: 11, fontWeight: 600 }}>{values[values.length - 1]?.v}</span>
      </div>
    </div>
  );
}

/** 单序列小折线：2px 线，点 r=4，末点 r=5.5 白描边并标数值。 */
function MiniLine({ title, values }: { title: string; values: { label: string; v: number | null }[] }) {
  const pts = values.map((x, i) => ({ ...x, i })).filter((x) => x.v != null) as { label: string; v: number; i: number }[];
  const W = 190;
  const H = 56;
  const max = Math.max(1, ...pts.map((p) => p.v));
  const min = Math.min(0, ...pts.map((p) => p.v));
  const px = (i: number) => values.length <= 1 ? W / 2 : (i / (values.length - 1)) * (W - 12) + 6;
  const py = (v: number) => H - 6 - ((v - min) / (max - min || 1)) * (H - 12);
  const last = pts[pts.length - 1];
  return (
    <div className="card" style={{ width: 218, padding: '12px 14px' }}>
      <div style={{ fontSize: 12, fontWeight: 600, marginBottom: 8 }}>{title}</div>
      <svg width={W} height={H + 8} style={{ display: 'block' }}>
        {pts.length > 1 && (
          <polyline fill="none" stroke="#2f5ccc" strokeWidth={2}
            points={pts.map((p) => `${px(p.i)},${py(p.v)}`).join(' ')} />
        )}
        {pts.map((p) => (
          <circle key={p.i} cx={px(p.i)} cy={py(p.v)} r={p === last ? 5.5 : 4}
            fill="#2f5ccc" stroke={p === last ? '#fff' : 'none'} strokeWidth={p === last ? 1.5 : 0}>
            <title>{`${p.label}：${p.v}%`}</title>
          </circle>
        ))}
        {last && <text x={Math.min(px(last.i), W - 26)} y={Math.max(12, py(last.v) - 9)} fontSize={11} fontWeight={600} fill="#2f5ccc">{last.v}%</text>}
      </svg>
      <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 3 }}>
        <span className="m hint" style={{ fontSize: 10 }}>{values[0]?.label}</span>
        <span className="m hint" style={{ fontSize: 10 }}>{values[values.length - 1]?.label}</span>
      </div>
    </div>
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
      <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '10px 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
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
