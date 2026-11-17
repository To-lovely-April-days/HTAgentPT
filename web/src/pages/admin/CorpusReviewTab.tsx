import type React from 'react';
// 语料管理 · 待审核：同事上传的文件，管理员通过（自动排队解析）或写明原因驳回（上传人看得到）。
// 左边列表可勾选批量处理，点一行在右边看详情、下载原文、驳回。
import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { download, get, post, ApiError } from '../../lib/api';
import type { DocRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';
import { ADMIN_REVIEW_API } from './reviewApi';
import type { ReviewApi } from './reviewApi';

const REASONS = ['内容重复', '非公司资料', '含价格/敏感信息', '文件打不开', '资料信息填写不全'];
const fmtSize = (n: number) => (n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);
const when = (iso: string) => iso.slice(5, 16).replace('T', ' ');

export default function CorpusReviewTab({ onChanged, api = ADMIN_REVIEW_API, title = '待审核', sub: subtitle, extra }: {
  onChanged: () => void; api?: ReviewApi; title?: string; sub?: string; extra?: React.ReactNode;
}) {
  const qc = useQueryClient();
  const q = useQuery({
    queryKey: [api.key],
    queryFn: () => get<DocRow[]>(api.list),
    refetchInterval: 30_000,
  });
  const rows = useMemo(() => q.data ?? [], [q.data]);
  const [picked, setSelected] = useState<Set<string>>(new Set());
  const [focusRaw, setFocus] = useState<string | null>(null);
  const [rejecting, setRejecting] = useState(false);
  const [chips, setChips] = useState<Set<string>>(new Set());
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  // 列表变了（别人处理过、刚处理完）：选中和详情里不存在的不算
  const ids = new Set(rows.map((r) => r.id));
  const selected = new Set([...picked].filter((id) => ids.has(id)));
  const focus = focusRaw && ids.has(focusRaw) ? focusRaw : rows[0]?.id ?? null;

  const current = rows.find((r) => r.id === focus) ?? null;
  // 批量条作用于勾选的；没勾选时右侧按钮只作用于当前这一份
  const targets = selected.size > 0 ? [...selected] : current ? [current.id] : [];
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: [api.key] });
    onChanged();
  };

  const approve = async (ids: string[]) => {
    if (!ids.length) return;
    setBusy(true); setError(null); setDone(null);
    try {
      const r = await post<{ approved: number }>(api.approve, { docIds: ids });
      setDone(`已通过 ${r.approved} 份，已排队解析`);
      setSelected(new Set());
      refresh();
    } catch (err) { setError(err instanceof ApiError ? err.message : '审核失败'); }
    finally { setBusy(false); }
  };

  const fullReason = [...chips, reason.trim()].filter(Boolean).join('；');
  const reject = async (ids: string[]) => {
    if (!ids.length || !fullReason) return;
    setBusy(true); setError(null); setDone(null);
    try {
      const r = await post<{ rejected: number }>(api.reject, { docIds: ids, reason: fullReason });
      setDone(`已驳回 ${r.rejected} 份，上传人在「文件管理」里能看到原因`);
      setSelected(new Set()); setRejecting(false); setChips(new Set()); setReason('');
      refresh();
    } catch (err) { setError(err instanceof ApiError ? err.message : '驳回失败'); }
    finally { setBusy(false); }
  };

  const toggle = (id: string) => setSelected((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const allOn = rows.length > 0 && selected.size === rows.length;
  const selSize = rows.filter((r) => selected.has(r.id)).reduce((n, r) => n + r.fileSize, 0);

  return (
    <div className="rv-wrap">
      <div className="rv-main sc">
        <div className="dash-head">
          <div>
            <h1 className="dash-title" style={{ fontSize: 24 }}>{title}</h1>
            <p className="dash-sub">{subtitle ?? '同事上传的文件，通过后自动排队解析；驳回要写原因，上传人在「文件管理」里看得到。'}</p>
          </div>
          {extra}
        </div>

        {selected.size > 0 && (
          <div className="rv-batch">
            <span style={{ fontWeight: 600 }}>已选 {selected.size} 份</span>
            <span style={{ color: '#a6a6a6', fontSize: 13 }}>共 {fmtSize(selSize)}</span>
            <div style={{ flexGrow: 1 }} />
            <button type="button" className="ghost" disabled={busy} onClick={() => setRejecting(true)}>驳回…</button>
            <button type="button" className="solid" disabled={busy} onClick={() => void approve([...selected])}>通过并解析</button>
          </div>
        )}
        {error && <ErrorBox message={error} />}
        {done && <div className="dash-card" style={{ padding: '10px 16px', fontSize: 13, color: '#1c6b45', background: '#e6f2ec', borderColor: '#c2ded1' }}>{done}</div>}

        <section className="dash-card" style={{ padding: 0, overflow: 'hidden' }}>
          {q.isLoading && <div style={{ padding: 24 }}><Spinner text="载入…" /></div>}
          {!q.isLoading && rows.length === 0 && <div className="dash-empty" style={{ padding: '28px 24px' }}>没有待审核的文件。</div>}
          {rows.length > 0 && (
            <div style={{ overflowX: 'auto' }}>
              <table className="us-tbl rv-tbl fm-tbl">
                <thead>
                  <tr>
                    <th style={{ width: 28 }}>
                      <input type="checkbox" aria-label="全选" checked={allOn}
                        onChange={() => setSelected(allOn ? new Set() : new Set(rows.map((r) => r.id)))} />
                    </th>
                    <th style={{ minWidth: 240 }}>文件</th><th>上传人</th><th>资料类型 · 客户/项目</th><th>进入资料库</th><th>提示</th><th style={{ textAlign: 'right' }}>操作</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => (
                    <tr key={r.id} className={focus === r.id ? 'on' : ''} onClick={() => { setFocus(r.id); setRejecting(false); }}>
                      <td onClick={(e) => e.stopPropagation()}>
                        <input type="checkbox" aria-label={`选择 ${r.title}`} checked={selected.has(r.id)} onChange={() => toggle(r.id)} />
                      </td>
                      <td style={{ whiteSpace: 'normal' }}>
                        <span style={{ display: 'flex', flexDirection: 'column' }}>
                          <b style={{ fontWeight: 600 }}>{r.title}</b>
                          <span className="dash-muted" style={{ fontSize: 12 }}>{fmtSize(r.fileSize)} · {when(r.uploadedAt)}</span>
                        </span>
                      </td>
                      <td>
                        <span className="us-who">
                          <i aria-hidden="true">{(r.uploaderName ?? '?').slice(0, 1)}</i>
                          <span style={{ display: 'flex', flexDirection: 'column' }}>
                            <b>{r.uploaderName}</b>
                            <span className="dash-muted" style={{ fontSize: 12 }}>{r.uploaderRole ?? ''}</span>
                          </span>
                        </span>
                      </td>
                      <td>
                        <span style={{ display: 'flex', flexDirection: 'column' }}>
                          <span>{r.docCategory ?? '—'}</span>
                          <span className="dash-muted" style={{ fontSize: 12 }}>{[r.customerName, r.projectNo].filter(Boolean).join(' · ') || '未关联项目'}</span>
                        </span>
                      </td>
                      <td style={{ whiteSpace: 'normal' }}>
                        <span style={{ display: 'flex', gap: 4, flexWrap: 'wrap' }}>
                          {(r.kbNames?.length ? r.kbNames : [r.kbName]).map((k) => <span key={k} className="rv-kb">{k}</span>)}
                        </span>
                      </td>
                      <td>{r.reviewHint ? <span className="rv-hint dup">{r.reviewHint}</span> : <span className="rv-hint new">新资料</span>}</td>
                      <td style={{ textAlign: 'right' }} onClick={(e) => e.stopPropagation()}>
                        <span style={{ display: 'inline-flex', gap: 14 }}>
                          <button type="button" className="fm-act" onClick={() => { setFocus(r.id); setSelected(new Set()); setRejecting(true); }}>驳回</button>
                          <button type="button" className="fm-act" style={{ fontWeight: 600, color: 'var(--ink)' }} disabled={busy} onClick={() => void approve([r.id])}>通过</button>
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
        {api === ADMIN_REVIEW_API && <p className="dash-muted" style={{ margin: 0 }}>「文档列表」里也能看到每份文件是谁传的。管理员自己在后台上传的文件不用审核。</p>}
      </div>

      <aside className="rv-side" aria-label="文件详情">
        {!current ? <div className="dash-empty">点左边一行看详情。</div> : (
          <>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <span className="dash-muted" style={{ fontSize: 12 }}>正在看</span>
              <b style={{ fontSize: 16, fontWeight: 600, lineHeight: 1.4 }}>{current.title}</b>
              <span style={{ fontSize: 13, color: '#5a5a5a' }}>{current.uploaderName}{current.uploaderRole ? ` · ${current.uploaderRole}` : ''} · {when(current.uploadedAt)} 上传</span>
            </div>
            <dl className="rv-dl">
              <dt>文件名</dt><dd style={{ wordBreak: 'break-all' }}>{current.fileName}</dd>
              <dt>资料类型</dt><dd>{current.docCategory ?? '—'}</dd>
              <dt>进入</dt><dd>{(current.kbNames?.length ? current.kbNames : [current.kbName]).join('、')} · 内部密级</dd>
              <dt>客户/项目</dt><dd>{[current.customerName, current.projectNo].filter(Boolean).join(' · ') || '未关联'}</dd>
              <dt>大小</dt><dd>{fmtSize(current.fileSize)}</dd>
              {current.reviewHint && <><dt>提示</dt><dd style={{ color: '#8a5a00' }}>{current.reviewHint}</dd></>}
            </dl>
            <button type="button" className="gbtn" style={{ alignSelf: 'flex-start' }}
              onClick={() => void download(`/api/files/${current.id}`, current.fileName).catch((err) => setError(err instanceof ApiError ? err.message : '下载失败'))}>
              下载原文查看
            </button>

            <div style={{ borderTop: '1px solid var(--line-soft)', paddingTop: 16, display: 'flex', flexDirection: 'column', gap: 10 }}>
              {!rejecting ? (
                <div style={{ display: 'flex', gap: 8 }}>
                  <button type="button" className="gbtn" style={{ height: 36, padding: '0 14px' }} onClick={() => setRejecting(true)}>
                    驳回{selected.size > 0 ? `已选 ${selected.size} 份` : ''}…
                  </button>
                  <button type="button" className="pbtn" style={{ height: 36, padding: '0 14px' }} disabled={busy} onClick={() => void approve(targets)}>
                    通过并解析{selected.size > 0 ? `（${selected.size} 份）` : ''}
                  </button>
                </div>
              ) : (
                <>
                  <b style={{ fontWeight: 600 }}>驳回原因{selected.size > 0 ? `（已选 ${selected.size} 份）` : ''}</b>
                  <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                    {REASONS.map((x) => (
                      <button key={x} type="button" className={'rv-chip' + (chips.has(x) ? ' on' : '')}
                        onClick={() => setChips((c) => { const n = new Set(c); if (n.has(x)) n.delete(x); else n.add(x); return n; })}>{x}</button>
                    ))}
                  </div>
                  <textarea className="rv-reason" placeholder="补充说明（可选），比如：含第三方报价，请走合同归档" value={reason} onChange={(e) => setReason(e.target.value)} />
                  <span className="dash-muted" style={{ fontSize: 12 }}>上传人在「文件管理」里会看到这段原因。</span>
                  <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
                    <button type="button" className="gbtn" style={{ height: 36, padding: '0 14px' }} onClick={() => setRejecting(false)}>取消</button>
                    <button type="button" className="pbtn" style={{ height: 36, padding: '0 14px', background: 'var(--brand)', borderColor: 'var(--brand)' }}
                      disabled={busy || !fullReason} onClick={() => void reject(targets)}>驳回</button>
                  </div>
                </>
              )}
            </div>
          </>
        )}
      </aside>
    </div>
  );
}
