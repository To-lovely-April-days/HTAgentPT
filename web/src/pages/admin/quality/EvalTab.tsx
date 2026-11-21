// 「测试题与准确率」：测试题列表、跑一遍、每次的准确率和中间改了什么、这次比上次变好变差的题。
import { useEffect, useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, del, ApiError } from '../../../lib/api';
import { Icon } from '../../../components/Icons';
import { Spinner } from '../../../components/Common';
import { pct, when } from './types';
import type { EvalQuestion, EvalResultRow, EvalRunDetail, EvalRunRow } from './types';
import { QuestionForm } from './QuestionForm';
import { useEscape } from './useEscape';
import type { QuestionDraft } from './QuestionForm';

const SRC: Record<string, string> = { issue: '错题', import: '导入', manual: '手动' };

export function EvalTab() {
  const qc = useQueryClient();
  const runs = useQuery({
    queryKey: ['eval-runs'], queryFn: () => get<EvalRunRow[]>('/api/quality/eval/runs'),
    refetchInterval: (q) => (q.state.data?.some((r) => r.status === 'Running') ? 3000 : false),
  });
  const questions = useQuery({ queryKey: ['eval-questions'], queryFn: () => get<EvalQuestion[]>('/api/quality/eval/questions') });
  const running = runs.data?.find((r) => r.status === 'Running');
  const done = (runs.data ?? []).filter((r) => r.status === 'Done');
  const [runId, setRunId] = useState<string | null>(null);
  const shownRun = runId ?? done[0]?.id ?? null;
  const detail = useQuery({ queryKey: ['eval-run', shownRun], enabled: !!shownRun, queryFn: () => get<EvalRunDetail>(`/api/quality/eval/runs/${shownRun}`) });
  const [form, setForm] = useState<QuestionDraft | null>(null);
  const [importing, setImporting] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  // 按哪一档跑：同一批题快速、标准、深度各跑一遍，比准确率和用时
  const [level, setLevel] = useState('auto');
  // 跑完一轮，题目的对错历史也刷一下
  const latestDone = done[0]?.id;
  useEffect(() => { if (latestDone) void qc.invalidateQueries({ queryKey: ['eval-questions'] }); }, [latestDone, qc]);

  const start = async () => {
    setMsg(null);
    try { await post('/api/quality/eval/runs', { level }); setRunId(null); void qc.invalidateQueries({ queryKey: ['eval-runs'] }); }
    catch (e) { setMsg(e instanceof ApiError ? e.message : '没能开始'); }
  };
  const qs = questions.data ?? [];
  const lastRun = runs.data?.[0];

  return (
    <div className="qy">
      <div className="ev-hd">
        <div>
          <b>{qs.length} 道测试题</b>
          <span className="hint">
            {lastRun ? `上次跑：${when(lastRun.startedAt)} · ${lastRun.finishedAt ? `用时 ${Math.max(1, Math.round((+new Date(lastRun.finishedAt) - +new Date(lastRun.startedAt)) / 60000))} 分钟 · ` : ''}账号 ${lastRun.runAs}` : '还没跑过'}
          </span>
        </div>
        {msg && <span className="q-err">{msg}</span>}
        <button type="button" className="gbtn" onClick={() => setImporting(true)}>导入题目</button>
        <button type="button" className="gbtn" onClick={() => setForm({ question: '', expectDocs: [], expectProjects: [], expectPoints: [], expectAsk: false, dept: '' })}>加一题</button>
        <label className="ev-level" title="同一批题按不同档位各跑一遍，比准确率和用时">按
          <select value={level} onChange={(e) => setLevel(e.target.value)} disabled={!!running}>
            <option value="auto">默认档位</option><option value="fast">快速</option><option value="standard">标准</option><option value="deep">深度</option>
          </select>跑</label>
        <button type="button" className="pbtn ev-run" disabled={!!running || qs.length === 0} onClick={() => void start()}>
          <Icon name="play" size={11} />{running ? '正在跑…' : '跑一遍'}
        </button>
      </div>

      {running && (
        <div className="ev-progress">
          <span>正在用 {running.runAs} 的账号挨个问：{running.finished} / {running.total}</span>
          <span className="qy-bar wide"><i style={{ width: `${running.total ? (running.finished / running.total) * 100 : 0}%` }} /></span>
          <span className="hint">每题开一个临时对话，问完就删，不进历史和统计</span>
        </div>
      )}
      {lastRun?.status === 'Failed' && <div className="ev-failed">上一次没跑完：{lastRun.error ?? '出错'}</div>}

      <div className="ev-top">
        <div className="ev-card ev-chart">
          <div className="qy-sec"><b>准确率</b><span>每跑一次一个点</span></div>
          <Chart runs={done.slice(0, 12).reverse()} selected={shownRun} onPick={setRunId} />
          <div className="ev-legend"><span><i className="l1" />查得准：该查到的资料查到了</span><span><i className="l2" />答得全：要点都说到了</span></div>
        </div>
        <div className="ev-card ev-runs">
          <div className="qy-sec"><b>每次跑之前改了什么</b><span>系统自动记</span></div>
          <div className="ev-runs-h"><span>时间</span><span>查得准</span><span>答得全</span><span>改了什么</span></div>
          {done.slice(0, 8).map((r) => (
            <button key={r.id} type="button" className={'ev-runs-r' + (r.id === shownRun ? ' sel' : '')} onClick={() => setRunId(r.id)}>
              <span>{when(r.startedAt)}</span><span>{fmt(pct(r.retrievalOk, r.retrievalTotal))}</span><span>{fmt(pct(r.answerOk, r.answerTotal))}</span>
              <span title={r.changes ?? ''}>{r.changes ? r.changes.split('\n').join('；') : '—'}</span>
            </button>
          ))}
          {done.length === 0 && <div className="qy-empty">跑过一次以后这里会列出来。</div>}
        </div>
      </div>

      <div className="ev-bottom">
        {detail.data && <Compare run={detail.data} />}
        <Questions qs={qs} run={detail.data} loading={questions.isLoading}
          onEdit={(q) => setForm({ id: q.id, question: q.question, expectDocs: q.expectDocs, expectProjects: q.expectProjects, expectPoints: q.expectPoints, expectAsk: q.expectAsk, dept: q.dept ?? '' })}
          onRemove={async (q) => { await del(`/api/quality/eval/questions/${q.id}`); void qc.invalidateQueries({ queryKey: ['eval-questions'] }); }} />
      </div>

      {form && <QuestionForm draft={form} onClose={() => setForm(null)}
        onSaved={(code) => { setForm(null); setMsg(code ? `已加 ${code}` : '已保存'); void qc.invalidateQueries({ queryKey: ['eval-questions'] }); }} />}
      {importing && <ImportBox onClose={() => setImporting(false)} onDone={(n) => { setImporting(false); setMsg(`导入了 ${n} 题`); void qc.invalidateQueries({ queryKey: ['eval-questions'] }); }} />}
    </div>
  );
}

const fmt = (v: number | null) => (v == null ? '—' : `${v}%`);

function Chart({ runs, selected, onPick }: { runs: EvalRunRow[]; selected: string | null; onPick: (id: string) => void }) {
  const W = 460, H = 150, x0 = 36, y0 = 10, bottom = 22;
  if (runs.length === 0) return <div className="qy-empty" style={{ height: H }}>还没跑过。</div>;
  const vals = runs.flatMap((r) => [pct(r.retrievalOk, r.retrievalTotal), pct(r.answerOk, r.answerTotal)]).filter((v): v is number => v != null);
  const lo = Math.max(0, Math.floor((Math.min(...vals, 100) - 5) / 10) * 10);
  const px = (i: number) => (runs.length === 1 ? (x0 + W - 10) / 2 : x0 + (i * (W - x0 - 20)) / (runs.length - 1));
  const py = (v: number) => y0 + ((100 - v) / (100 - lo || 1)) * (H - y0 - bottom);
  const line = (key: 'r' | 'a') => runs.map((r, i) => {
    const v = key === 'r' ? pct(r.retrievalOk, r.retrievalTotal) : pct(r.answerOk, r.answerTotal);
    return v == null ? null : `${px(i).toFixed(1)},${py(v).toFixed(1)}`;
  }).filter(Boolean).join(' ');
  const ticks = [lo, lo + (100 - lo) / 2, 100].map((v) => Math.round(v));
  return (
    <svg className="ev-svg" viewBox={`0 0 ${W} ${H}`} role="img" aria-label="每次跑分的查得准和答得全">
      {ticks.map((v) => (
        <g key={v}><line x1={x0} x2={W - 10} y1={py(v)} y2={py(v)} className="grid" /><text x={x0 - 6} y={py(v) + 4} textAnchor="end">{v}%</text></g>
      ))}
      <polyline points={line('a')} className="l2" />
      <polyline points={line('r')} className="l1" />
      {runs.map((r, i) => {
        const a = pct(r.retrievalOk, r.retrievalTotal), b = pct(r.answerOk, r.answerTotal);
        return (
          <g key={r.id} className={'pt' + (r.id === selected ? ' sel' : '')} onClick={() => onPick(r.id)}>
            <rect x={px(i) - 14} y={y0} width={28} height={H - y0} fill="transparent" />
            {a != null && <circle cx={px(i)} cy={py(a)} r={3.5} className="d1"><title>{`${when(r.startedAt)} 查得准 ${a}%`}</title></circle>}
            {b != null && <circle cx={px(i)} cy={py(b)} r={3.5} className="d2"><title>{`${when(r.startedAt)} 答得全 ${b}%`}</title></circle>}
            <text x={px(i)} y={H - 4} textAnchor="middle">{when(r.startedAt).replace(/^今天 |^昨天 /, '')}</text>
          </g>
        );
      })}
    </svg>
  );
}

function Compare({ run }: { run: EvalRunDetail }) {
  const rs = run.results;
  const better = rs.filter((r) => r.trend === 'better').length;
  const worse = rs.filter((r) => r.trend === 'worse').length;
  const failing = rs.filter((r) => r.retrievalOk === false || r.answerOk === false || r.error);
  const noDocs = failing.filter((r) => !r.error && r.retrievalOk === false).length;
  const notAnswered = failing.filter((r) => !r.error && r.retrievalOk !== false && r.answerOk === false).length;
  const errors = failing.filter((r) => r.error).length;
  return (
    <div className="ev-card ev-compare">
      <div className="qy-sec"><b>这次{run.previousRunId ? '和上次比' : ''}</b><span>{when(run.startedAt)}</span></div>
      {run.previousRunId ? (
        <>
          <div className="ev-cmp ok">变好 {better} 题</div>
          <div className="ev-cmp bad">变差 {worse} 题</div>
        </>
      ) : <div className="hint">第一次跑，下次起和这次比。</div>}
      <div className="ev-cmp">还错 {failing.length} 题</div>
      {failing.length > 0 && (
        <div className="ev-why">
          <span className="hint">错在哪</span>
          <div><span>该查到的资料没查到</span><b>{noDocs}</b></div>
          <div><span>查到了，没答全</span><b>{notAnswered}</b></div>
          {errors > 0 && <div><span>出错 / 超时</span><b>{errors}</b></div>}
        </div>
      )}
      {run.changes && <div className="ev-changes"><span className="hint">这次之前改了</span>{run.changes.split('\n').map((c) => <div key={c}>{c}</div>)}</div>}
    </div>
  );
}

function Mark({ v }: { v: boolean | null }) {
  if (v == null) return <span className="ev-mark na">—</span>;
  return <span className={'ev-mark ' + (v ? 'y' : 'n')} aria-label={v ? '对' : '错'}><Icon name={v ? 'check' : 'x'} size={12} stroke={2.4} /></span>;
}

function Questions({ qs, run, loading, onEdit, onRemove }: {
  qs: EvalQuestion[]; run?: EvalRunDetail; loading: boolean; onEdit: (q: EvalQuestion) => void; onRemove: (q: EvalQuestion) => Promise<void>;
}) {
  const [filter, setFilter] = useState<'all' | 'wrong' | 'worse'>('all');
  const [open, setOpen] = useState<string | null>(null);
  const byQ = useMemo(() => new Map((run?.results ?? []).map((r) => [r.questionId, r])), [run]);
  const wrong = (r?: EvalResultRow) => !!r && (r.retrievalOk === false || r.answerOk === false || !!r.error);
  const shown = qs.filter((q) => filter === 'all' || (filter === 'wrong' ? wrong(byQ.get(q.id)) : byQ.get(q.id)?.trend === 'worse'));
  return (
    <div className="ev-card ev-qs">
      <div className="ev-qs-hd">
        <b>题目</b>
        <button type="button" className={'q-chip' + (filter === 'all' ? ' on' : '')} onClick={() => setFilter('all')}>全部 {qs.length}</button>
        <button type="button" className={'q-chip' + (filter === 'wrong' ? ' on' : '')} onClick={() => setFilter('wrong')}>这次错的 {qs.filter((q) => wrong(byQ.get(q.id))).length}</button>
        <button type="button" className={'q-chip' + (filter === 'worse' ? ' on' : '')} onClick={() => setFilter('worse')}>变差的 {(run?.results ?? []).filter((r) => r.trend === 'worse').length}</button>
      </div>
      <div className="ev-qrow h"><span>题号</span><span>问题</span><span>部门</span><span>来源</span><span>查</span><span>答</span><span>最近几次</span></div>
      {loading && <div style={{ padding: 16 }}><Spinner text="正在载入…" /></div>}
      {shown.map((q) => {
        const r = byQ.get(q.id);
        return (
          <div key={q.id}>
            <button type="button" className={'ev-qrow' + (open === q.id ? ' sel' : '')} onClick={() => setOpen(open === q.id ? null : q.id)} aria-expanded={open === q.id}>
              <span className="m">{q.code}</span><span className="ev-q">{q.question}</span><span>{q.dept ?? ''}</span><span>{SRC[q.source] ?? q.source}</span>
              <span>{r ? <Mark v={r.retrievalOk} /> : ''}</span><span>{r ? <Mark v={r.answerOk} /> : ''}</span>
              <span className="ev-hist">{q.history.map((h, i) => <i key={i} className={h.retrievalOk === false || h.answerOk === false ? 'n' : 'y'} />)}</span>
            </button>
            {open === q.id && (
              <div className="ev-qd">
                <div><span className="hint">应该查到</span>{[...q.expectDocs, ...q.expectProjects].join('、') || '—'}</div>
                <div><span className="hint">要点</span>{q.expectPoints.join('；') || '—'}{q.expectAsk ? '（应先问清楚）' : ''}</div>
                {r && <>
                  {r.error && <div className="q-err">{r.error}</div>}
                  <div><span className="hint">这次查到</span>{r.foundDocs.join('、') || '什么都没查到'}</div>
                  {r.missingPoints.length > 0 && <div><span className="hint">没说到</span>{r.missingPoints.join('；')}</div>}
                  {r.answer && <div className="ev-ans">{r.answer.length > 600 ? r.answer.slice(0, 600) + '…' : r.answer}</div>}
                </>}
                <div className="ev-qd-act">
                  <button type="button" className="gbtn" onClick={() => onEdit(q)}>改这题</button>
                  <button type="button" className="gbtn" onClick={() => { if (confirm(`删掉测试题 ${q.code}？以前跑的结果也不再显示。`)) void onRemove(q); }}>删掉</button>
                </div>
              </div>
            )}
          </div>
        );
      })}
      {!loading && shown.length === 0 && <div className="qy-empty">{qs.length === 0 ? '还没有测试题：从「答错的问题」里存，或者导入一批。' : '没有符合的题。'}</div>}
    </div>
  );
}

function ImportBox({ onClose, onDone }: { onClose: () => void; onDone: (n: number) => void }) {
  const [text, setText] = useState('');
  const [errors, setErrors] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  useEscape(onClose);
  const pick = async (f: File | undefined) => { if (f) setText(await f.text()); };
  const go = async () => {
    setBusy(true); setErrors([]);
    try {
      const r = await post<{ added: number; errors: string[] }>('/api/quality/eval/questions/import', { text });
      if (r.errors.length) setErrors([`导入了 ${r.added} 题，以下几行没导进去：`, ...r.errors]); else onDone(r.added);
    } catch (e) { setErrors([e instanceof ApiError ? e.message : '导入失败']); } finally { setBusy(false); }
  };
  return (
    <div className="q-mask" role="dialog" aria-modal="true" aria-label="导入题目" onClick={onClose}>
      <div className="q-dlg" onClick={(e) => e.stopPropagation()}>
        <div className="q-dlg-hd"><b>导入题目</b><button type="button" className="q-x" aria-label="关闭" onClick={onClose}><Icon name="x" size={14} /></button></div>
        <p className="hint" style={{ lineHeight: 1.7 }}>
          一行一题的 JSON，和 deploy/eval/questions.example.jsonl 一样：q 问题，expect_docs 应该查到的资料，expect_projects 项目号，
          expect_points 要点（几种说法用 | 隔开），expect_ask 是否应先问清楚，dept 部门。// 开头的行跳过。
        </p>
        <input type="file" accept=".jsonl,.json,.txt" onChange={(e) => void pick(e.target.files?.[0])} />
        <textarea className="ev-import" rows={10} value={text} onChange={(e) => setText(e.target.value)}
          placeholder={'{"q": "16 公斤的釜能不能做 200 度的氢化", "expect_points": ["1.6 MPa|1.57 MPa"], "dept": "技术部"}'} />
        {errors.map((e) => <div key={e} className="q-err">{e}</div>)}
        <div className="q-dlg-ft">
          <button type="button" className="gbtn" onClick={onClose}>取消</button>
          <button type="button" className="pbtn" disabled={busy || !text.trim()} onClick={() => void go()}>{busy ? '正在导入…' : '导入'}</button>
        </div>
      </div>
    </div>
  );
}
