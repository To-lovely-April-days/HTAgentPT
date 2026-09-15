// B2 案例检索：同型归并（FR-8.6）——同一设备型号归为一组，每组先给 3 条，
// 其余折叠展开，避免常见型号的几十条案例把其他型号挤出首屏。
import React, { useCallback, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { post, ApiError } from '../../lib/api';
import { SYNC_PILL_STYLE } from '../../lib/types';
import type { CaseModelGroup, CaseRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';

interface Filters { deviceModel: string; alarmCode: string; keyword: string; }
const EMPTY: Filters = { deviceModel: '', alarmCode: '', keyword: '' };

export default function CasesPage() {
  const nav = useNavigate();
  const [filters, setFilters] = useState<Filters>(EMPTY);
  const [groups, setGroups] = useState<CaseModelGroup[] | null>(null);
  const [expanded, setExpanded] = useState<Record<string, CaseRow[]>>({});
  const [sort, setSort] = useState<'rel' | 'time'>('rel');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = useCallback(async (f: Filters) => {
    setBusy(true);
    setError(null);
    setExpanded({});
    try {
      setGroups(await post<CaseModelGroup[]>('/api/cases/search-grouped', {
        deviceModel: f.deviceModel.trim() || null,
        alarmCode: f.alarmCode.trim() || null,
        keyword: f.keyword.trim() || null,
      }));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '检索失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  }, []);

  useEffect(() => { void run(EMPTY); }, [run]);

  const expand = async (model: string) => {
    const rows = await post<CaseRow[]>('/api/cases/search', {
      deviceModel: model,
      alarmCode: filters.alarmCode.trim() || null,
      keyword: filters.keyword.trim() || null,
      limit: 500,
    });
    setExpanded((e) => ({ ...e, [model]: rows }));
  };

  const total = groups?.reduce((s, g) => s + g.count, 0) ?? 0;
  const byTime = (rows: CaseRow[]) =>
    sort === 'time' ? [...rows].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)) : rows;

  const fl: React.CSSProperties = { fontSize: 11.5, color: 'var(--ink-3)', marginBottom: 5 };
  const fi: React.CSSProperties = { height: 28, width: '100%', padding: '0 9px', background: 'var(--panel)', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };
  const sortTab = (on: boolean): React.CSSProperties => ({
    display: 'inline-flex', alignItems: 'center', height: 24, padding: '0 9px', borderRadius: 4, fontSize: 11.5, cursor: 'pointer',
    ...(on ? { fontWeight: 500, color: 'var(--accent)', background: 'var(--accent-bg)', border: '1px solid var(--accent-line)' }
          : { color: 'var(--ink-2)', background: 'var(--panel)', border: '1px solid #dde3ea' }),
  });

  return (
    <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minHeight: 0, minWidth: 0 }}>
      {/* 检索条 */}
      <div style={{ flexShrink: 0, background: 'var(--panel)', borderBottom: '1px solid var(--line)', padding: '14px 18px 13px' }}>
        <form onSubmit={(e) => { e.preventDefault(); void run(filters); }} style={{ display: 'flex', gap: 12, alignItems: 'flex-end' }}>
          <div style={{ width: 180 }}>
            <div style={fl}>设备型号</div>
            <input style={fi} className="m" value={filters.deviceModel} placeholder="如 CJF-5L"
              onChange={(e) => setFilters({ ...filters, deviceModel: e.target.value })} />
          </div>
          <div style={{ width: 150 }}>
            <div style={fl}>报警代码</div>
            <input style={fi} className="m" value={filters.alarmCode} placeholder="如 E-17"
              onChange={(e) => setFilters({ ...filters, alarmCode: e.target.value })} />
          </div>
          <div style={{ flexGrow: 1 }}>
            <div style={fl}>故障现象关键词</div>
            <input style={fi} value={filters.keyword} placeholder="如：搅拌 停机"
              onChange={(e) => setFilters({ ...filters, keyword: e.target.value })} />
          </div>
          <button type="button" className="gbtn" style={{ height: 28 }} onClick={() => { setFilters(EMPTY); void run(EMPTY); }}>重置</button>
          <button type="submit" className="pbtn" style={{ height: 28, padding: '0 15px' }} disabled={busy}>检索</button>
        </form>
      </div>

      {error && <div style={{ padding: 16 }}><ErrorBox message={error} /></div>}
      {!error && !groups && <div style={{ padding: 40 }}><Spinner text="正在检索…" /></div>}
      {!error && groups && (
        <>
          <div style={{ height: 42, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 12, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
            <span style={{ fontSize: 12.5 }}>命中 <span className="m" style={{ fontWeight: 500 }}>{total}</span> 条</span>
            {groups.length > 1 && <span className="hint">按设备型号归并为 {groups.length} 组，避免同型案例占满结果</span>}
            <div style={{ flexGrow: 1 }} />
            <span className="hint">排序</span>
            <span style={sortTab(sort === 'rel')} onClick={() => setSort('rel')}>相关度</span>
            <span style={sortTab(sort === 'time')} onClick={() => setSort('time')}>时间</span>
          </div>

          <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '12px 18px 16px' }}>
            {groups.length === 0 && (
              <div className="card" style={{ maxWidth: 540, margin: '40px auto', padding: '22px 24px' }}>
                <div style={{ fontSize: 15, fontWeight: 600, marginBottom: 7 }}>没有匹配的案例</div>
                <div style={{ fontSize: 12.5, lineHeight: 1.75, color: 'var(--ink-2)' }}>
                  按型号与报警代码是精确匹配，关键词匹配现象、原因与步骤全文。也可以到「问答」按自然语言描述现象，语义检索能命中措辞不同的案例。
                </div>
              </div>
            )}
            {groups.map((g) => {
              const rows = expanded[g.deviceModel] ?? g.top;
              const rest = g.count - rows.length;
              return (
                <div key={g.deviceModel} className="card" style={{ marginBottom: 12, overflow: 'hidden', padding: 0 }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '12px 15px', borderBottom: '1px solid var(--line-soft)', background: 'var(--bg-soft)' }}>
                    <span className="m" style={{ fontSize: 13, fontWeight: 600 }}>{g.deviceModel}</span>
                    <div style={{ flexGrow: 1 }} />
                    <span className="hint">
                      本公司 <span className="m" style={{ color: 'var(--ink)' }}>{g.ownCount}</span> 条
                      {g.sharedCount > 0 && <> · 共享库 <span className="m" style={{ color: 'var(--ink)' }}>{g.sharedCount}</span> 条</>}
                    </span>
                  </div>
                  {byTime(rows).map((c) => (
                    <div key={c.id} style={{ display: 'flex', alignItems: 'flex-start', gap: 11, padding: '12px 15px', borderBottom: '1px solid var(--line-soft)', cursor: 'pointer' }}
                      onClick={() => nav(`/cases/${c.id}`)}>
                      {c.alarmCode && (
                        <span className="m" style={{ flexShrink: 0, display: 'inline-flex', alignItems: 'center', height: 20, padding: '0 7px', marginTop: 1, borderRadius: 3, fontSize: 11.5, fontWeight: 500, background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' }}>
                          {c.alarmCode}
                        </span>
                      )}
                      <div style={{ flexGrow: 1, minWidth: 0 }}>
                        <div style={{ fontSize: 13, fontWeight: 500, lineHeight: 1.5 }}>{c.phenomenon}</div>
                        <div style={{ fontSize: 11.5, color: 'var(--ink-2)', lineHeight: 1.65, marginTop: 4, display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>{c.result}</div>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 7 }}>
                          <span className="m" style={{ fontSize: 11, color: 'var(--ink-3)' }}>{c.caseNo}</span>
                          <span className="m" style={{ fontSize: 11, color: 'var(--ink-3)' }}>{c.updatedAt.slice(0, 10)}</span>
                          <span className="pill" style={c.sourceCompany
                            ? { background: 'var(--cls-pub-bg)', color: 'var(--cls-pub-fg)', border: '1px solid var(--cls-pub-line)' }
                            : { background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' }}>
                            {c.sourceCompany ? `共享库 · ${c.sourceCompany}` : '本公司'}
                          </span>
                          {!c.sourceCompany && c.syncStatus !== 'Local' && (
                            <span className="pill" style={SYNC_PILL_STYLE[c.syncStatus]}>
                              {{ Pending: '待总部审核', Shared: '已共享', Rejected: '已驳回' }[c.syncStatus as 'Pending' | 'Shared' | 'Rejected']}
                            </span>
                          )}
                        </div>
                      </div>
                    </div>
                  ))}
                  {rest > 0 && (
                    <div style={{ padding: '10px 15px', textAlign: 'center' }}>
                      <button onClick={() => void expand(g.deviceModel)}
                        style={{ fontSize: 12, color: 'var(--accent)', background: 'none', border: 'none', cursor: 'pointer', fontFamily: 'var(--font)' }}>
                        展开其余 {rest} 条
                      </button>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </>
      )}
    </div>
  );
}
