// B3 案例详情：正文 + 流转状态（local/pending/shared/rejected 四态）。
// 录入即生效——无论流转到哪一步，本公司检索不受影响；提交总部只决定能否进共享库。
import React, { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, ApiError } from '../../lib/api';
import { SYNC_LABEL, SYNC_PILL_STYLE } from '../../lib/types';
import type { CaseDetailData, CaseSyncStatus, SensitiveHit } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';

const STEPS = [
  { name: '本地录入', note: '写入本公司私有库，立即可检索' },
  { name: '提交总部', note: '经与共享库同步相同的通道上传' },
  { name: '总部审核', note: '审核人可查看、修改、通过或驳回' },
  { name: '并入共享库', note: '标注来源公司与录入时间，随下次同步下发' },
];
const STEP_AT: Record<CaseSyncStatus, number> = { Local: 0, Pending: 1, Rejected: 2, Shared: 3 };

export default function CaseDetailPage() {
  const { caseId = '' } = useParams();
  const nav = useNavigate();
  const qc = useQueryClient();
  const [hits, setHits] = useState<SensitiveHit[] | null>(null); // 敏感检测结果（提交前）
  const [actBusy, setActBusy] = useState(false);
  const [actError, setActError] = useState<string | null>(null);

  const q = useQuery({
    queryKey: ['case', caseId],
    queryFn: () => get<CaseDetailData>(`/api/cases/${caseId}`),
    retry: (n, err) => !(err instanceof ApiError && err.status === 404) && n < 2,
  });

  if (q.isLoading) return <div style={{ padding: 40 }}><Spinner text="正在载入案例…" /></div>;
  if (q.isError || !q.data) {
    return (
      <div style={{ padding: 40, maxWidth: 560 }}>
        <ErrorBox message={q.error instanceof ApiError && q.error.status === 404 ? '案例不存在或不在你的可见范围内。' : '载入失败，请稍后重试'} />
        <div style={{ marginTop: 12 }}><Link to="/cases" style={{ fontSize: 13 }}>← 返回案例检索</Link></div>
      </div>
    );
  }
  const c = q.data;
  const refresh = () => { void qc.invalidateQueries({ queryKey: ['case', caseId] }); };

  const beginSubmit = async () => {
    setActBusy(true);
    setActError(null);
    try {
      const found = await post<SensitiveHit[]>(`/api/cases/${caseId}/check-sensitive`);
      if (found.length === 0) {
        await post(`/api/cases/${caseId}/submit`, { acknowledged: false });
        refresh();
      } else {
        setHits(found); // 有命中：先呈现，确认后再提交
      }
    } catch (err) {
      setActError(err instanceof ApiError ? err.message : '提交失败，请稍后重试');
    } finally {
      setActBusy(false);
    }
  };
  const confirmSubmit = async () => {
    setActBusy(true);
    setActError(null);
    try {
      await post(`/api/cases/${caseId}/submit`, { acknowledged: true });
      setHits(null);
      refresh();
    } catch (err) {
      setActError(err instanceof ApiError ? err.message : '提交失败，请稍后重试');
    } finally {
      setActBusy(false);
    }
  };
  const withdraw = async () => {
    setActBusy(true);
    setActError(null);
    try {
      await post(`/api/cases/${caseId}/withdraw`);
      refresh();
    } catch (err) {
      setActError(err instanceof ApiError ? err.message : '撤回失败，请稍后重试');
      if (err instanceof ApiError && err.code === 'REVIEW_ALREADY_DECIDED') refresh();
    } finally {
      setActBusy(false);
    }
  };

  const at = STEP_AT[c.syncStatus];
  const extra = Object.entries(c.extra ?? {});

  return (
    <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minHeight: 0, minWidth: 0 }}>
      <div style={{ height: 58, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 11, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <button className="gbtn" style={{ height: 28, padding: '0 9px' }} onClick={() => nav(-1)} title="返回">←</button>
        <span className="m" style={{ fontSize: 14, fontWeight: 600 }}>{c.caseNo}</span>
        <span className="m pill" style={{ background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' }}>{c.deviceModel}</span>
        {c.alarmCode && <span className="m pill" style={{ background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' }}>{c.alarmCode}</span>}
        <span className="pill" style={{ background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' }}>{c.result}</span>
        <div style={{ flexGrow: 1 }} />
        <span className="pill" style={{ ...SYNC_PILL_STYLE[c.syncStatus], height: 22, padding: '0 9px', fontSize: 11.5 }}>{SYNC_LABEL[c.syncStatus]}</span>
        <button className="gbtn" onClick={() => nav(`/cases/${caseId}/edit`)}>编辑</button>
      </div>

      <div style={{ flexGrow: 1, display: 'flex', minHeight: 0 }}>
        {/* 正文（敏感检测有命中时，命中文本就地高亮——FR-8.3） */}
        <div className="sc" style={{ flexGrow: 1, minWidth: 0, padding: '20px 24px 24px', background: 'var(--panel)' }}>
          <div style={{ maxWidth: 760 }}>
            <Section title="故障现象">{mark(c.phenomenon, hits)}</Section>
            <Section title="原因判断">{mark(c.causeAnalysis, hits)}</Section>
            <Section title="处理步骤">{mark(c.steps, hits)}</Section>
            {c.spareParts && <Section title="所用备件">{mark(c.spareParts, hits)}</Section>}
            {extra.map(([k, v]) => <Section key={k} title={k}>{mark(v, hits)}</Section>)}
            <div style={{ display: 'flex', alignItems: 'center', gap: 14, paddingTop: 6, borderTop: '1px solid var(--line-soft)' }}>
              <span className="m" style={{ fontSize: 11.5, color: 'var(--ink-3)' }}>录入 {c.createdAt.slice(0, 10)}</span>
              <span className="m" style={{ fontSize: 11.5, color: 'var(--ink-3)' }}>更新 {c.updatedAt.slice(0, 10)}</span>
              {c.sourceCompany && <span style={{ fontSize: 11.5, color: 'var(--ink-3)' }}>来源公司 {c.sourceCompany}</span>}
            </div>
          </div>
        </div>

        {/* 流转状态 */}
        <div style={{ width: 420, flexShrink: 0, borderLeft: '1px solid var(--line)', background: 'var(--panel)', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: 18 }}>
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 12 }}>流转状态</div>
            <div style={{ marginBottom: 16 }}>
              {STEPS.map((s, i) => {
                const isRej = c.syncStatus === 'Rejected' && i === 2;
                const isNow = i === at;
                const isDone = i < at || (c.syncStatus === 'Shared' && i <= at);
                const color = isRej ? 'var(--cls-conf-fg)' : isNow ? 'var(--accent)' : isDone ? '#1c6b45' : '#cfd6df';
                return (
                  <div key={s.name} style={{ display: 'flex', gap: 11 }}>
                    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', flexShrink: 0 }}>
                      <div style={{ width: 11, height: 11, borderRadius: '50%', marginTop: 3, background: (isDone || isNow || isRej) ? color : 'var(--panel)', border: `1.5px solid ${color}` }} />
                      {i < STEPS.length - 1 && <div style={{ width: 1.5, flexGrow: 1, minHeight: 16, background: i < at ? '#c2ded1' : 'var(--line-soft)' }} />}
                    </div>
                    <div style={{ paddingBottom: 14, flexGrow: 1, minWidth: 0 }}>
                      <div style={{ fontSize: 12.5, fontWeight: (isNow || isRej) ? 600 : 500, color: (isNow || isRej) ? 'var(--ink)' : isDone ? 'var(--ink)' : 'var(--ink-3)' }}>
                        {isRej && i === 2 ? '总部审核 · 已驳回' : s.name}
                      </div>
                      <div style={{ fontSize: 11, color: 'var(--ink-3)', lineHeight: 1.6, marginTop: 2 }}>{s.note}</div>
                    </div>
                  </div>
                );
              })}
            </div>

            {/* 状态面板 + 主动作 */}
            {hits === null && <StatePanel c={c} busy={actBusy} onSubmit={beginSubmit} onWithdraw={withdraw} onEdit={() => nav(`/cases/${caseId}/edit`)} />}

            {/* 提交前敏感检测结果（FR-8.3） */}
            {hits !== null && (
              <div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 5 }}>
                  <span style={{ fontSize: 13, fontWeight: 600 }}>提交前检测</span>
                  <span className="pill" style={SYNC_PILL_STYLE.Pending}>检出 {hits.length} 处</span>
                </div>
                <div style={{ fontSize: 11.5, color: 'var(--ink-2)', lineHeight: 1.7, marginBottom: 12 }}>
                  案例进共享库后各公司都能看到。以下内容疑似客户信息，请先回到编辑处理，或确认无泄漏后提交。
                </div>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                  {hits.map((h, i) => (
                    <div key={i} style={{ border: '1px solid var(--cls-int-line)', background: 'var(--cls-int-bg)', borderRadius: 5, padding: '10px 12px' }}>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 4 }}>
                        <span className="pill" style={{ background: 'var(--panel)', color: 'var(--cls-int-fg)', border: '1px solid var(--cls-int-line)' }}>{h.kind}</span>
                        <span className="m" style={{ fontSize: 12, fontWeight: 500, color: 'var(--cls-int-fg)' }}>{h.match}</span>
                      </div>
                      <div style={{ fontSize: 11, color: 'var(--cls-int-fg)', lineHeight: 1.6, opacity: .9 }}>位于「{h.field}」。</div>
                    </div>
                  ))}
                </div>
                <div style={{ marginTop: 12, padding: '11px 12px', background: 'var(--bg-soft)', border: '1px solid var(--line-soft)', borderRadius: 5, fontSize: 11.5, lineHeight: 1.7, color: 'var(--ink-2)' }}>
                  检测依据为客户名称词表、项目编号格式、电话与邮箱格式。它只做格式与词表匹配，不理解语义——写成「某高校」仍可能漏检，最终由你确认。
                </div>
                <div style={{ display: 'flex', gap: 8, marginTop: 12 }}>
                  <button className="gbtn" disabled={actBusy} onClick={() => { setHits(null); nav(`/cases/${caseId}/edit`); }}>返回修改</button>
                  <button className="pbtn" style={{ height: 28 }} disabled={actBusy} onClick={() => void confirmSubmit()}>确认无泄漏，提交总部</button>
                  <button className="gbtn" disabled={actBusy} onClick={() => setHits(null)}>取消</button>
                </div>
              </div>
            )}

            {actError && <div style={{ marginTop: 12 }}><ErrorBox message={actError} /></div>}

            <div style={{ marginTop: 16, padding: '12px 13px', background: 'var(--bg-soft)', border: '1px solid var(--line-soft)', borderRadius: 5, fontSize: 11.5, lineHeight: 1.75, color: 'var(--ink-2)' }}>
              无论流转到哪一步，本案例在本公司的检索都不受影响——录入即生效，不依赖审核。提交总部只决定它能不能进集团共享库、被其他公司看到。
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function StatePanel({ c, busy, onSubmit, onWithdraw, onEdit }: {
  c: CaseDetailData; busy: boolean;
  onSubmit: () => void; onWithdraw: () => void; onEdit: () => void;
}) {
  const P: Record<CaseSyncStatus, { style: React.CSSProperties; title: string; body: string }> = {
    Local: {
      style: { background: 'var(--accent-bg)', border: '1px solid var(--accent-line)', color: 'var(--ink)' },
      title: '尚未提交总部',
      body: '本案例目前只在本公司可见。提交后经审核可并入集团共享库，供其他公司检索——这是共享库长期增值的来源。提交前会先做敏感信息检测。',
    },
    Pending: {
      style: { background: 'var(--cls-int-bg)', border: '1px solid var(--cls-int-line)', color: 'var(--cls-int-fg)' },
      title: `已于 ${c.updatedAt.slice(0, 10)} 提交，待总部审核`,
      body: '审核期间本公司检索不受影响。审核人可以修改后再通过，通过或驳回都会回传结果。',
    },
    Shared: {
      style: { background: '#e6f2ec', border: '1px solid #c2ded1', color: '#1c6b45' },
      title: '已并入集团共享库',
      body: '共享库中标注来源公司与录入时间，随下次同步下发各公司。本地这份是正本，共享库那份是副本，两者独立；修改本地后如需更新共享库，再次提交即可。',
    },
    Rejected: {
      style: { background: 'var(--cls-conf-bg)', border: '1px solid var(--cls-conf-line)', color: 'var(--cls-conf-fg)' },
      title: '被总部驳回',
      body: c.rejectReason ? `驳回原因：${c.rejectReason}` : '总部未附具体原因。',
    },
  };
  const p = P[c.syncStatus];
  return (
    <div style={{ padding: '13px 14px', borderRadius: 5, ...p.style }}>
      <div style={{ fontSize: 12.5, fontWeight: 600, lineHeight: 1.5, marginBottom: 6 }}>{p.title}</div>
      <div style={{ fontSize: 11.5, lineHeight: 1.75, marginBottom: 11, opacity: .92 }}>{p.body}</div>
      <div style={{ display: 'flex', gap: 6 }}>
        {c.syncStatus === 'Local' && <button className="pbtn" style={{ height: 26, fontSize: 12 }} disabled={busy} onClick={onSubmit}>提交总部</button>}
        {c.syncStatus === 'Pending' && (
          <button disabled={busy} onClick={onWithdraw}
            style={{ height: 26, padding: '0 11px', borderRadius: 4, fontSize: 12, fontWeight: 500, color: 'var(--cls-int-fg)', background: 'var(--panel)', border: '1px solid var(--cls-int-line)', cursor: 'pointer', fontFamily: 'var(--font)' }}>
            撤回提交
          </button>
        )}
        {c.syncStatus === 'Rejected' && (
          <button disabled={busy} onClick={onEdit}
            style={{ height: 26, padding: '0 11px', borderRadius: 4, fontSize: 12, fontWeight: 500, color: '#fff', background: 'var(--cls-conf-fg)', border: 'none', cursor: 'pointer', fontFamily: 'var(--font)' }}>
            修改后重新提交
          </button>
        )}
      </div>
    </div>
  );
}

/** 把敏感检测命中的文本片段就地高亮（琥珀底 + 下划线，与 B4 原型一致）。 */
function mark(text: string, hits: SensitiveHit[] | null): React.ReactNode {
  if (!hits || hits.length === 0) return text;
  const targets = [...new Set(hits.map((h) => h.match))].filter(Boolean);
  if (targets.length === 0) return text;
  const re = new RegExp(targets.map((t) => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|'), 'g');
  const out: React.ReactNode[] = [];
  let last = 0;
  for (const m of text.matchAll(re)) {
    if (m.index! > last) out.push(text.slice(last, m.index));
    out.push(
      <span key={m.index} style={{ background: 'var(--cls-int-bg)', borderBottom: '2px solid var(--cls-int-fg)', padding: '0 1px' }}>
        {m[0]}
      </span>,
    );
    last = m.index! + m[0].length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <>
      <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--ink-2)', margin: '0 0 6px' }}>{title}</div>
      <div style={{ fontSize: 13.5, lineHeight: 1.85, whiteSpace: 'pre-wrap', marginBottom: 18 }}>{children}</div>
    </>
  );
}
