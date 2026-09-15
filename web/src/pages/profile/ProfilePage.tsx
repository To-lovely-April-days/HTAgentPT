// C3 个人中心：把「做不到的事」明确写出来——口令不可自助修改（决策 2），
// 说清找谁重置、上次重置的时间与经手人。操作记录与管理员离职导出是同一份数据，
// 字段完全一致；被拒的越权请求也在表里——不是留污点，是免得日后争执。
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get, download, ApiError } from '../../lib/api';
import { ClsBadge, Spinner } from '../../components/Common';
import type { Classification } from '../../lib/types';

interface ProfileData {
  username: string; displayName: string; employeeNo: string | null; department: string | null;
  role: { code: string; name: string } | null; company: string;
  classifications: Classification[];
  password: { selfServiceChange: boolean; passwordResetAt: string | null; passwordResetBy: string | null };
  currentTerminal: string | null; lastLoginAt: string | null;
  terminals: { id: string; terminalId: string; name: string | null; lastSeenAt: string | null; lastIp: string | null }[];
}
interface MyAuditRow {
  id: number; at: string; action: string; targetType: string | null; targetId: string | null;
  result: string; detail: string | null;
}

export default function ProfilePage() {
  const profile = useQuery({ queryKey: ['my-profile'], queryFn: () => get<ProfileData>('/api/profile') });
  const audit = useQuery({ queryKey: ['my-audit'], queryFn: () => get<MyAuditRow[]>('/api/profile/my-audit?days=30&limit=100') });
  const [dlError, setDlError] = useState<string | null>(null);

  if (profile.isLoading || !profile.data) return <div style={{ padding: 40 }}><Spinner text="载入…" /></div>;
  const p = profile.data;

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '20px 24px' }}>
      <div style={{ maxWidth: 820, margin: '0 auto' }}>
        <div style={{ fontSize: 15, fontWeight: 600, marginBottom: 14 }}>个人中心</div>

        <div className="card" style={{ padding: '14px 16px', marginBottom: 12 }}>
          <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 10 }}>我的信息</div>
          {[
            ['姓名', p.displayName], ['账号', p.username], ['工号', p.employeeNo ?? '—'],
            ['部门', p.department ?? '—'], ['角色', p.role?.name ?? '—'], ['所属', p.company],
          ].map(([k, v]) => (
            <div key={k} style={{ display: 'flex', gap: 12, padding: '5px 0', fontSize: 12.5 }}>
              <span style={{ width: 70, color: 'var(--ink-3)' }}>{k}</span>
              <span>{v}</span>
            </div>
          ))}
          <div style={{ display: 'flex', gap: 12, padding: '5px 0', fontSize: 12.5, alignItems: 'center' }}>
            <span style={{ width: 70, color: 'var(--ink-3)' }}>可访问密级</span>
            <span style={{ display: 'flex', gap: 5 }}>{p.classifications.map((c) => <ClsBadge key={c} cls={c} />)}</span>
          </div>
        </div>

        {/* 决策 2：反过来写——标明不可自助修改，而不是删掉入口装没这回事 */}
        <div className="card" style={{ padding: '14px 16px', marginBottom: 12 }}>
          <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 6 }}>口令</div>
          <div style={{ fontSize: 12.5, lineHeight: 1.75 }}>
            <span className="pill pill-neutral">不可自助修改</span>
            <span style={{ marginLeft: 8 }}>忘记或需要更换口令，请联系系统管理员重置——重置动作本身会记入操作留痕。</span>
          </div>
          <div className="hint" style={{ marginTop: 6 }}>
            上次重置：{p.password.passwordResetAt ? `${p.password.passwordResetAt.slice(0, 16).replace('T', ' ')} · 经手 ${p.password.passwordResetBy ?? '—'}` : '开户以来未重置过'}
          </div>
        </div>

        <div className="card" style={{ padding: '14px 16px', marginBottom: 12 }}>
          <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 6 }}>登录与终端</div>
          <div className="hint" style={{ marginBottom: 8 }}>
            同一账号同一时刻只允许一处在线；新登录会替代旧会话。上次登录 {p.lastLoginAt ? p.lastLoginAt.slice(0, 16).replace('T', ' ') : '—'}。
          </div>
          {p.terminals.length === 0 && <div className="hint">未绑定终端——绑定后账号只允许从绑定终端接入，绑定找系统管理员。</div>}
          {p.terminals.map((t) => (
            <div key={t.id} style={{ display: 'flex', gap: 10, padding: '5px 0', fontSize: 12.5, alignItems: 'center' }}>
              <span className="m">{t.terminalId}</span>
              <span className="hint">{t.name ?? ''}</span>
              {t.terminalId === p.currentTerminal && <span className="pill pill-accent">当前</span>}
              <div style={{ flexGrow: 1 }} />
              <span className="m hint">{t.lastSeenAt ? t.lastSeenAt.slice(0, 16).replace('T', ' ') : ''}</span>
            </div>
          ))}
        </div>

        <div className="card" style={{ padding: '14px 16px' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 6 }}>
            <span style={{ fontSize: 12.5, fontWeight: 600 }}>我的操作记录（近 30 天）</span>
            <div style={{ flexGrow: 1 }} />
            <button className="gbtn" style={{ height: 26, fontSize: 11.5 }}
              onClick={() => void download('/api/profile/my-audit/export', 'my-operations.csv').catch((e) => setDlError(e instanceof ApiError ? e.message : '导出失败'))}>
              导出 CSV
            </button>
          </div>
          <div className="hint" style={{ marginBottom: 10, lineHeight: 1.65 }}>
            与管理员在离职回收时导出的记录是同一份数据、同一套字段。被拒绝的越权请求也在表里并写明当时申请的是什么——免得日后争执。
          </div>
          {dlError && <div style={{ fontSize: 12, color: 'var(--cls-conf-fg)', marginBottom: 8 }}>{dlError}</div>}
          {(audit.data ?? []).map((a) => (
            <div key={a.id} style={{ display: 'flex', gap: 10, padding: '6px 0', borderBottom: '1px solid var(--line-soft)', fontSize: 12, alignItems: 'baseline' }}>
              <span className="m" style={{ width: 118, flexShrink: 0, color: 'var(--ink-3)' }}>{a.at.slice(0, 16).replace('T', ' ')}</span>
              <span className="m" style={{ width: 160, flexShrink: 0 }}>{a.action}</span>
              <span className="pill" style={{ flexShrink: 0, ...(a.result === 'Denied'
                ? { background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' }
                : { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' }) }}>
                {a.result === 'Denied' ? '拒绝' : '成功'}
              </span>
              <span className="m hint" style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {a.targetType ? `${a.targetType} ${a.targetId ?? ''}` : (a.detail ?? '')}
              </span>
            </div>
          ))}
          {audit.data && audit.data.length === 0 && <div className="hint">近 30 天没有操作记录。</div>}
        </div>
      </div>
    </div>
  );
}
