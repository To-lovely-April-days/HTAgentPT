// B5 工单列表 + B6 工单详情。FR-8.8 本期只做最简流转：受理、指派、更新状态、追加记录，
// 不含派工调度与备件管理——界面写明这条边界，免得后续被当成缺失功能补上。
// 追加记录分两处：内部记录（默认）与「同时发给客户」的对客留言（K5 只透出后者）。
import React, { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, ApiError } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { TICKET_LABEL } from '../../lib/types';
import type { TicketRow, TicketStatus } from '../../lib/types';
import { Spinner } from '../../components/Common';

const STATUS_PILL: Record<TicketStatus, React.CSSProperties> = {
  Submitted: { background: 'var(--cls-int-bg)', color: 'var(--cls-int-fg)', border: '1px solid var(--cls-int-line)' },
  Assigned: { background: 'var(--accent-bg)', color: 'var(--accent)', border: '1px solid var(--accent-line)' },
  InProgress: { background: 'var(--accent-bg)', color: 'var(--accent)', border: '1px solid var(--accent-line)' },
  Resolved: { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' },
  Closed: { background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' },
};

export default function TicketsPage() {
  const [statusFilter, setStatusFilter] = useState('');
  const [openId, setOpenId] = useState<string | null>(null);
  const q = useQuery({
    queryKey: ['tickets', statusFilter],
    queryFn: () => get<TicketRow[]>(`/api/tickets${statusFilter ? `?status=${statusFilter}` : ''}`),
  });
  const open = (q.data ?? []).find((t) => t.id === openId) ?? null;

  return (
    <div style={{ flexGrow: 1, display: 'flex', minHeight: 0, minWidth: 0 }}>
      <div style={{ width: open ? 440 : undefined, flexGrow: open ? 0 : 1, flexShrink: 0, display: 'flex', flexDirection: 'column', minHeight: 0, background: 'var(--panel)', borderRight: open ? '1px solid var(--line)' : 'none' }}>
        <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '12px 16px', borderBottom: '1px solid var(--line)' }}>
          <span style={{ fontSize: 13, fontWeight: 600 }}>报修工单</span>
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}
            style={{ height: 26, padding: '0 8px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12, fontFamily: 'var(--font)' }}>
            <option value="">全部状态</option>
            {Object.entries(TICKET_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
          <div style={{ flexGrow: 1 }} />
          <span className="hint">{(q.data ?? []).length} 单</span>
        </div>
        <div style={{ flexShrink: 0, padding: '9px 16px', background: 'var(--bg-soft)', borderBottom: '1px solid var(--line)' }}>
          <span className="hint">本期为最简流转：受理、指派、更新状态、追加记录——不含派工调度与备件管理。</span>
        </div>
        <div className="sc" style={{ flexGrow: 1, minHeight: 0 }}>
          {q.isLoading && <div style={{ padding: 24 }}><Spinner text="载入工单…" /></div>}
          {(q.data ?? []).map((t) => (
            <div key={t.id} onClick={() => setOpenId(t.id)}
              style={{ padding: '11px 16px', borderBottom: '1px solid var(--line-soft)', cursor: 'pointer', background: openId === t.id ? 'var(--accent-bg)' : undefined }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 3 }}>
                <span className="m" style={{ fontSize: 12.5, fontWeight: 600 }}>{t.ticketNo}</span>
                <span className="pill" style={STATUS_PILL[t.status]}>{TICKET_LABEL[t.status]}</span>
                <div style={{ flexGrow: 1 }} />
                <span className="m" style={{ fontSize: 11, color: 'var(--ink-3)' }}>{t.updatedAt.slice(5, 16).replace('T', ' ')}</span>
              </div>
              <div style={{ fontSize: 12.5, lineHeight: 1.6, display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>{t.description}</div>
              <div className="hint" style={{ marginTop: 3 }}>
                {t.deviceNo} · 客户 {t.customerNo}{t.assigneeName ? ` · 处理人 ${t.assigneeName}` : ' · 未指派'}
              </div>
            </div>
          ))}
          {q.data && q.data.length === 0 && <div className="hint" style={{ padding: 20 }}>没有匹配的工单。</div>}
        </div>
      </div>

      {open && <TicketDetail ticket={open} onClose={() => setOpenId(null)} />}
    </div>
  );
}

function TicketDetail({ ticket: t, onClose }: { ticket: TicketRow; onClose: () => void }) {
  const qc = useQueryClient();
  const { profile } = useAuth();
  const [status, setStatus] = useState<TicketStatus>(t.status);
  const [note, setNote] = useState('');
  const [forCustomer, setForCustomer] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = () => void qc.invalidateQueries({ queryKey: ['tickets'] });

  const assignSelf = async () => {
    if (!profile) return;
    setBusy(true);
    setError(null);
    try {
      await post(`/api/tickets/${t.id}/assign`, { assigneeId: profile.id });
      refresh();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '指派失败');
    } finally {
      setBusy(false);
    }
  };

  const update = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await post(`/api/tickets/${t.id}/status`, { status, note: note.trim() || null, forCustomer });
      setNote('');
      setForCustomer(false);
      refresh();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '更新失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0, background: 'var(--panel)' }}>
      <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 10, padding: '12px 18px', borderBottom: '1px solid var(--line)' }}>
        <span className="m" style={{ fontSize: 14, fontWeight: 600 }}>{t.ticketNo}</span>
        <span className="pill" style={STATUS_PILL[t.status]}>{TICKET_LABEL[t.status]}</span>
        <span className="hint">{t.deviceNo} · 客户 {t.customerNo} · 联系 {t.contact}</span>
        <div style={{ flexGrow: 1 }} />
        {!t.assigneeId && <button className="gbtn" disabled={busy} onClick={() => void assignSelf()}>受理（指派给我）</button>}
        {t.assigneeName && <span className="hint">处理人 {t.assigneeName}</span>}
        <button className="gbtn" onClick={onClose}>收起</button>
      </div>

      <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '16px 18px' }}>
        <div style={{ maxWidth: 720 }}>
          <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--ink-2)', marginBottom: 5 }}>现象描述</div>
          <div style={{ fontSize: 13, lineHeight: 1.8, whiteSpace: 'pre-wrap', marginBottom: 18 }}>{t.description}</div>

          <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--ink-2)', marginBottom: 8 }}>处理记录</div>
          {t.trail.map((e, i) => (
            <div key={i} style={{ display: 'flex', gap: 10, padding: '7px 0', borderBottom: '1px solid var(--line-soft)' }}>
              <span className="m" style={{ fontSize: 11.5, color: 'var(--ink-3)', flexShrink: 0, width: 118 }}>{e.at.slice(0, 16).replace('T', ' ')}</span>
              <span style={{ fontSize: 12, flexShrink: 0, width: 64, color: 'var(--ink-2)' }}>{TICKET_LABEL[e.status as TicketStatus] ?? e.status}</span>
              <span style={{ fontSize: 12.5, lineHeight: 1.6, flexGrow: 1, minWidth: 0 }}>
                {e.note ?? '—'}
                {e.note && (
                  <span className="pill" style={{ marginLeft: 8, ...(e.forCustomer
                    ? { background: 'var(--cls-pub-bg)', color: 'var(--cls-pub-fg)', border: '1px solid var(--cls-pub-line)' }
                    : { background: '#eef1f5', color: 'var(--ink-3)', border: '1px solid #dde3ea' }) }}>
                    {e.forCustomer ? '客户可见' : '仅内部'}
                  </span>
                )}
              </span>
              <span className="hint" style={{ flexShrink: 0 }}>{e.by}</span>
            </div>
          ))}

          <form onSubmit={update} style={{ marginTop: 18, padding: '14px 15px', border: '1px solid var(--line)', borderRadius: 6, background: 'var(--bg-soft)' }}>
            <div style={{ display: 'flex', gap: 10, alignItems: 'center', marginBottom: 10 }}>
              <span style={{ fontSize: 12, fontWeight: 600 }}>更新状态</span>
              <select value={status} onChange={(e) => setStatus(e.target.value as TicketStatus)}
                style={{ height: 28, padding: '0 9px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' }}>
                {Object.entries(TICKET_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </select>
            </div>
            <textarea value={note} onChange={(e) => setNote(e.target.value)}
              placeholder="追加记录。技术判断与备件信息留内部即可；勾选「同时发给客户」的内容会原样出现在客户的进度页上。"
              style={{ width: '100%', minHeight: 66, padding: '8px 10px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, lineHeight: 1.7, fontFamily: 'var(--font)', resize: 'vertical' }} />
            <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginTop: 9 }}>
              <label style={{ display: 'flex', alignItems: 'center', gap: 5, fontSize: 12.5, cursor: 'pointer' }}>
                <input type="checkbox" checked={forCustomer} onChange={(e) => setForCustomer(e.target.checked)} />
                同时发给客户
              </label>
              <div style={{ flexGrow: 1 }} />
              {error && <span style={{ fontSize: 12, color: 'var(--cls-conf-fg)' }}>{error}</span>}
              <button type="submit" className="pbtn" style={{ height: 28, padding: '0 14px' }} disabled={busy}>更新</button>
            </div>
          </form>
        </div>
      </div>
    </div>
  );
}
