// 个人中心 › 我的记忆：助手记下的关于我的事。只有本人能看、能改、能删；管理员只看得到条数。
import { useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { del, get, post, put, ApiError } from '../../lib/api';
import type { MemoryState } from '../../lib/types';

const KINDS = ['偏好', '工作', '客户'];

export function MemorySection() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['my-memories'], queryFn: () => get<MemoryState>('/api/profile/memories') });
  const [text, setText] = useState('');
  const [kind, setKind] = useState('偏好');
  const [edit, setEdit] = useState<{ id: string; text: string; kind: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => { if (location.hash === '#memory' && q.data) ref.current?.scrollIntoView({ block: 'start' }); }, [q.data]);

  const run = async (fn: () => Promise<unknown>) => {
    setError(null);
    try { await fn(); void qc.invalidateQueries({ queryKey: ['my-memories'] }); return true; }
    catch (err) { setError(err instanceof ApiError ? err.message : '操作失败'); return false; }
  };
  const d = q.data;
  if (!d) return null;
  const on = d.globalOn && d.enabled;

  return (
    <div ref={ref} id="memory" className="card" style={{ padding: '14px 16px', marginBottom: 12 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 6 }}>
        <span style={{ fontSize: 12.5, fontWeight: 600 }}>我的记忆</span>
        <span className="hint">{d.items.length} / {d.max} 条</span>
        <div style={{ flexGrow: 1 }} />
        {d.globalOn ? (
          <button type="button" role="switch" aria-checked={d.enabled} aria-label="长期记忆开关" className={'tl-sw' + (d.enabled ? ' on' : '')}
            onClick={() => void run(() => put('/api/profile/memories/enabled', { enabled: !d.enabled }))}><i /></button>
        ) : <span className="pill pill-neutral">管理员已关闭长期记忆</span>}
      </div>
      <div className="hint" style={{ marginBottom: 10, lineHeight: 1.65 }}>
        助手会记住你交代的偏好、工作范围、在跟的客户，以后的对话都会带上。这里的内容<b>只有你自己能看到</b>；管理员只看得到条数。
        联系方式、证件号、口令这类不会被记。{!d.enabled && d.globalOn && '现在已关闭：助手不会读取也不会新记。'}
      </div>
      {error && <div style={{ fontSize: 12, color: 'var(--cls-conf-fg)', marginBottom: 8 }}>{error}</div>}
      {d.items.map((m) => (
        <div key={m.id} className="mem-row" style={{ opacity: on ? 1 : 0.6 }}>
          {edit?.id === m.id ? (
            <form className="mem-edit" onSubmit={(e) => { e.preventDefault(); void run(() => put(`/api/profile/memories/${m.id}`, { text: edit.text, kind: edit.kind })).then((ok) => ok && setEdit(null)); }}>
              <select className="inp" value={edit.kind} onChange={(e) => setEdit({ ...edit, kind: e.target.value })}>{KINDS.map((k) => <option key={k}>{k}</option>)}</select>
              <input className="inp" value={edit.text} maxLength={200} onChange={(e) => setEdit({ ...edit, text: e.target.value })} autoFocus />
              <button type="submit" className="pbtn">保存</button>
              <button type="button" className="gbtn" onClick={() => setEdit(null)}>取消</button>
            </form>
          ) : (
            <>
              <span className="mem-kind">{m.kind}</span>
              <span className="mem-text">{m.text}</span>
              <span className="hint" style={{ flexShrink: 0 }}>{m.source === 'chat' ? '对话中记下' : '手动添加'} · {m.updatedAt.slice(0, 10)}</span>
              <button type="button" className="fm-act" onClick={() => setEdit({ id: m.id, text: m.text, kind: m.kind })}>改</button>
              <button type="button" className="fm-act" onClick={() => void run(() => del(`/api/profile/memories/${m.id}`))}>删</button>
            </>
          )}
        </div>
      ))}
      {d.items.length === 0 && <div className="hint" style={{ padding: '4px 0 8px' }}>还没有记忆。对话里说「记住……」，或者在下面直接添加。</div>}
      <form className="mem-edit" style={{ marginTop: 8 }} onSubmit={(e) => {
        e.preventDefault();
        if (text.trim()) void run(() => post('/api/profile/memories', { text, kind })).then((ok) => ok && setText(''));
      }}>
        <select className="inp" value={kind} onChange={(e) => setKind(e.target.value)} aria-label="类别">{KINDS.map((k) => <option key={k}>{k}</option>)}</select>
        <input className="inp" placeholder="如：报价一律按含税价写" value={text} maxLength={200} onChange={(e) => setText(e.target.value)} />
        <button type="submit" className="gbtn" disabled={!text.trim() || d.items.length >= d.max}>添加</button>
      </form>
    </div>
  );
}
