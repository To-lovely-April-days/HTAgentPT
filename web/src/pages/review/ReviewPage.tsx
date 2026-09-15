// H1 待审列表 + H2 审核详情 + H4 驳回 + H3 同型对照。
// 审核台不是管理后台的一个视角，是总部节点——各公司私有库不存在访问通道，
// 不是权限没给，是架构上就没有这条路（3.4）。
// 查重：相关度是机器给的、重复是人判的——只有逐项对照，没有「判为重复」快捷按钮。
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, ApiError } from '../../lib/api';
import type { CaseDetailData, CaseRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';

export default function ReviewPage() {
  const qc = useQueryClient();
  const [openId, setOpenId] = useState<string | null>(null);
  const pending = useQuery({ queryKey: ['review-pending'], queryFn: () => get<CaseRow[]>('/api/review/cases') });
  const rows = pending.data ?? [];
  const active = openId ?? rows[0]?.id ?? null;

  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['review-pending'] });
    void qc.invalidateQueries({ queryKey: ['review-case'] });
  };

  return (
    <div style={{ flexGrow: 1, display: 'flex', minHeight: 0, minWidth: 0 }}>
      {/* H1 待审列表 */}
      <div style={{ width: 320, flexShrink: 0, display: 'flex', flexDirection: 'column', minHeight: 0, background: 'var(--panel)', borderRight: '1px solid var(--line)' }}>
        <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '12px 16px', borderBottom: '1px solid var(--line)' }}>
          <span style={{ fontSize: 13, fontWeight: 600 }}>待审核案例</span>
          <div style={{ flexGrow: 1 }} />
          <span className="hint">{rows.length} 条</span>
        </div>
        <div className="sc" style={{ flexGrow: 1, minHeight: 0 }}>
          {pending.isLoading && <div style={{ padding: 20 }}><Spinner text="载入…" /></div>}
          {rows.map((c) => (
            <div key={c.id} onClick={() => setOpenId(c.id)}
              style={{ padding: '11px 16px', borderBottom: '1px solid var(--line-soft)', cursor: 'pointer', background: active === c.id ? 'var(--accent-bg)' : undefined }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 7, marginBottom: 3 }}>
                <span className="m" style={{ fontSize: 12, fontWeight: 600 }}>{c.caseNo}</span>
                <span className="m pill pill-neutral">{c.deviceModel}</span>
                {c.alarmCode && <span className="m pill" style={{ background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' }}>{c.alarmCode}</span>}
              </div>
              <div style={{ fontSize: 12.5, lineHeight: 1.55, display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>{c.phenomenon}</div>
              <div className="hint" style={{ marginTop: 3 }}>来源 {c.sourceCompany ?? '—'} · {c.createdAt.slice(0, 10)}</div>
            </div>
          ))}
          {!pending.isLoading && rows.length === 0 && <div className="hint" style={{ padding: 20 }}>没有待审核的案例。</div>}
        </div>
        <div style={{ flexShrink: 0, padding: '11px 16px', borderTop: '1px solid var(--line)', background: 'var(--bg-soft)' }}>
          <span className="hint" style={{ lineHeight: 1.65 }}>
            本节点只看得到各公司提交上来的案例与集团共享库。各公司私有库不存在访问通道——不是权限没给，是架构上就没有这条路。
          </span>
        </div>
      </div>

      {/* H2 审核详情 */}
      {active ? <ReviewDetail key={active} caseId={active} onDone={refresh} /> : (
        <div style={{ flexGrow: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          <span className="hint">左侧选择一条待审案例。</span>
        </div>
      )}
    </div>
  );
}

function ReviewDetail({ caseId, onDone }: { caseId: string; onDone: () => void }) {
  const [mode, setMode] = useState<'view' | 'reject' | 'edit'>('view');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [edit, setEdit] = useState<Record<string, string>>({});

  const q = useQuery({ queryKey: ['review-case', caseId], queryFn: () => get<CaseDetailData>(`/api/review/cases/${caseId}`) });
  const similar = useQuery({ queryKey: ['review-similar', caseId], queryFn: () => get<CaseRow[]>(`/api/review/cases/${caseId}/similar`) });

  if (q.isLoading || !q.data) return <div style={{ flexGrow: 1, padding: 40 }}><Spinner text="载入案例…" /></div>;
  const c = q.data;

  const approve = async (edited: boolean) => {
    setBusy(true);
    setError(null);
    try {
      await post(`/api/review/cases/${caseId}/approve`, {
        edited: edited ? {
          deviceModel: edit.deviceModel ?? c.deviceModel,
          alarmCode: (edit.alarmCode ?? c.alarmCode) || null,
          phenomenon: edit.phenomenon ?? c.phenomenon,
          causeAnalysis: edit.causeAnalysis ?? c.causeAnalysis,
          steps: edit.steps ?? c.steps,
          spareParts: (edit.spareParts ?? c.spareParts) || null,
          result: edit.result ?? c.result,
          extra: c.extra,
        } : null,
      });
      onDone();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '操作失败');
    } finally {
      setBusy(false);
    }
  };

  const reject = async () => {
    setBusy(true);
    setError(null);
    try {
      await post(`/api/review/cases/${caseId}/reject`, { reason: reason.trim() });
      setMode('view');
      setReason('');
      onDone();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '驳回失败');
    } finally {
      setBusy(false);
    }
  };

  const fields: [string, keyof CaseDetailData & string, boolean][] = [
    ['故障现象', 'phenomenon', true], ['原因判断', 'causeAnalysis', true],
    ['处理步骤', 'steps', true], ['所用备件', 'spareParts', false], ['处理结果', 'result', false],
  ];

  return (
    <div style={{ flexGrow: 1, display: 'flex', minWidth: 0, minHeight: 0 }}>
      <div className="sc" style={{ flexGrow: 1, minWidth: 0, padding: '18px 22px', background: 'var(--panel)' }}>
        <div style={{ maxWidth: 720 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 9, marginBottom: 4 }}>
            <span className="m" style={{ fontSize: 14, fontWeight: 600 }}>{c.caseNo}</span>
            <span className="m pill pill-neutral">{c.deviceModel}</span>
            {c.alarmCode && <span className="m pill" style={{ background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' }}>{c.alarmCode}</span>}
            <span className="pill" style={{ background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' }}>{c.result}</span>
          </div>
          <div className="hint" style={{ marginBottom: 16 }}>来源公司 {c.sourceCompany ?? '—'} · 录入 {c.createdAt.slice(0, 10)}</div>

          {mode !== 'edit' && fields.map(([label, key]) => {
            const v = c[key] as string | null;
            return v ? (
              <div key={key} style={{ marginBottom: 16 }}>
                <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--ink-2)', marginBottom: 4 }}>{label}</div>
                <div style={{ fontSize: 13, lineHeight: 1.8, whiteSpace: 'pre-wrap' }}>{v}</div>
              </div>
            ) : null;
          })}

          {mode === 'edit' && (
            <div style={{ marginBottom: 16 }}>
              <div className="hint" style={{ marginBottom: 10 }}>修改后通过：改动会以修改后的版本入共享库，原提交方会在状态回传里看到已通过。</div>
              {fields.map(([label, key]) => (
                <div key={key} style={{ marginBottom: 12 }}>
                  <div style={{ fontSize: 11.5, color: 'var(--ink-2)', marginBottom: 4 }}>{label}</div>
                  <textarea
                    value={edit[key] ?? (c[key] as string | null) ?? ''}
                    onChange={(e) => setEdit((s) => ({ ...s, [key]: e.target.value }))}
                    style={{ width: '100%', minHeight: key === 'steps' ? 96 : 56, padding: '8px 10px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, lineHeight: 1.7, fontFamily: 'var(--font)', resize: 'vertical' }}
                  />
                </div>
              ))}
            </div>
          )}

          {error && <div style={{ marginBottom: 12 }}><ErrorBox message={error} /></div>}

          {/* H4 驳回面板：原因必填，原样回传提交人 */}
          {mode === 'reject' && (
            <div style={{ padding: '13px 14px', borderRadius: 5, background: 'var(--cls-conf-bg)', border: '1px solid var(--cls-conf-line)', marginBottom: 14 }}>
              <div style={{ fontSize: 12.5, fontWeight: 600, color: 'var(--cls-conf-fg)', marginBottom: 6 }}>驳回原因（必填，会原样回传给提交人）</div>
              <textarea value={reason} onChange={(e) => setReason(e.target.value)} autoFocus
                placeholder="写清楚哪里不对、要怎么改——提交人照着这段话修改后重新提交。"
                style={{ width: '100%', minHeight: 72, padding: '8px 10px', border: '1px solid var(--cls-conf-line)', borderRadius: 4, fontSize: 12.5, lineHeight: 1.7, fontFamily: 'var(--font)', resize: 'vertical' }} />
              <div style={{ display: 'flex', gap: 8, marginTop: 9 }}>
                <button disabled={busy || !reason.trim()} onClick={() => void reject()}
                  style={{ height: 28, padding: '0 13px', borderRadius: 4, fontSize: 12.5, fontWeight: 500, color: '#fff', background: 'var(--cls-conf-fg)', border: 'none', cursor: 'pointer', fontFamily: 'var(--font)' }}>
                  确认驳回
                </button>
                <button className="gbtn" onClick={() => setMode('view')}>取消</button>
              </div>
            </div>
          )}

          <div style={{ display: 'flex', gap: 8, paddingTop: 6, borderTop: '1px solid var(--line-soft)' }}>
            {mode === 'view' && (
              <>
                <button className="pbtn" style={{ height: 30, padding: '0 16px' }} disabled={busy} onClick={() => void approve(false)}>通过，入共享库</button>
                <button className="gbtn" style={{ height: 30 }} onClick={() => setMode('edit')}>修改后通过</button>
                <button className="gbtn" style={{ height: 30 }} onClick={() => setMode('reject')}>驳回</button>
              </>
            )}
            {mode === 'edit' && (
              <>
                <button className="pbtn" style={{ height: 30, padding: '0 16px' }} disabled={busy} onClick={() => void approve(true)}>以修改后版本通过</button>
                <button className="gbtn" style={{ height: 30 }} onClick={() => { setMode('view'); setEdit({}); }}>取消修改</button>
              </>
            )}
          </div>
          <div className="hint" style={{ marginTop: 10, lineHeight: 1.65 }}>
            通过后渲染入集团共享库并标注来源公司与录入时间，随下次同步下发各公司；通过或驳回都会回传提交方。
          </div>
        </div>
      </div>

      {/* H3 同型对照 */}
      <div style={{ width: 380, flexShrink: 0, borderLeft: '1px solid var(--line)', background: 'var(--panel)', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '16px 16px 18px' }}>
          <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 5 }}>同型号已入共享库的案例</div>
          <div className="hint" style={{ marginBottom: 11, lineHeight: 1.7 }}>
            查重靠逐项对照，不靠相关度——同型号同代码的现象本来就写得像。成因与处理步骤不同的两条都留；判为重复时把对照写进驳回原因回传。
          </div>
          {similar.isLoading && <Spinner text="检索中…" />}
          {similar.data && similar.data.length === 0 && <div className="hint">共享库中没有同型号案例——不存在重复可能。</div>}
          {(similar.data ?? []).map((s) => (
            <div key={s.id} className="card" style={{ padding: '10px 12px', marginBottom: 8 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 7, marginBottom: 4 }}>
                <span className="m" style={{ fontSize: 11.5, fontWeight: 600 }}>{s.caseNo}</span>
                {s.alarmCode && <span className="m pill" style={{ background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' }}>{s.alarmCode}</span>}
                <span className="hint" style={{ marginLeft: 'auto' }}>{s.sourceCompany ?? '—'}</span>
              </div>
              <CompareRow label="报警代码" a={c.alarmCode ?? '—'} b={s.alarmCode ?? '—'} />
              <div style={{ fontSize: 12, lineHeight: 1.65, margin: '4px 0' }}>{s.phenomenon}</div>
              <div style={{ fontSize: 11.5, lineHeight: 1.6, color: 'var(--ink-2)', display: '-webkit-box', WebkitLineClamp: 3, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>{s.result}</div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

function CompareRow({ label, a, b }: { label: string; a: string; b: string }) {
  const same = a === b;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 11 }}>
      <span style={{ color: 'var(--ink-3)' }}>{label}</span>
      <span className="pill" style={same
        ? { background: 'var(--cls-int-bg)', color: 'var(--cls-int-fg)', border: '1px solid var(--cls-int-line)' }
        : { background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' }}>
        {same ? '相同' : '不同'}
      </span>
    </div>
  );
}
