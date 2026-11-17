// 文件管理：员工把手里的资料交给知识库。只列当前账号自己上传的文件（服务端按上传人过滤）。
// 流程：上传 → 管理员审核 → 通过即自动解析 → 同事能问到；驳回会写原因，这里看得到。
import React, { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, download, get, ApiError } from '../../lib/api';
import type { MyFileRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';
import { Icon } from '../../components/Icons';
import UploadModal from '../admin/UploadModal';

type Stage = 'pending' | 'rejected' | 'processing' | 'failed' | 'done';

function stageOf(r: MyFileRow): Stage {
  if (r.review === 'Pending') return 'pending';
  if (r.review === 'Rejected') return 'rejected';
  if (r.parseStatus === 'Parsed') return 'done';
  if (r.parseStatus === 'Failed') return 'failed';
  return 'processing';
}

const STAGE: Record<Stage, { label: string; dot: string; fg: string }> = {
  pending: { label: '待审核', dot: '#c98a12', fg: '#8a5a00' },
  rejected: { label: '已驳回', dot: 'var(--brand)', fg: 'var(--brand-fg)' },
  processing: { label: '解析中', dot: '#2f5ccc', fg: '#2a4a85' },
  failed: { label: '解析失败', dot: 'var(--brand)', fg: 'var(--brand-fg)' },
  done: { label: '已入库', dot: '#1c7c54', fg: '#1c6b45' },
};

const TABS: { key: 'all' | Stage; label: string }[] = [
  { key: 'all', label: '全部' }, { key: 'pending', label: '待审核' }, { key: 'rejected', label: '已驳回' },
  { key: 'processing', label: '解析中' }, { key: 'done', label: '已入库' },
];

const EXT_COLOR: Record<string, [string, string]> = {
  PDF: ['#fde8ea', '#a00d25'], DOCX: ['#e8eef8', '#2a4a85'], DOC: ['#e8eef8', '#2a4a85'],
  XLSX: ['#e6f2ec', '#1c6b45'], XLS: ['#e6f2ec', '#1c6b45'], PPTX: ['#fbf0d9', '#8a5a00'],
};

const FLOW = [
  { n: 1, t: '上传', d: '填资料类型、客户/项目', cls: 'f1' },
  { n: 2, t: '管理员审核', d: '通过或写明原因驳回', cls: 'f2' },
  { n: 3, t: '自动解析', d: '通过后排队，不用再点', cls: 'f3' },
  { n: 4, t: '可检索', d: '同事提问能引用到', cls: 'f4' },
];

const fmtSize = (n: number) => (n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`);
function when(iso: string) {
  const t = new Date(iso);
  const day = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diff = Math.round((day(new Date()) - day(t)) / 86400000);
  const hm = `${String(t.getHours()).padStart(2, '0')}:${String(t.getMinutes()).padStart(2, '0')}`;
  return diff === 0 ? `今天 ${hm}` : diff === 1 ? `昨天 ${hm}` : `${t.getMonth() + 1}/${t.getDate()} ${hm}`;
}

export default function FilesPage() {
  const qc = useQueryClient();
  const [tab, setTab] = useState<'all' | Stage>('all');
  const [search, setSearch] = useState('');
  const [uploading, setUploading] = useState<File[] | null>(null);
  const [drag, setDrag] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const q = useQuery({
    queryKey: ['my-files'],
    queryFn: () => get<MyFileRow[]>('/api/my-files'),
    // 有在审核或解析中的就定时刷新，状态变了不用手动刷
    refetchInterval: (query) => ((query.state.data ?? []).some((r) => ['pending', 'processing'].includes(stageOf(r))) ? 8000 : false),
  });
  const rows = useMemo(() => q.data ?? [], [q.data]);
  const counts = useMemo(() => {
    const c: Record<string, number> = { all: rows.length };
    for (const r of rows) { const s = stageOf(r); const k = s === 'failed' ? 'processing' : s; c[k] = (c[k] ?? 0) + 1; }
    return c;
  }, [rows]);
  const shown = rows.filter((r) => {
    const s = stageOf(r);
    if (tab !== 'all' && (s === 'failed' ? 'processing' : s) !== tab) return false;
    const k = search.trim().toLowerCase();
    return !k || r.title.toLowerCase().includes(k) || r.fileName.toLowerCase().includes(k);
  });

  const refresh = () => void qc.invalidateQueries({ queryKey: ['my-files'] });
  const withdraw = async (r: MyFileRow) => {
    const verb = r.review === 'Rejected' ? '删除' : '撤回';
    if (!window.confirm(`${verb}「${r.title}」？原件会一起删除。`)) return;
    setError(null);
    try { await del(`/api/my-files/${r.id}`); refresh(); }
    catch (err) { setError(err instanceof ApiError ? err.message : `${verb}失败`); }
  };

  const note = (r: MyFileRow): { text: string; warn?: boolean } => {
    switch (stageOf(r)) {
      case 'pending': return { text: '等管理员审核，通过后自动解析' };
      case 'rejected': return { text: r.reviewNote ? `驳回原因：${r.reviewNote}` : '已驳回', warn: true };
      case 'failed': return { text: r.parseError ? `审核已通过，解析没成功：${r.parseError}。管理员会处理` : '审核已通过，解析没成功，管理员会处理', warn: true };
      case 'done': return { text: `已入库 · ${r.chunkCount} 个片段 · 同事提问能引用到` };
      default: return { text: r.parseStatus === 'Waiting' ? '审核已通过 · 解析服务暂时不可用，恢复后自动继续' : '审核已通过 · 正在解析' };
    }
  };

  const onDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setDrag(false);
    const fs = Array.from(e.dataTransfer.files ?? []);
    if (fs.length) setUploading(fs);
  };

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, background: '#FAFAF9' }}
      onDragOver={(e) => { e.preventDefault(); setDrag(true); }} onDragLeave={() => setDrag(false)} onDrop={onDrop}>
      <div className="dash-in">
        <div className="dash-head">
          <div>
            <h1 className="dash-title">文件管理</h1>
            <p className="dash-sub">这里是你用自己账号上传过的全部文件，别人上传的看不到。管理员审核通过后自动解析，之后所有同事都能问到。</p>
          </div>
          <button className="pbtn dash-btn" style={{ display: 'inline-flex', alignItems: 'center', gap: 7 }} onClick={() => setUploading([])}>
            <Icon name="upload" size={16} stroke={2} />上传文件
          </button>
        </div>

        <section style={{ display: 'flex', gap: 18, flexWrap: 'wrap' }}>
          <button type="button" className={'fm-drop' + (drag ? ' on' : '')} onClick={() => setUploading([])}>
            <span className="fm-drop-i" aria-hidden="true"><Icon name="upload" size={24} /></span>
            <span style={{ display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0, textAlign: 'left' }}>
              <b>把文件拖到这里，或点击选择</b>
              <span>PDF、Word、Excel、PPT、文本 · 一次最多 20 份 · 默认按「内部」密级</span>
            </span>
          </button>
          <div className="dash-card fm-flow">
            <span className="dash-muted" style={{ fontSize: 13 }}>一份文件的经历</span>
            <div style={{ display: 'flex', alignItems: 'flex-start', gap: 6 }}>
              {FLOW.map((f) => (
                <div key={f.n} className="fm-step">
                  <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}><i className={f.cls}>{f.n}</i><span className="fm-line" /></div>
                  <b>{f.t}</b><span>{f.d}</span>
                </div>
              ))}
            </div>
          </div>
        </section>

        {error && <ErrorBox message={error} />}

        <section className="dash-card" style={{ padding: 0, overflow: 'hidden' }}>
          <div className="fm-bar">
            <div role="tablist" style={{ display: 'flex', gap: 24 }}>
              {TABS.map((t) => (
                <button key={t.key} type="button" role="tab" aria-selected={tab === t.key} className={'fm-tab' + (tab === t.key ? ' on' : '')} onClick={() => setTab(t.key)}>
                  {t.label}<span>{counts[t.key] ?? 0}</span>
                </button>
              ))}
            </div>
            <div style={{ flexGrow: 1 }} />
            <input className="inp" style={{ width: 220, height: 34 }} placeholder="搜索文件名" value={search} onChange={(e) => setSearch(e.target.value)} />
          </div>
          {q.isLoading && <div style={{ padding: 24 }}><Spinner text="载入…" /></div>}
          {q.isError && <div style={{ padding: 24 }}><ErrorBox message={(q.error as Error).message} /></div>}
          {!q.isLoading && !q.isError && (
            <div style={{ overflowX: 'auto' }}>
              <table className="us-tbl fm-tbl">
                <thead>
                  <tr><th>文件</th><th>资料类型</th><th>客户 / 项目</th><th>上传时间</th><th style={{ width: '32%' }}>状态</th><th style={{ textAlign: 'right' }}>操作</th></tr>
                </thead>
                <tbody>
                  {shown.map((r) => {
                    const s = STAGE[stageOf(r)];
                    const n = note(r);
                    const ext = (r.fileName.split('.').pop() ?? '').toUpperCase().slice(0, 4);
                    const [bg, fg] = EXT_COLOR[ext] ?? ['#f1f1ef', '#4a4a4a'];
                    return (
                      <tr key={r.id}>
                        <td>
                          <span style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                            <span className="fm-ext" style={{ background: bg, color: fg }} aria-hidden="true">{ext}</span>
                            <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
                              <b style={{ fontWeight: 600, whiteSpace: 'normal' }}>{r.title}</b>
                              <span className="dash-muted" style={{ fontSize: 12 }}>{fmtSize(r.fileSize)}</span>
                            </span>
                          </span>
                        </td>
                        <td>{r.docCategory ?? '—'}</td>
                        <td style={{ color: 'var(--ink-2)' }}>{[r.customerName, r.projectNo].filter(Boolean).join(' · ') || '—'}</td>
                        <td style={{ color: 'var(--ink-3)' }}>{when(r.uploadedAt)}</td>
                        <td style={{ whiteSpace: 'normal' }}>
                          <span style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
                            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 7, fontWeight: 600, color: s.fg }}>
                              <i style={{ width: 8, height: 8, borderRadius: '50%', background: s.dot }} />{s.label}
                            </span>
                            <span style={{ fontSize: 12, lineHeight: 1.5, color: n.warn ? 'var(--brand-fg)' : 'var(--ink-3)' }}>{n.text}</span>
                          </span>
                        </td>
                        <td style={{ textAlign: 'right' }}>
                          <span style={{ display: 'inline-flex', gap: 14 }}>
                            {r.review === 'Rejected' && <button type="button" className="fm-act" onClick={() => setUploading([])}>重新上传</button>}
                            {r.review !== 'Approved' && <button type="button" className="fm-act" onClick={() => void withdraw(r)}>{r.review === 'Rejected' ? '删除' : '撤回'}</button>}
                            {r.review === 'Approved' && (
                              <button type="button" className="fm-act" onClick={() => void download(`/api/files/${r.id}`, r.fileName)
                                .catch((err) => setError(err instanceof ApiError ? err.message : '下载失败'))}>下载</button>
                            )}
                          </span>
                        </td>
                      </tr>
                    );
                  })}
                  {shown.length === 0 && (
                    <tr><td colSpan={6} className="dash-empty" style={{ padding: '28px 24px' }}>
                      {rows.length === 0 ? '还没有上传过文件。手册、方案、图纸、作业指导书都可以交上来。' : '这一栏没有文件。'}
                    </td></tr>
                  )}
                </tbody>
              </table>
            </div>
          )}
        </section>
      </div>

      {uploading && (
        <UploadModal mode="staff" initialFiles={uploading}
          onClose={() => setUploading(null)}
          onUploaded={() => { setUploading(null); setTab('pending'); refresh(); }} />
      )}
    </div>
  );
}
