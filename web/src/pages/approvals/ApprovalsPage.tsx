// 审批：部门主管处理本部门（含下级部门）同事提交的文件和新增客户申请；系统管理员看全部，兜底。
// 能看谁的由组织架构决定，服务端按范围过滤——这一页只是把待办摆出来。
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, ApiError } from '../../lib/api';
import type { ApprovalSummary, CustomerRequestRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';
import CorpusReviewTab from '../admin/CorpusReviewTab';
import { HEAD_REVIEW_API } from '../admin/reviewApi';

type Tab = 'files' | 'customers' | 'history';
interface HistoryRow { kind: 'file' | 'customer'; id: string; title: string; review: string; note: string | null; at: string; by: string | null }

export default function ApprovalsPage() {
  const qc = useQueryClient();
  const [tab, setTab] = useState<Tab>('files');
  const summary = useQuery({ queryKey: ['approvals-summary'], queryFn: () => get<ApprovalSummary>('/api/approvals/summary'), refetchInterval: 30_000 });
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['approvals-summary'] });
    void qc.invalidateQueries({ queryKey: ['approvals-customers'] });
    void qc.invalidateQueries({ queryKey: ['approvals-history'] });
  };
  const s = summary.data;
  if (summary.isLoading) return <div style={{ padding: 40 }}><Spinner text="载入…" /></div>;
  if (s && !s.isApprover) {
    return <div style={{ padding: 40 }} className="dash-empty">你目前不是部门主管，没有需要你审批的事项。</div>;
  }
  const tabBtn = (key: Tab, label: string, n?: number) => (
    <button type="button" role="tab" aria-selected={tab === key} className={'fm-tab' + (tab === key ? ' on' : '')} onClick={() => setTab(key)}>
      {label}{n != null && n > 0 && <span style={{ background: 'var(--brand)', color: '#fff' }}>{n}</span>}
    </button>
  );
  return (
    <div style={{ flexGrow: 1, minHeight: 0, display: 'flex', flexDirection: 'column', background: '#FAFAF9' }}>
      <div style={{ padding: '0 40px', borderBottom: '1px solid var(--line)', background: 'var(--panel)', display: 'flex', gap: 24 }} role="tablist">
        {tabBtn('files', '待审文件', s?.files)}
        {tabBtn('customers', '新增客户申请', s?.customers)}
        {tabBtn('history', '我处理过的')}
      </div>
      {tab === 'files' && (
        <CorpusReviewTab api={HEAD_REVIEW_API} onChanged={refresh} title="审批 · 待审文件"
          sub={s?.isAdmin
            ? '你是系统管理员，能看到全部待审文件。部门主管只看得到自己部门（含下级部门）的。'
            : '本部门（含下级部门）同事上传的文件。通过后自动解析入库；驳回要写原因，上传人在「文件管理」里看得到。'} />
      )}
      {tab === 'customers' && <CustomerRequests onChanged={refresh} />}
      {tab === 'history' && <History />}
    </div>
  );
}

function CustomerRequests({ onChanged }: { onChanged: () => void }) {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['approvals-customers'], queryFn: () => get<CustomerRequestRow[]>('/api/approvals/customers') });
  const [edit, setEdit] = useState<{ id: string; name: string } | null>(null);
  const [rejecting, setRejecting] = useState<{ id: string; reason: string } | null>(null);
  const [adding, setAdding] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const run = async (fn: () => Promise<unknown>, ok: string) => {
    setError(null); setDone(null);
    try { await fn(); setDone(ok); setEdit(null); setRejecting(null); void qc.invalidateQueries({ queryKey: ['approvals-customers'] }); onChanged(); }
    catch (err) { setError(err instanceof ApiError ? err.message : '操作失败'); }
  };
  const rows = q.data ?? [];
  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0 }}>
      <div className="dash-in">
        <div className="dash-head">
          <div>
            <h1 className="dash-title" style={{ fontSize: 24 }}>新增客户申请</h1>
            <p className="dash-sub">同事上传时客户名单里没有的客户。通过后进名单，大家都能选；名字不规范可以改了再通过。</p>
          </div>
          <form style={{ display: 'flex', gap: 8 }} onSubmit={(e) => { e.preventDefault(); if (adding.trim()) void run(() => post('/api/approvals/customers', { name: adding.trim() }), `已把「${adding.trim()}」加进客户名单`).then(() => setAdding('')); }}>
            <input className="inp" style={{ width: 220, height: 36 }} placeholder="直接新增客户名称" value={adding} onChange={(e) => setAdding(e.target.value)} />
            <button type="submit" className="pbtn" style={{ height: 36, padding: '0 14px' }} disabled={!adding.trim()}>新增</button>
          </form>
        </div>
        {error && <ErrorBox message={error} />}
        {done && <div className="dash-card" style={{ padding: '10px 16px', fontSize: 13, color: '#1c6b45', background: '#e6f2ec', borderColor: '#c2ded1' }}>{done}</div>}
        {q.isLoading && <Spinner text="载入…" />}
        {!q.isLoading && rows.length === 0 && <div className="dash-card dash-empty" style={{ padding: '24px' }}>没有待处理的客户申请。</div>}
        {rows.map((r) => (
          <section key={r.id} className="dash-card cr-row">
            <span className="cr-ic" aria-hidden="true">客</span>
            <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 4 }}>
              {edit?.id === r.id
                ? <input className="inp" style={{ height: 34, maxWidth: 360 }} value={edit.name} onChange={(e) => setEdit({ id: r.id, name: e.target.value })} autoFocus />
                : <b style={{ fontWeight: 600 }}>{r.name}</b>}
              <span className="dash-muted">
                {r.requesterName}{r.requesterDept ? ` · ${r.requesterDept}` : ''} · {r.requestedAt.slice(5, 16).replace('T', ' ')} 申请{r.reason ? ` · 理由：${r.reason}` : ''}
              </span>
              {r.similar.length > 0 && <span style={{ fontSize: 12, color: '#8a5a00' }}>名单里有相似的：{r.similar.map((x) => `「${x}」`).join('、')}——确认不是同一家再通过</span>}
              {rejecting?.id === r.id && (
                <input className="inp" style={{ height: 34, maxWidth: 480 }} placeholder="驳回原因（申请人能看到），比如：和「某某公司」是同一家，请直接选它"
                  value={rejecting.reason} onChange={(e) => setRejecting({ id: r.id, reason: e.target.value })} autoFocus />
              )}
            </div>
            {rejecting?.id === r.id ? (
              <>
                <button type="button" className="gbtn" onClick={() => setRejecting(null)}>取消</button>
                <button type="button" className="pbtn" style={{ background: 'var(--brand)', borderColor: 'var(--brand)' }} disabled={!rejecting.reason.trim()}
                  onClick={() => void run(() => post(`/api/approvals/customers/${r.id}/reject`, { reason: rejecting.reason.trim() }), '已驳回，申请人能看到原因')}>驳回</button>
              </>
            ) : edit?.id === r.id ? (
              <>
                <button type="button" className="gbtn" onClick={() => setEdit(null)}>取消</button>
                <button type="button" className="pbtn" disabled={edit.name.trim().length < 2}
                  onClick={() => void run(() => post(`/api/approvals/customers/${r.id}/approve`, { name: edit.name.trim() }), `已按「${edit.name.trim()}」加进客户名单`)}>改名后通过</button>
              </>
            ) : (
              <>
                <button type="button" className="fm-act" onClick={() => setEdit({ id: r.id, name: r.name })}>改名后通过</button>
                <button type="button" className="fm-act" onClick={() => setRejecting({ id: r.id, reason: '' })}>驳回</button>
                <button type="button" className="pbtn" style={{ height: 34, padding: '0 14px' }}
                  onClick={() => void run(() => post(`/api/approvals/customers/${r.id}/approve`, { name: null }), `已把「${r.name}」加进客户名单`)}>通过</button>
              </>
            )}
          </section>
        ))}
      </div>
    </div>
  );
}

const REVIEW_LABEL: Record<string, [string, string]> = {
  Approved: ['已通过', '#1c6b45'], Rejected: ['已驳回', 'var(--brand-fg)'], Pending: ['待处理', '#8a5a00'],
};

function History() {
  const q = useQuery({ queryKey: ['approvals-history'], queryFn: () => get<HistoryRow[]>('/api/approvals/history') });
  const rows = q.data ?? [];
  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0 }}>
      <div className="dash-in">
        <h1 className="dash-title" style={{ fontSize: 24 }}>我处理过的</h1>
        {q.isLoading && <Spinner text="载入…" />}
        {!q.isLoading && rows.length === 0 && <div className="dash-card dash-empty" style={{ padding: 24 }}>还没有处理过的事项。</div>}
        {rows.length > 0 && (
          <section className="dash-card" style={{ padding: 0, overflow: 'hidden' }}>
            <table className="us-tbl fm-tbl">
              <thead><tr><th>事项</th><th>类型</th><th>提交人</th><th>结果</th><th>时间</th></tr></thead>
              <tbody>
                {rows.map((r) => {
                  const [label, color] = REVIEW_LABEL[r.review] ?? [r.review, 'var(--ink-2)'];
                  return (
                    <tr key={`${r.kind}${r.id}`}>
                      <td style={{ whiteSpace: 'normal' }}><b style={{ fontWeight: 600 }}>{r.title}</b>{r.note && <div className="dash-muted" style={{ fontSize: 12 }}>原因：{r.note}</div>}</td>
                      <td>{r.kind === 'file' ? '文件' : '新增客户'}</td>
                      <td>{r.by ?? '—'}</td>
                      <td style={{ color, fontWeight: 600 }}>{label}</td>
                      <td style={{ color: 'var(--ink-3)' }}>{r.at ? r.at.slice(5, 16).replace('T', ' ') : '—'}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </section>
        )}
      </div>
    </div>
  );
}
