// 测试题的编辑框：从错题「存为测试题」时预先填好问题、当时搜到的资料、提问人的补充；也用来手动加一题、改一题。
import { useState } from 'react';
import { useEscape } from './useEscape';
import { post, put, ApiError } from '../../../lib/api';
import { Icon } from '../../../components/Icons';

export interface QuestionDraft {
  id?: string; question: string; expectDocs: string[]; expectProjects: string[]; expectPoints: string[];
  expectAsk: boolean; dept: string; issueId?: string; note?: string;
  /** 从当时搜到的资料里可以直接勾的 */
  candidates?: string[];
}

function Chips({ items, onRemove }: { items: string[]; onRemove: (x: string) => void }) {
  return (
    <>
      {items.map((x) => (
        <span key={x} className="q-chip on">{x}
          <button type="button" aria-label={`去掉 ${x}`} onClick={() => onRemove(x)}><Icon name="x" size={11} stroke={2.4} /></button>
        </span>
      ))}
    </>
  );
}

function AddInput({ placeholder, onAdd }: { placeholder: string; onAdd: (x: string) => void }) {
  const [v, setV] = useState('');
  const add = () => { const t = v.trim(); if (t) { onAdd(t); setV(''); } };
  return (
    <input className="q-add" value={v} placeholder={placeholder} onChange={(e) => setV(e.target.value)}
      onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); add(); } }} onBlur={add} />
  );
}

export function QuestionForm({ draft, onClose, onSaved }: { draft: QuestionDraft; onClose: () => void; onSaved: (code: string) => void }) {
  const [d, setD] = useState<QuestionDraft>({ ...draft, expectPoints: draft.expectPoints.length ? draft.expectPoints : [''] });
  useEscape(onClose);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const set = (p: Partial<QuestionDraft>) => setD((x) => ({ ...x, ...p }));
  const uniq = (xs: string[]) => [...new Set(xs)];

  const save = async () => {
    setBusy(true); setErr(null);
    const body = {
      question: d.question, expectDocs: d.expectDocs, expectProjects: d.expectProjects,
      expectPoints: d.expectPoints.map((p) => p.trim()).filter(Boolean), expectAsk: d.expectAsk, dept: d.dept || null, issueId: d.issueId ?? null,
    };
    try {
      if (d.id) { await put(`/api/quality/eval/questions/${d.id}`, body); onSaved(''); }
      else { const r = await post<{ code: string }>('/api/quality/eval/questions', body); onSaved(r.code); }
    } catch (e) { setErr(e instanceof ApiError ? e.message : '没存上'); } finally { setBusy(false); }
  };

  const unpicked = (d.candidates ?? []).filter((c) => !d.expectDocs.includes(c));
  return (
    <div className="q-mask" role="dialog" aria-modal="true" aria-label="测试题" onClick={onClose}>
      <div className="q-dlg" onClick={(e) => e.stopPropagation()}>
        <div className="q-dlg-hd">
          <b>{d.id ? '改测试题' : '存为测试题'}</b>
          {d.note && <span className="hint">{d.note}</span>}
          <button type="button" className="q-x" aria-label="关闭" onClick={onClose}><Icon name="x" size={14} /></button>
        </div>
        <label className="q-field"><span>问题</span>
          <textarea rows={2} value={d.question} onChange={(e) => set({ question: e.target.value })} />
        </label>
        <div className="q-field"><span>应该查到的资料 <i>· 文件名或标题里的一段文字；从当时搜到的里点，或自己写</i></span>
          <div className="q-chips">
            <Chips items={d.expectDocs} onRemove={(x) => set({ expectDocs: d.expectDocs.filter((y) => y !== x) })} />
            {unpicked.map((c) => (
              <button key={c} type="button" className="q-chip" onClick={() => set({ expectDocs: uniq([...d.expectDocs, c]) })}><Icon name="plus" size={12} className="gi l" />{c}</button>
            ))}
            <AddInput placeholder="再加一份" onAdd={(x) => set({ expectDocs: uniq([...d.expectDocs, x]) })} />
          </div>
        </div>
        <div className="q-field"><span>应该查到的项目 <i>· 项目号，可不填</i></span>
          <div className="q-chips">
            <Chips items={d.expectProjects} onRemove={(x) => set({ expectProjects: d.expectProjects.filter((y) => y !== x) })} />
            <AddInput placeholder="项目号" onAdd={(x) => set({ expectProjects: uniq([...d.expectProjects, x]) })} />
          </div>
        </div>
        <div className="q-field"><span>答案里要有的要点 <i>· 同一个意思的几种说法用 | 隔开，比如「缠绕垫|金属缠绕垫」</i></span>
          {d.expectPoints.map((p, i) => (
            <div key={i} className="q-point">
              <input value={p} onChange={(e) => set({ expectPoints: d.expectPoints.map((x, j) => (j === i ? e.target.value : x)) })} />
              {d.expectPoints.length > 1 && (
                <button type="button" aria-label="去掉这条要点" onClick={() => set({ expectPoints: d.expectPoints.filter((_, j) => j !== i) })}><Icon name="x" size={12} /></button>
              )}
            </div>
          ))}
          <button type="button" className="q-link" onClick={() => set({ expectPoints: [...d.expectPoints, ''] })}><Icon name="plus" size={12} className="gi l" />再加一条要点</button>
        </div>
        <label className="q-check">
          <input type="checkbox" checked={d.expectAsk} onChange={(e) => set({ expectAsk: e.target.checked })} />
          <Icon name={d.expectAsk ? 'boxOn' : 'boxOff'} size={15} />这题应该先问清楚再答（比如没说是哪台设备）
        </label>
        <div className="q-dlg-ft">
          <label className="q-dept">部门 <input value={d.dept} onChange={(e) => set({ dept: e.target.value })} /></label>
          {err && <span className="q-err">{err}</span>}
          <button type="button" className="gbtn" onClick={onClose}>取消</button>
          <button type="button" className="pbtn" disabled={busy || !d.question.trim()} onClick={() => void save()}>{busy ? '正在存…' : d.id ? '保存' : '存为测试题'}</button>
        </div>
      </div>
    </div>
  );
}
