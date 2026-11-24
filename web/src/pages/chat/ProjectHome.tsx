// 项目首页：在项目里直接提问；左边是这个项目的对话，右边是项目说明、项目资料、关联的台账。
// 项目里的每次提问，助手都先看项目说明和台账信息，项目资料当附件一起读。第一版只有建项目的人自己能看。
import { useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, del, get, post, put, upload } from '../../lib/api';
import { DELIVERY_LABEL } from '../../lib/types';
import type { ChatAttachment } from '../../lib/types';
import { Icon } from '../../components/Icons';
import { Spinner } from '../../components/Common';
import { ATTACH_ACCEPT, formatSize } from './useAttachments';
import { projectColor } from './composerUtil';

export interface ProjectEdit { id: string; name: string; description: string | null; ledgerNo: string | null }
interface ProjectFile { id: string; fileName: string; size: number; status: ChatAttachment['status']; error: string | null; createdAt: string }
interface ProjectDetail {
  project: ProjectEdit & { createdAt: string; updatedAt: string };
  sessions: { id: string; title: string; updatedAt: string; messages: number }[];
  files: ProjectFile[];
  ledger: { no: string; customer: string | null; year: number | null; device: string | null; model: string | null; status: string | null; owner: string | null } | null;
}

const busyFile = (f: ProjectFile) => f.status !== 'Parsed' && f.status !== 'Failed' && f.status !== 'NotParsed';

function when(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const today = new Date();
  if (d.toDateString() === today.toDateString()) return `今天 ${d.toTimeString().slice(0, 5)}`;
  const y = new Date(today.getTime() - 86_400_000);
  if (d.toDateString() === y.toDateString()) return '昨天';
  return iso.slice(5, 10).replace('-', '/');
}

export function ProjectHome({ id, composer, busy, onOpenSession, onEdit, onGone, onPreview }: {
  id: string; composer: ReactNode; busy: boolean;
  onOpenSession: (sessionId: string) => void;
  onEdit: (p: ProjectEdit) => void;
  /** 项目归档了、或者找不到了：回普通首页 */
  onGone: () => void;
  onPreview: (f: ProjectFile) => void;
}) {
  const qc = useQueryClient();
  const q = useQuery({
    queryKey: ['chat-project', id], queryFn: () => get<ProjectDetail>(`/api/chat-projects/${id}`),
    refetchInterval: (x) => (x.state.data?.files.some(busyFile) ? 2000 : false),
  });
  const pick = useRef<HTMLInputElement>(null);
  const [note, setNote] = useState<string | null>(null);
  const [desc, setDesc] = useState<string | null>(null);   // 正在改的项目说明；null = 没在改
  const [saving, setSaving] = useState(false);
  const reload = () => { void qc.invalidateQueries({ queryKey: ['chat-project', id] }); void qc.invalidateQueries({ queryKey: ['chat-projects'] }); };

  if (q.isPending) return <div className="pjh"><Spinner text="打开项目…" /></div>;
  if (q.isError || !q.data) {
    return (
      <div className="pjh"><div className="pjh-panel" style={{ padding: 16 }}>
        <b>这个项目打不开</b><p className="hint">{q.error instanceof ApiError ? q.error.message : '可能已经归档了'}</p>
        <button type="button" className="gbtn" onClick={onGone}>回首页</button>
      </div></div>
    );
  }
  const { project: p, sessions, files, ledger } = q.data;

  const addFiles = async (list: FileList) => {
    setNote(null);
    for (const file of Array.from(list)) {
      const ext = file.name.slice(file.name.lastIndexOf('.')).toLowerCase();
      if (!ATTACH_ACCEPT.split(',').includes(ext)) { setNote(`「${file.name}」格式不支持（可传 Word、Excel、PowerPoint、PDF、图片）`); continue; }
      if (file.size > 20 * 1024 * 1024) { setNote(`「${file.name}」超过 20MB`); continue; }
      const form = new FormData();
      form.append('file', file);
      try { await upload(`/api/chat-projects/${id}/files`, form); }
      catch (e) { setNote(e instanceof ApiError ? e.message : `「${file.name}」没传上去`); }
    }
    reload();
  };
  const removeFile = async (f: ProjectFile) => {
    if (!window.confirm(`从项目里删掉「${f.fileName}」？`)) return;
    try { await del(`/api/chat-projects/${id}/files/${f.id}`); reload(); }
    catch (e) { setNote(e instanceof ApiError ? e.message : '没删掉'); }
  };
  const saveDesc = async () => {
    if (desc === null) return;
    setSaving(true);
    try { await put(`/api/chat-projects/${id}`, { name: p.name, description: desc, ledgerNo: p.ledgerNo }); setDesc(null); reload(); }
    catch (e) { setNote(e instanceof ApiError ? e.message : '没存上'); }
    finally { setSaving(false); }
  };
  const archive = async () => {
    if (!window.confirm(`归档「${p.name}」？\n侧栏里不再显示；里面的对话移回「最近」，不会删。`)) return;
    try { await del(`/api/chat-projects/${id}`); onGone(); }
    catch (e) { setNote(e instanceof ApiError ? e.message : '没归档成'); }
  };

  return (
    <div className="pjh">
      <header className="pjh-hd">
        <span className="pj-ic big" style={{ background: projectColor(p.id) }}>{p.name.slice(0, 1)}</span>
        <h1>{p.name}</h1>
        {p.ledgerNo && <span className="pjh-tag ledger"><Icon name="link" size={12} />台账 {p.ledgerNo}</span>}
        <span className="pjh-tag">只有我能看</span>
        <span className="pjh-act">
          <button type="button" className="hm-link" onClick={() => onEdit(p)}><Icon name="edit" size={13} />改名、关联台账</button>
          <button type="button" className="hm-link" onClick={() => void archive()}>归档</button>
        </span>
      </header>

      {composer}
      {note && <div className="pjh-note" role="alert"><Icon name="warn" size={14} />{note}<button type="button" aria-label="知道了" onClick={() => setNote(null)}><Icon name="x" size={12} /></button></div>}

      <div className="pjh-cols">
        <section className="pjh-panel pjh-convs" aria-label="这个项目的对话">
          <h5>对话<span>{sessions.length} 个</span></h5>
          {sessions.length === 0 && <div className="pjh-empty">在上面问第一个问题，对话就会放在这里。</div>}
          {sessions.map((s) => (
            <button key={s.id} type="button" className="pjh-cv" disabled={busy} onClick={() => onOpenSession(s.id)}>
              <b>{s.title}</b><span>{when(s.updatedAt)}{s.messages > 0 ? ` · ${Math.ceil(s.messages / 2)} 问` : ''}</span>
            </button>
          ))}
        </section>

        <aside className="pjh-side">
          <section className="pjh-panel">
            <h5>项目说明
              {desc === null && <button type="button" className="hm-link" onClick={() => setDesc(p.description ?? '')}><Icon name="edit" size={12} />改</button>}
            </h5>
            {desc === null ? (
              p.description ? <div className="pjh-desc">{p.description}</div>
                : <button type="button" className="pjh-empty pjh-empty-btn" onClick={() => setDesc('')}>写几句背景：客户最在意什么、设计条件、联系人……</button>
            ) : (
              <>
                <textarea className="pjh-ta" autoFocus value={desc} maxLength={2000} rows={5} onChange={(e) => setDesc(e.target.value)}
                  placeholder="客户最在意交期（60 天内）和防爆；联系人王工，倾向磁力搅拌。" />
                <div className="pjh-row">
                  <button type="button" className="gbtn" onClick={() => setDesc(null)}>取消</button>
                  <button type="button" className="pbtn" disabled={saving} onClick={() => void saveDesc()}>{saving ? '存…' : '存好'}</button>
                </div>
              </>
            )}
            <span className="pjh-hint">每次在这个项目里提问，助手都先看这段</span>
          </section>

          <section className="pjh-panel">
            <h5>项目资料
              <button type="button" className="hm-link" onClick={() => pick.current?.click()}><Icon name="upload" size={12} />上传</button>
            </h5>
            <input ref={pick} type="file" multiple accept={ATTACH_ACCEPT} hidden
              onChange={(e) => { if (e.target.files?.length) void addFiles(e.target.files); e.target.value = ''; }} />
            {files.length === 0 && <div className="pjh-empty">技术协议、图纸、报价单放这里，项目里的每次提问助手都能读到。只在这个项目里用，不进公司知识库。</div>}
            {files.map((f) => (
              <div key={f.id} className={'pjh-file' + (f.status === 'Failed' ? ' bad' : '')}>
                <Icon name="file" size={14} />
                <button type="button" className="n" title={`${f.fileName} · 点开预览`} onClick={() => onPreview(f)}>{f.fileName}</button>
                <span className="s">{busyFile(f) ? '解析中…' : f.status === 'Failed' ? (f.error ?? '没读出来') : formatSize(f.size)}</span>
                <button type="button" className="x" aria-label={`删掉 ${f.fileName}`} onClick={() => void removeFile(f)}><Icon name="x" size={12} /></button>
              </div>
            ))}
          </section>

          {p.ledgerNo && (
            <section className="pjh-panel">
              <h5><span><Icon name="link" size={13} style={{ verticalAlign: '-2px', marginRight: 5 }} />台账 {p.ledgerNo}</span></h5>
              {ledger ? (
                <div className="pjh-kv">
                  {ledger.customer && <><span>客户</span><span>{ledger.customer}</span></>}
                  {(ledger.device || ledger.model) && <><span>设备</span><span>{[ledger.device, ledger.model].filter(Boolean).join(' · ')}</span></>}
                  {ledger.year && <><span>年份</span><span>{ledger.year}</span></>}
                  {ledger.status && <><span>状态</span><span>{DELIVERY_LABEL[ledger.status] ?? ledger.status}</span></>}
                  {ledger.owner && <><span>负责人</span><span>{ledger.owner}</span></>}
                </div>
              ) : <div className="pjh-empty">台账里查不到这个项目号（或你没有查台账的权限）。</div>}
            </section>
          )}
        </aside>
      </div>
    </div>
  );
}

/** 新建项目 / 改项目：名字、关联台账项目号（可不填）、项目说明。 */
export function ProjectDialog({ edit, onClose, onDone }: {
  edit: ProjectEdit | null; onClose: () => void; onDone: (id: string) => void;
}) {
  const [name, setName] = useState(edit?.name ?? '');
  const [ledgerNo, setLedgerNo] = useState(edit?.ledgerNo ?? '');
  const [desc, setDesc] = useState(edit?.description ?? '');
  const [err, setErr] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const submit = async () => {
    if (!name.trim()) { setErr('给项目起个名字'); return; }
    setSaving(true); setErr(null);
    const body = { name: name.trim(), ledgerNo: ledgerNo.trim() || null, description: desc.trim() || null };
    try {
      if (edit) { await put(`/api/chat-projects/${edit.id}`, body); onDone(edit.id); }
      else { const r = await post<{ id: string }>('/api/chat-projects', body); onDone(r.id); }
    } catch (e) { setErr(e instanceof ApiError ? e.message : '没存上'); }
    finally { setSaving(false); }
  };
  return (
    <div className="modal-mask" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <form className="modal-box pjd" role="dialog" aria-modal="true" aria-labelledby="pjd-t"
        onSubmit={(e) => { e.preventDefault(); void submit(); }} onKeyDown={(e) => { if (e.key === 'Escape') onClose(); }}>
        <b id="pjd-t">{edit ? '改项目' : '新建项目'}</b>
        <label>名字<input autoFocus value={name} maxLength={80} onChange={(e) => setName(e.target.value)} placeholder="某研究院 30L 加氢釜" /></label>
        <label>关联台账项目号（可不填）
          <input value={ledgerNo} maxLength={64} onChange={(e) => setLedgerNo(e.target.value)} placeholder="比如 HT-2026-017" />
          <span className="pjh-hint">关联了，助手直接知道客户、设备、交付情况；只读，不改台账</span>
        </label>
        <label>项目说明（给助手看的背景）
          <textarea rows={4} value={desc} maxLength={2000} onChange={(e) => setDesc(e.target.value)} placeholder="客户最在意交期（60 天内）和防爆；联系人王工，倾向磁力搅拌。" />
        </label>
        {err && <div className="pjh-note" role="alert"><Icon name="warn" size={14} />{err}</div>}
        <div className="pjh-row">
          {!edit && <span className="pjh-hint" style={{ marginRight: 'auto' }}>资料可以建好以后再传；只有你自己能看</span>}
          <button type="button" className="gbtn" onClick={onClose}>取消</button>
          <button type="submit" className="pbtn" disabled={saving}>{saving ? '存…' : edit ? '存好' : '建好'}</button>
        </div>
      </form>
    </div>
  );
}
