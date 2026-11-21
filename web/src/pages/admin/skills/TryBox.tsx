// 试一试：同一个问题，不开技能、只开这个技能各答一遍，并排看。审核时用；有代表性的直接存成测试题。
import { useRef, useState } from 'react';
import { post, sse, ApiError } from '../../../lib/api';
import { Icon } from '../../../components/Icons';
import { useEscape } from '../quality/useEscape';
import { Prose } from '../../../components/Prose';

interface Side { text: string; steps: { label: string; detail: string }[]; running: boolean; error: string | null }
const EMPTY: Side = { text: '', steps: [], running: false, error: null };

export function TryBox({ code, name, onClose }: { code: string; name: string; onClose: () => void }) {
  const [q, setQ] = useState('');
  const [off, setOff] = useState<Side>(EMPTY);
  const [on, setOn] = useState<Side>(EMPTY);
  const [saved, setSaved] = useState<string | null>(null);
  const abort = useRef<AbortController | null>(null);
  const close = () => { abort.current?.abort(); onClose(); };
  useEscape(close);

  const ask = async (enabled: boolean, set: (f: (s: Side) => Side) => void, signal: AbortSignal) => {
    set(() => ({ ...EMPTY, running: true }));
    try {
      for await (const ev of sse('/api/chat/completions', { question: q.trim(), skillTest: { code, on: enabled } }, signal)) {
        const p = (ev.payload ?? {}) as Record<string, unknown>;
        if (ev.kind === 'delta') set((s) => ({ ...s, text: s.text + String(p.text ?? '') }));
        else if (ev.kind === 'text') set((s) => ({ ...s, text: String(p.message ?? p.text ?? s.text) }));
        else if (ev.kind === 'step' && p.status !== 'running') set((s) => ({ ...s, steps: [...s.steps, { label: String(p.label ?? ''), detail: String(p.detail ?? '') }] }));
        else if (ev.kind === 'error') set((s) => ({ ...s, error: String(p.message ?? '出错了') }));
      }
    } catch (e) {
      if (!signal.aborted) set((s) => ({ ...s, error: e instanceof ApiError ? e.message : '没答完' }));
    } finally { set((s) => ({ ...s, running: false })); }
  };
  const run = () => {
    if (!q.trim()) return;
    abort.current?.abort();
    const c = new AbortController(); abort.current = c;
    setSaved(null);
    void ask(false, setOff, c.signal);
    void ask(true, setOn, c.signal);
  };
  const saveQuestion = async () => {
    try { const r = await post<{ code: string }>('/api/quality/eval/questions', { question: q.trim(), expectDocs: [], expectProjects: [], expectPoints: [], expectAsk: false, dept: null }); setSaved(`已存为测试题 ${r.code}，去「回答质量」补上答案要点`); }
    catch (e) { setSaved(e instanceof ApiError ? e.message : '没存上'); }
  };
  const busy = off.running || on.running;

  return (
    <div className="q-mask" role="dialog" aria-modal="true" aria-label="试一试" onClick={close}>
      <div className="q-dlg sk-try" onClick={(e) => e.stopPropagation()}>
        <div className="q-dlg-hd"><b>试一试：{name}</b><span className="hint">同一个问题开不开这个技能各答一遍（草稿也能试）</span>
          <button type="button" className="q-x" aria-label="关闭" onClick={close}><Icon name="x" size={14} /></button></div>
        <div className="sk-try-q">
          <input className="sk-in" value={q} onChange={(e) => setQ(e.target.value)} placeholder="输入一个这类问题，比如：反应釜接触 10% 盐酸、80 ℃，用 316L 行不行？"
            onKeyDown={(e) => { if (e.key === 'Enter' && !busy) run(); }} />
          <button type="button" className="pbtn" disabled={busy || !q.trim()} onClick={run}>{busy ? '正在答…' : '各答一遍'}</button>
        </div>
        <div className="sk-try-cols">
          <Answer title="不开技能" side={off} />
          <Answer title={`开「${name}」`} side={on} highlight />
        </div>
        <div className="q-dlg-ft">
          <span className="hint" style={{ marginRight: 'auto' }}>{saved ?? '两次问答会留在你的对话历史里'}</span>
          <button type="button" className="gbtn" disabled={!q.trim() || busy || !on.text} onClick={() => void saveQuestion()}>存为测试题</button>
          <button type="button" className="gbtn" onClick={close}>关闭</button>
        </div>
      </div>
    </div>
  );
}

function Answer({ title, side, highlight }: { title: string; side: Side; highlight?: boolean }) {
  return (
    <div className={'sk-try-col' + (highlight ? ' hl' : '')}>
      <div className="sk-try-h"><b>{title}</b>{side.running && <span className="hint">正在答…</span>}</div>
      {side.steps.length > 0 && (
        <div className="sk-try-steps">{side.steps.map((s, i) => <span key={i}><b>{s.label}</b>{s.detail ? ` ${s.detail}` : ''}</span>)}</div>
      )}
      {side.error && <div className="q-err">{side.error}</div>}
      {side.text ? <div className="sk-try-a"><Prose text={side.text} live={side.running} /></div> : <div className="sk-try-a hint">{side.running ? '' : '（还没问）'}</div>}
    </div>
  );
}
