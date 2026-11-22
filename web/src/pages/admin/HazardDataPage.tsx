// 后台「国家目录」：重点监管危险化工工艺、危险化学品目录（含分类信息表）、易制爆、易制毒。
// 系统自带官方原文；目录更新或要补充时在这里改。改过的条目系统更新不会覆盖，工艺可以「恢复原文」。
import { Fragment, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, get, post, put } from '../../lib/api';
import { Icon } from '../../components/Icons';
import { ErrorBox, Spinner } from '../../components/Common';
import '../../styles/quality.css';

interface Meta { processes: number; chemicals: number; toxic: number; explosive: number; drug: number; edited: number; sources: string[] }
interface Proc {
  id: string; no: number; name: string; reactionType: string; monitorUnits: string; intro: string; hazards: string; typical: string;
  params: string; requirements: string; controls: string; source: string; updatedAt: string; edited: boolean;
}
interface Chem {
  id: string; seq: number; sub: boolean; name: string; aliases: string; english: string | null; cas: string | null; classes: string;
  toxic: boolean; note: string | null; edited: boolean; controlled: string[];
}
interface ChemPage { total: number; page: number; size: number; items: Chem[] }
interface Ctl { id: string; list: 'yzb' | 'yzd'; no: string; name: string; cas: string | null; category: string | null }

const FIELDS: [keyof Proc, string][] = [
  ['params', '重点监控工艺参数'], ['requirements', '安全控制的基本要求'], ['controls', '宜采用的控制方式'],
  ['hazards', '工艺危险特点'], ['typical', '典型工艺'], ['intro', '工艺简介'], ['reactionType', '反应类型'], ['monitorUnits', '重点监控单元'],
];

function ProcEditor({ p, onDone }: { p: Proc; onDone: () => void }) {
  const [v, setV] = useState<Record<string, string>>(() => Object.fromEntries(FIELDS.map(([k]) => [k, String(p[k] ?? '')])));
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const save = async () => {
    setBusy(true); setErr(null);
    try { await put(`/api/hazard-data/processes/${p.id}`, v); onDone(); }
    catch (e) { setErr(e instanceof ApiError ? e.message : '没存上'); }
    finally { setBusy(false); }
  };
  const reset = async () => {
    if (!window.confirm(`把「${p.name}」恢复成系统自带的原文？你改过的内容会丢掉。`)) return;
    setBusy(true);
    try { await post(`/api/hazard-data/processes/${p.id}/reset`, {}); onDone(); }
    catch (e) { setErr(e instanceof ApiError ? e.message : '没恢复成'); }
    finally { setBusy(false); }
  };
  return (
    <div className="hzd-edit">
      {FIELDS.map(([k, label]) => (
        <label key={k}>{label}
          <textarea rows={k === 'reactionType' || k === 'monitorUnits' ? 1 : 4} value={v[k]} onChange={(e) => setV({ ...v, [k]: e.target.value })} />
        </label>
      ))}
      {err && <ErrorBox message={err} />}
      <div className="hzd-row">
        {p.edited && <button type="button" className="gbtn" disabled={busy} onClick={() => void reset()}>恢复原文</button>}
        <span className="hint" style={{ marginRight: 'auto' }}>改过的条目，系统以后更新目录时不会覆盖</span>
        <button type="button" className="gbtn" onClick={onDone}>取消</button>
        <button type="button" className="pbtn" disabled={busy} onClick={() => void save()}>存好</button>
      </div>
    </div>
  );
}

function Processes() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['hz-processes'], queryFn: () => get<Proc[]>('/api/hazard-data/processes') });
  const [open, setOpen] = useState<string | null>(null);
  const [edit, setEdit] = useState<string | null>(null);
  if (q.isPending) return <Spinner text="读取中…" />;
  if (q.isError) return <ErrorBox message={q.error instanceof ApiError ? q.error.message : '读不出来'} />;
  return (
    <div className="hzd-list">
      {q.data.map((p) => (
        <div key={p.id} className={'hzd-item' + (open === p.id ? ' on' : '')}>
          <button type="button" className="hzd-hd" aria-expanded={open === p.id} onClick={() => { setOpen(open === p.id ? null : p.id); setEdit(null); }}>
            <span className="m hint">{p.no}</span><b>{p.name}</b><span className="hint">{p.reactionType} · {p.monitorUnits}</span>
            {p.edited && <span className="pill p-n">公司改过</span>}
            <Icon name="down" size={13} style={{ marginLeft: 'auto', color: 'var(--ink-4)', transform: open === p.id ? 'rotate(180deg)' : undefined }} />
          </button>
          {open === p.id && (edit === p.id
            ? <ProcEditor p={p} onDone={() => { setEdit(null); void qc.invalidateQueries({ queryKey: ['hz-processes'] }); void qc.invalidateQueries({ queryKey: ['hz-meta'] }); }} />
            : (
              <div className="hzd-body">
                <dl className="hz-kv">{FIELDS.slice(0, 5).map(([k, label]) => <Fragment key={k}><dt>{label}</dt><dd>{String(p[k])}</dd></Fragment>)}</dl>
                <div className="hzd-row"><span className="hint" style={{ marginRight: 'auto' }}>来源：{p.source}</span>
                  <button type="button" className="gbtn" onClick={() => setEdit(p.id)}><Icon name="edit" size={13} style={{ verticalAlign: '-2px', marginRight: 4 }} />修改</button></div>
              </div>
            ))}
        </div>
      ))}
    </div>
  );
}

function ChemRow({ c, onSaved }: { c: Chem; onSaved: () => void }) {
  const [edit, setEdit] = useState(false);
  const [v, setV] = useState({ name: c.name, aliases: c.aliases, cas: c.cas ?? '', classes: c.classes, toxic: c.toxic });
  const [err, setErr] = useState<string | null>(null);
  const save = async () => {
    setErr(null);
    try { await put(`/api/hazard-data/chemicals/${c.id}`, v); setEdit(false); onSaved(); }
    catch (e) { setErr(e instanceof ApiError ? e.message : '没存上'); }
  };
  if (edit) {
    return (
      <tr className="hzd-tr-edit"><td colSpan={6}>
        <div className="hzd-edit hzd-edit-chem">
          <label>品名<input value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })} /></label>
          <label>CAS 号<input value={v.cas} onChange={(e) => setV({ ...v, cas: e.target.value })} /></label>
          <label className="wide">别名（用「；」隔开）<input value={v.aliases} onChange={(e) => setV({ ...v, aliases: e.target.value })} /></label>
          <label className="wide">危险性类别（一行一个）<textarea rows={3} value={v.classes} onChange={(e) => setV({ ...v, classes: e.target.value })} /></label>
          <label className="chk"><input type="checkbox" checked={v.toxic} onChange={(e) => setV({ ...v, toxic: e.target.checked })} />剧毒化学品</label>
          {err && <ErrorBox message={err} />}
          <div className="hzd-row wide"><span style={{ marginRight: 'auto' }} /><button type="button" className="gbtn" onClick={() => setEdit(false)}>取消</button>
            <button type="button" className="pbtn" onClick={() => void save()}>存好</button></div>
        </div>
      </td></tr>
    );
  }
  return (
    <tr className={c.sub ? 'sub' : undefined}>
      <td className="m">{c.sub ? '' : c.seq}</td>
      <td><b>{c.name}</b>{c.aliases && <span className="hint"> · {c.aliases}</span>}{c.edited && <span className="pill p-n" style={{ marginLeft: 6 }}>公司改过</span>}</td>
      <td className="m">{c.cas ?? ''}</td>
      <td>{c.classes.split('\n').filter(Boolean).map((x) => <span key={x} className="hzd-cls">{x}</span>)}</td>
      <td>{c.toxic && <span className="hzd-cls bad">剧毒</span>}{c.controlled.map((x) => <span key={x} className="hzd-cls warn">{x}</span>)}</td>
      <td><button type="button" className="hm-link" onClick={() => setEdit(true)}>改</button></td>
    </tr>
  );
}

function Chemicals() {
  const qc = useQueryClient();
  const [q, setQ] = useState('');
  const [term, setTerm] = useState('');
  const [page, setPage] = useState(1);
  const r = useQuery({ queryKey: ['hz-chems', term, page], queryFn: () => get<ChemPage>(`/api/hazard-data/chemicals?q=${encodeURIComponent(term)}&page=${page}`) });
  const pages = r.data ? Math.max(1, Math.ceil(r.data.total / r.data.size)) : 1;
  return (
    <>
      <form className="hzd-search" onSubmit={(e) => { e.preventDefault(); setTerm(q.trim()); setPage(1); }}>
        <Icon name="search" size={14} />
        <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="品名、别名、英文名、CAS 号或序号" aria-label="搜危险化学品" />
        <button type="submit" className="gbtn">查</button>
        {r.data && <span className="hint">{r.data.total} 条</span>}
      </form>
      {r.isPending ? <Spinner text="读取中…" /> : r.isError ? <ErrorBox message="读不出来" /> : (
        <div className="hzd-tbl-w">
          <table className="hzd-tbl">
            <thead><tr><th>序号</th><th>品名</th><th>CAS</th><th>危险性类别</th><th>管控</th><th /></tr></thead>
            <tbody>{r.data.items.map((c) => <ChemRow key={c.id} c={c} onSaved={() => void qc.invalidateQueries({ queryKey: ['hz-chems'] })} />)}</tbody>
          </table>
        </div>
      )}
      {pages > 1 && (
        <div className="hzd-row" style={{ justifyContent: 'center' }}>
          <button type="button" className="gbtn" disabled={page <= 1} onClick={() => setPage(page - 1)}>上一页</button>
          <span className="hint">{page} / {pages}</span>
          <button type="button" className="gbtn" disabled={page >= pages} onClick={() => setPage(page + 1)}>下一页</button>
        </div>
      )}
    </>
  );
}

function Controlled() {
  const q = useQuery({ queryKey: ['hz-ctl'], queryFn: () => get<Ctl[]>('/api/hazard-data/controlled') });
  if (q.isPending) return <Spinner text="读取中…" />;
  if (q.isError) return <ErrorBox message="读不出来" />;
  return (
    <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap', alignItems: 'flex-start' }}>
      {(['yzb', 'yzd'] as const).map((l) => (
        <div key={l} className="hzd-tbl-w" style={{ flex: '1 1 420px' }}>
          <table className="hzd-tbl">
            <thead><tr><th colSpan={3}>{l === 'yzb' ? '易制爆危险化学品名录（2017 年版）' : '易制毒化学品分类和品种目录'}</th></tr></thead>
            <tbody>{q.data.filter((x) => x.list === l).map((x) => (
              <tr key={x.id}><td className="m">{l === 'yzb' ? x.no : x.no.replace(/^第.类-/, '')}</td><td>{x.name}{x.category && <span className="hint"> · {x.category}</span>}</td><td className="m">{x.cas ?? ''}</td></tr>
            ))}</tbody>
          </table>
        </div>
      ))}
    </div>
  );
}

export default function HazardDataPage() {
  const meta = useQuery({ queryKey: ['hz-meta'], queryFn: () => get<Meta>('/api/hazard-data/meta') });
  const [tab, setTab] = useState<'p' | 'c' | 'k'>('p');
  const m = meta.data;
  return (
    <>
      <div className="qy-tabs">
        <b>国家目录</b>
        <span className="hint">助手「查危险工艺和危化品」用的本地数据，系统自带官方原文；目录更新或要补充时在这里改</span>
      </div>
      <div className="qy">
        {m && (
          <div className="qy-kpis">
            <div className="qy-kpi"><span>重点监管工艺</span><b>{m.processes}</b><span>类</span></div>
            <div className="qy-kpi"><span>危险化学品</span><b>{m.chemicals}</b><span>其中剧毒 {m.toxic} 种</span></div>
            <div className="qy-kpi"><span>易制爆 / 易制毒</span><b>{m.explosive} / {m.drug}</b><span>种</span></div>
            <div className="qy-kpi"><span>公司改过</span><b>{m.edited}</b><span>条</span></div>
          </div>
        )}
        {m && <details className="hzd-src"><summary>数据来源</summary><ul>{m.sources.map((x) => <li key={x}>{x}</li>)}</ul></details>}
        <div className="qy-filters">
          {([['p', '重点监管危险化工工艺'], ['c', '危险化学品目录'], ['k', '易制爆、易制毒']] as const).map(([k, label]) => (
            <button key={k} type="button" className={'q-chip' + (tab === k ? ' on' : '')} onClick={() => setTab(k)}>{label}</button>
          ))}
        </div>
        {tab === 'p' ? <Processes /> : tab === 'c' ? <Chemicals /> : <Controlled />}
      </div>
    </>
  );
}
