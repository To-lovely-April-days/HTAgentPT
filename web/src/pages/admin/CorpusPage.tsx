import { useNavigate } from 'react-router-dom';
// E1 语料管理（文档列表）+ E3 解析任务队列。
// FR-1.2 上传后不自动解析（解析占算力，与在线问答共用资源）；
// FR-1.7 失败必须给出具体原因；10.3 「等待」与「失败」是两回事，分开呈现。
import React, { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, ApiError } from '../../lib/api';
import { CLS_PILL, CLS_LABEL, PARSE_LABEL } from '../../lib/types';
import type { DocRow, KbRow, ParseJobRow, ParseStatus } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';
import UploadModal from './UploadModal';

const STATUS_PILL: Record<ParseStatus, React.CSSProperties> = {
  Parsed: { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' },
  Parsing: { background: 'var(--accent-bg)', color: 'var(--accent)', border: '1px solid var(--accent-line)' },
  Queued: { background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' },
  NotParsed: { background: 'var(--panel)', color: 'var(--cls-int-fg)', border: '1px dashed var(--cls-int-line)' },
  Failed: { background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' },
};
const JOB_LABEL: Record<string, string> = {
  Queued: '排队中', Running: '解析中', Waiting: '等待中', Succeeded: '已完成', Failed: '失败', Cancelled: '已取消',
};
const JOB_PILL: Record<string, React.CSSProperties> = {
  Queued: STATUS_PILL.Queued, Running: STATUS_PILL.Parsing,
  Waiting: { background: 'var(--cls-int-bg)', color: 'var(--cls-int-fg)', border: '1px solid var(--cls-int-line)' },
  Succeeded: STATUS_PILL.Parsed, Failed: STATUS_PILL.Failed, Cancelled: STATUS_PILL.Queued,
};
const KIND_LABEL: Record<string, string> = { Parse: '解析', Reparse: '重新解析', EmbedOnly: '重算向量', EmbedDoc: '整篇重算向量' };

export default function CorpusPage() {
  const qc = useQueryClient();
  const nav = useNavigate();
  const [tab, setTab] = useState<'docs' | 'queue'>('docs');
  const [kbId, setKbId] = useState('');
  const [status, setStatus] = useState('');
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [uploading, setUploading] = useState(false);
  const [actError, setActError] = useState<string | null>(null);

  const kbs = useQuery({ queryKey: ['kbs'], queryFn: () => get<KbRow[]>('/api/kbs'), staleTime: 60_000 });
  const docs = useQuery({
    queryKey: ['docs', kbId, status, search],
    queryFn: () => {
      const p = new URLSearchParams();
      if (kbId) p.set('kbId', kbId);
      if (status) p.set('status', status);
      if (search.trim()) p.set('search', search.trim());
      return get<DocRow[]>(`/api/documents${p.size ? `?${p}` : ''}`);
    },
  });
  const queue = useQuery({
    queryKey: ['parse-queue'],
    queryFn: () => get<ParseJobRow[]>('/api/documents/queue'),
    refetchInterval: tab === 'queue' ? 4000 : false,
  });

  const rows = docs.data ?? [];
  const stats = useMemo(() => ({
    total: rows.length,
    parsed: rows.filter((r) => r.parseStatus === 'Parsed').length,
    waiting: rows.filter((r) => r.parseStatus === 'NotParsed').length,
    failed: rows.filter((r) => r.parseStatus === 'Failed').length,
  }), [rows]);
  const activeJobs = (queue.data ?? []).filter((j) => j.status === 'Queued' || j.status === 'Running' || j.status === 'Waiting').length;

  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['docs'] });
    void qc.invalidateQueries({ queryKey: ['parse-queue'] });
    void qc.invalidateQueries({ queryKey: ['kbs'] });
  };

  const toggle = (id: string) => setSelected((s) => {
    const n = new Set(s);
    if (n.has(id)) n.delete(id);
    else n.add(id);
    return n;
  });

  const submitParse = async (ids: string[]) => {
    setActError(null);
    try {
      await post('/api/documents/parse', { docIds: ids });
      setSelected(new Set());
      refresh();
    } catch (err) {
      setActError(err instanceof ApiError ? err.message : '提交解析失败');
    }
  };
  const reparse = async (id: string) => {
    setActError(null);
    try {
      await post(`/api/documents/${id}/reparse`, { newStrategy: null });
      refresh();
    } catch (err) {
      setActError(err instanceof ApiError ? err.message : '重新解析失败');
    }
  };

  const fmtSize = (n: number) => (n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);
  const fi: React.CSSProperties = { height: 28, padding: '0 9px', background: 'var(--panel)', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };
  const tabStyle = (on: boolean): React.CSSProperties => ({
    height: 40, padding: '0 13px', display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: 12.5, cursor: 'pointer',
    borderBottom: on ? '2px solid var(--accent)' : '2px solid transparent',
    color: on ? 'var(--accent)' : 'var(--ink-2)', fontWeight: on ? 600 : 400,
  });

  return (
    <>
      <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 2, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <span style={tabStyle(tab === 'docs')} onClick={() => setTab('docs')}>文档列表</span>
        <span style={tabStyle(tab === 'queue')} onClick={() => setTab('queue')}>
          解析队列{activeJobs > 0 && <span className="m" style={{ color: 'var(--cls-int-fg)' }}>{activeJobs}</span>}
        </span>
      </div>

      {tab === 'docs' && (
        <>
          <div style={{ height: 48, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
            <select style={fi} value={kbId} onChange={(e) => { setKbId(e.target.value); setSelected(new Set()); }}>
              <option value="">知识库：全部</option>
              {(kbs.data ?? []).map((k) => <option key={k.id} value={k.id}>{k.name}</option>)}
            </select>
            <select style={fi} value={status} onChange={(e) => setStatus(e.target.value)}>
              <option value="">状态：全部</option>
              {Object.entries(PARSE_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </select>
            <input style={{ ...fi, width: 200 }} placeholder="搜索文档名" value={search} onChange={(e) => setSearch(e.target.value)} />
            <div style={{ flexGrow: 1 }} />
            {selected.size > 0 && <span className="hint">已选 <span className="m" style={{ color: 'var(--ink)', fontWeight: 500 }}>{selected.size}</span> 份</span>}
            <button className="gbtn" disabled={selected.size === 0} onClick={() => void submitParse([...selected])}>提交解析</button>
            <button className="pbtn" style={{ height: 28, padding: '0 13px' }} onClick={() => setUploading(true)}>上传文档</button>
          </div>

          <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 10, padding: '10px 18px', background: 'var(--bg-soft)', borderBottom: '1px solid var(--line)' }}>
            <span style={{ fontSize: 11.5, color: 'var(--ink-2)', lineHeight: 1.6 }}>
              上传后不会自动解析。挑好要解析的文档再提交——解析占算力，与在线问答共用同一批资源。
            </span>
          </div>

          {actError && <div style={{ padding: '10px 18px' }}><ErrorBox message={actError} /></div>}

          <div className="sc" style={{ flexGrow: 1, minHeight: 0, background: 'var(--panel)' }}>
            {docs.isLoading && <div style={{ padding: 30 }}><Spinner text="正在载入…" /></div>}
            {!docs.isLoading && (
              <table style={{ width: '100%', borderCollapse: 'collapse' }}>
                <thead>
                  <tr>
                    {['', '文档', '类别', '密级', '大小', '上传', '解析状态', '操作'].map((h, i) => (
                      <th key={i} style={{ ...th, width: [42, undefined, 100, 84, 90, 110, 230, 130][i] }}>{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => {
                    const selectable = r.parseStatus === 'NotParsed' || r.parseStatus === 'Failed';
                    const on = selected.has(r.id);
                    return (
                      <tr key={r.id} style={{ background: on ? 'var(--accent-bg)' : undefined }}>
                        <td style={td}>
                          <input type="checkbox" checked={on} disabled={!selectable} onChange={() => toggle(r.id)} />
                        </td>
                        <td style={td}>
                          <div style={{ fontSize: 12.5, fontWeight: 500, lineHeight: 1.45, whiteSpace: 'normal' }}>{r.title}</div>
                          <div style={{ fontSize: 10.5, color: 'var(--ink-3)', marginTop: 2 }}>
                            {r.kbName}{r.customerName ? ` · ${r.customerName}` : ''}{r.projectNo ? ` · ${r.projectNo}` : ''}
                          </div>
                        </td>
                        <td style={{ ...td, color: 'var(--ink-2)' }}>{r.docCategory ?? '—'}</td>
                        <td style={td}><span className={CLS_PILL[r.classification]}>{CLS_LABEL[r.classification]}</span></td>
                        <td className="m" style={{ ...td, color: 'var(--ink-3)', fontSize: 11.5 }}>{fmtSize(r.fileSize)}</td>
                        <td className="m" style={{ ...td, color: 'var(--ink-3)', fontSize: 11.5 }}>{r.uploadedAt.slice(5, 16).replace('T', ' ')}</td>
                        <td style={td}>
                          <div style={{ display: 'flex', alignItems: 'center', gap: 7, whiteSpace: 'normal' }}>
                            <span className="pill" style={STATUS_PILL[r.parseStatus]}>{PARSE_LABEL[r.parseStatus]}</span>
                            {r.parseStatus === 'Failed' && r.parseError && (
                              <span style={{ fontSize: 11, lineHeight: 1.5, color: 'var(--cls-conf-fg)' }}>{r.parseError}</span>
                            )}
                          </div>
                        </td>
                        <td style={td}>
                          <div style={{ display: 'flex', gap: 6 }}>
                            {(r.parseStatus === 'NotParsed' || r.parseStatus === 'Failed') && (
                              <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => void submitParse([r.id])}>提交解析</button>
                            )}
                            {r.parseStatus === 'Parsed' && (
                              <>
                                <button className="gbtn" style={{ height: 24, fontSize: 11.5 }}
                                  onClick={() => nav(`/admin/corpus/${r.id}/preview`, { state: { title: r.title } })}>对照预览</button>
                                <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => void reparse(r.id)}>重新解析</button>
                              </>
                            )}
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                  {rows.length === 0 && (
                    <tr><td colSpan={8} style={{ ...td, color: 'var(--ink-3)', padding: '20px 12px' }}>没有匹配的文档。</td></tr>
                  )}
                </tbody>
              </table>
            )}
          </div>

          <div style={{ height: 44, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 10, padding: '0 18px', background: 'var(--panel)', borderTop: '1px solid var(--line)' }}>
            <span className="hint">
              共 <span className="m">{stats.total}</span> 份　·　已解析 <span className="m">{stats.parsed}</span>
              　·　待解析 <span className="m">{stats.waiting}</span>
              　·　失败 <span className="m" style={{ color: stats.failed > 0 ? 'var(--cls-conf-fg)' : undefined }}>{stats.failed}</span>
            </span>
          </div>
        </>
      )}

      {tab === 'queue' && (
        <>
          <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 10, padding: '10px 18px', background: 'var(--bg-soft)', borderBottom: '1px solid var(--line)' }}>
            <span style={{ fontSize: 11.5, color: 'var(--ink-2)', lineHeight: 1.6 }}>
              「等待中」是解析引擎暂不可用，任务进度保留、服务恢复后自动继续，什么都不用做；「失败」才需要按原因处理。两者不是一回事。
            </span>
          </div>
          <div className="sc" style={{ flexGrow: 1, minHeight: 0, background: 'var(--panel)' }}>
            <table style={{ width: '100%', borderCollapse: 'collapse' }}>
              <thead>
                <tr>{['文档', '任务', '状态', '尝试', '入队', '开始', '结束', '原因'].map((h, i) => (
                  <th key={i} style={{ ...th, width: [undefined, 110, 90, 56, 120, 120, 120, undefined][i] }}>{h}</th>
                ))}</tr>
              </thead>
              <tbody>
                {(queue.data ?? []).map((j) => (
                  <tr key={j.id}>
                    <td style={{ ...td, whiteSpace: 'normal', fontSize: 12.5, fontWeight: 500 }}>{j.docTitle}</td>
                    <td style={{ ...td, color: 'var(--ink-2)' }}>{KIND_LABEL[j.kind] ?? j.kind}</td>
                    <td style={td}><span className="pill" style={JOB_PILL[j.status] ?? JOB_PILL.Queued}>{JOB_LABEL[j.status] ?? j.status}</span></td>
                    <td className="m" style={td}>{j.attempts}</td>
                    <td className="m" style={{ ...td, fontSize: 11.5, color: 'var(--ink-3)' }}>{j.queuedAt.slice(5, 16).replace('T', ' ')}</td>
                    <td className="m" style={{ ...td, fontSize: 11.5, color: 'var(--ink-3)' }}>{j.startedAt ? j.startedAt.slice(5, 16).replace('T', ' ') : '—'}</td>
                    <td className="m" style={{ ...td, fontSize: 11.5, color: 'var(--ink-3)' }}>{j.finishedAt ? j.finishedAt.slice(5, 16).replace('T', ' ') : '—'}</td>
                    <td style={{ ...td, whiteSpace: 'normal', fontSize: 11.5, color: j.status === 'Failed' ? 'var(--cls-conf-fg)' : 'var(--ink-3)' }}>{j.lastError ?? '—'}</td>
                  </tr>
                ))}
                {(queue.data ?? []).length === 0 && (
                  <tr><td colSpan={8} style={{ ...td, color: 'var(--ink-3)', padding: '20px 12px' }}>队列为空。</td></tr>
                )}
              </tbody>
            </table>
          </div>
        </>
      )}

      {uploading && (
        <UploadModal
          kbs={(kbs.data ?? []).filter((k) => k.isActive)}
          onClose={() => setUploading(false)}
          onUploaded={() => { setUploading(false); refresh(); }}
        />
      )}
    </>
  );
}

const th: React.CSSProperties = {
  fontSize: 11.5, fontWeight: 500, color: 'var(--ink-2)', textAlign: 'left', padding: '0 12px', height: 36,
  background: 'var(--bg-soft)', borderBottom: '1px solid var(--line)', whiteSpace: 'nowrap', position: 'sticky', top: 0,
};
const td: React.CSSProperties = { fontSize: 12.5, padding: '8px 12px', borderBottom: '1px solid var(--line-soft)', whiteSpace: 'nowrap', verticalAlign: 'top' };
