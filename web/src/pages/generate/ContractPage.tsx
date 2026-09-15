// A9 报价合同条款拼装（FR-5.18）：条款类槽位不接受模型生成，仅从已审定条款库按条件选取。
// 这一页没有任何「让 AI 改写这条」的动作——加了就违规。
import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get, post, ApiError } from '../../lib/api';
import type { ClauseAssembly, ClauseRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';

export default function ContractPage() {
  const [category, setCategory] = useState('');
  const [selected, setSelected] = useState<string[]>([]); // 保持点选顺序
  const [assembly, setAssembly] = useState<ClauseAssembly | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const clauses = useQuery({ queryKey: ['clauses-active'], queryFn: () => get<ClauseRow[]>('/api/clauses?status=Active') });
  const categories = useMemo(() => [...new Set((clauses.data ?? []).map((c) => c.category))], [clauses.data]);
  const visible = (clauses.data ?? []).filter((c) => !category || c.category === category);

  const toggle = (id: string) => setSelected((s) => (s.includes(id) ? s.filter((x) => x !== id) : [...s, id]));

  const assemble = async () => {
    setBusy(true);
    setError(null);
    try {
      setAssembly(await post<ClauseAssembly>('/api/generate/contract', { clauseIds: selected }));
    } catch (err) {
      setAssembly(null);
      setError(err instanceof ApiError ? err.message : '拼装失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div style={{ flexGrow: 1, display: 'flex', minHeight: 0, minWidth: 0 }}>
      {/* 左：条款库（仅已审定） */}
      <div style={{ width: 480, flexShrink: 0, borderRight: '1px solid var(--line)', background: 'var(--panel)', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '12px 16px', borderBottom: '1px solid var(--line)' }}>
          <span style={{ fontSize: 13, fontWeight: 600 }}>已审定条款</span>
          <select value={category} onChange={(e) => setCategory(e.target.value)}
            style={{ height: 26, padding: '0 8px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12, fontFamily: 'var(--font)' }}>
            <option value="">全部类目</option>
            {categories.map((c) => <option key={c}>{c}</option>)}
          </select>
          <div style={{ flexGrow: 1 }} />
          <span className="hint">已选 {selected.length}</span>
        </div>
        <div style={{ flexShrink: 0, padding: '10px 16px', background: 'var(--cls-conf-bg)', borderBottom: '1px solid var(--cls-conf-line)' }}>
          <div style={{ fontSize: 11.5, lineHeight: 1.65, color: 'var(--cls-conf-fg)' }}>
            条款不接受模型生成或改写，仅按条件选取。每条带审定人与生效日期；待审定的版本不在此列，审定通过才进入可选范围。
          </div>
        </div>
        <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '10px 16px 16px' }}>
          {clauses.isLoading && <Spinner text="载入条款库…" />}
          {visible.map((c) => {
            const on = selected.includes(c.id);
            return (
              <div key={c.id}
                style={{ border: `1px solid ${on ? 'var(--accent-line)' : 'var(--line)'}`, background: on ? 'var(--accent-bg)' : 'var(--panel)', borderRadius: 5, padding: '10px 12px', marginBottom: 8, cursor: 'pointer' }}
                onClick={() => toggle(c.id)}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 4 }}>
                  <input type="checkbox" checked={on} readOnly />
                  <span className="m pill pill-neutral">{c.code}</span>
                  <span style={{ fontSize: 12.5, fontWeight: 600 }}>{c.title}</span>
                  <span className="pill" style={{ background: 'var(--bg-soft)', color: 'var(--ink-3)', border: '1px solid var(--line-soft)' }}>{c.category}</span>
                </div>
                <div style={{ fontSize: 12, lineHeight: 1.7, color: 'var(--ink-2)', display: '-webkit-box', WebkitLineClamp: 3, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>{c.text}</div>
                <div className="hint" style={{ marginTop: 5 }}>
                  审定 {c.approvedByName ?? '—'}{c.effectiveDate ? ` · ${c.effectiveDate} 生效` : ''}{c.supersedesId ? ' · 替代旧版' : ''}
                </div>
              </div>
            );
          })}
          {!clauses.isLoading && visible.length === 0 && <div className="hint">该类目下没有已审定条款。</div>}
        </div>
        <div style={{ flexShrink: 0, display: 'flex', gap: 8, padding: '12px 16px', borderTop: '1px solid var(--line)', background: 'var(--bg-soft)' }}>
          <button className="gbtn" disabled={selected.length === 0} onClick={() => { setSelected([]); setAssembly(null); }}>清空</button>
          <div style={{ flexGrow: 1 }} />
          <button className="pbtn" style={{ height: 28, padding: '0 14px' }} disabled={busy || selected.length === 0} onClick={() => void assemble()}>
            {busy ? '拼装中…' : `拼装所选 ${selected.length} 条`}
          </button>
        </div>
      </div>

      {/* 右：拼装结果 */}
      <div className="sc" style={{ flexGrow: 1, minWidth: 0, padding: '18px 22px' }}>
        <div style={{ maxWidth: 760 }}>
          <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 4 }}>拼装结果</div>
          <div className="hint" style={{ marginBottom: 14, lineHeight: 1.65 }}>
            按点选顺序拼装为合同条款正文，可整段复制进合同文档。条款正文取自审定锁定的版本——要改内容走「拟修改」，生成待审新版本。
          </div>
          {error && <div style={{ marginBottom: 12 }}><ErrorBox message={error} /></div>}
          {!assembly && !error && <div className="hint">从左侧勾选条款后点「拼装」。</div>}
          {assembly && (
            <>
              <div className="card" style={{ padding: '16px 18px', marginBottom: 12 }}>
                <div style={{ fontSize: 13, lineHeight: 1.9, whiteSpace: 'pre-wrap' }}>{assembly.assembledText}</div>
              </div>
              <button className="gbtn" onClick={() => { void navigator.clipboard.writeText(assembly.assembledText); }}>复制全文</button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
