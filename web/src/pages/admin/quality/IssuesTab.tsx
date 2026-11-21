// 「答错的问题」：左边列表（员工点了没帮上的 + 系统发现的），右边还原当时怎么查的、错在哪一步、对应的修法。
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from 'react-router-dom';
import { get, post, put, ApiError } from '../../../lib/api';
import { Icon } from '../../../components/Icons';
import { Spinner, ErrorBox } from '../../../components/Common';
import { REASON_LABEL } from '../../chat/feedbackReasons';
import { CATEGORIES, SOURCE_LABEL, STATUS_LABEL, looksEmpty, when } from './types';
import type { IssueDetail, IssueRow, PayloadCard, QualitySummary } from './types';
import { QuestionForm } from './QuestionForm';
import type { QuestionDraft } from './QuestionForm';

export function IssuesTab() {
  const [status, setStatus] = useState<'open' | 'done' | 'all'>('open');
  const [source, setSource] = useState('');
  const [reason, setReason] = useState('');
  const [sel, setSel] = useState<string | null>(null);
  const summary = useQuery({ queryKey: ['quality-summary'], queryFn: () => get<QualitySummary>('/api/quality/summary?days=7') });
  const list = useQuery({
    queryKey: ['quality-issues', status, source, reason],
    queryFn: () => get<IssueRow[]>(`/api/quality/issues?status=${status}${source ? `&source=${source}` : ''}${reason ? `&reason=${reason}` : ''}`),
  });
  const rows = list.data ?? [];
  // 选中的那条不在当前列表里（换了筛选、刚处理完）就看第一条
  const current = sel && rows.some((r) => r.id === sel) ? sel : rows[0]?.id ?? null;
  const idx = rows.findIndex((r) => r.id === current);
  const s = summary.data;

  return (
    <div className="qy">
      {s && (
        <div className="qy-kpis">
          <Kpi k="这 7 天回答" v={s.answers} d={`${s.users} 人在用`} />
          <Kpi k="有帮助" v={s.helpful} d={s.helpful + s.notHelpful > 0 ? `点了的人里 ${Math.round((s.helpful / (s.helpful + s.notHelpful)) * 100)}%` : '还没人点'} tone="ok" />
          <Kpi k="没帮上" v={s.notHelpful} d={s.prevNotHelpful === s.notHelpful ? '和上 7 天一样' : `比上 7 天${s.notHelpful < s.prevNotHelpful ? '少' : '多'} ${Math.abs(s.notHelpful - s.prevNotHelpful)}`} tone="bad" />
          <Kpi k="系统发现" v={s.noEvidence + s.unverified} d={`查了没用上 ${s.noEvidence} · 数字对不上 ${s.unverified}`} />
          <Kpi k="待处理" v={s.open} d={s.oldestOpen ? `最早的 ${when(s.oldestOpen)}` : '都处理完了'} />
        </div>
      )}
      <div className="qy-split">
        <div className="qy-list">
          <div className="qy-filters">
            {(['open', 'done', 'all'] as const).map((k) => (
              <button key={k} type="button" className={'q-chip' + (status === k ? ' on' : '')} onClick={() => setStatus(k)}>
                {k === 'open' ? `待处理${s ? ` ${s.open}` : ''}` : k === 'done' ? '已处理' : '全部'}
              </button>
            ))}
            <select aria-label="来源" value={source} onChange={(e) => setSource(e.target.value)}>
              <option value="">全部来源</option><option value="feedback">员工反馈</option><option value="system">系统发现</option>
            </select>
            <select aria-label="原因" value={reason} onChange={(e) => setReason(e.target.value)}>
              <option value="">全部原因</option>
              {Object.entries(REASON_LABEL).map(([c, l]) => <option key={c} value={c}>{l}</option>)}
            </select>
          </div>
          {list.isLoading && <div style={{ padding: 20 }}><Spinner text="正在载入…" /></div>}
          {list.isError && <ErrorBox message="列表没载入" />}
          {rows.map((r) => (
            <button key={r.id} type="button" className={'qy-row' + (r.id === current ? ' sel' : '')} onClick={() => setSel(r.id)}>
              <span className="qy-q">{r.question}</span>
              <span className="qy-who">{r.source === 'Feedback' ? <>{r.person ?? '—'}<i>{r.dept ?? ''}</i></> : <em>系统发现</em>}</span>
              <span className="qy-why"><span className={'pill ' + (r.source === 'Feedback' ? 'pill-neutral' : 'qy-pill-warn')}>
                {r.source === 'Feedback' ? REASON_LABEL[r.reason ?? 'other'] ?? '其他' : SOURCE_LABEL[r.source]}</span></span>
              <span className="qy-when">{when(r.createdAt)}</span>
              <span className={'pill ' + (r.status === 'Open' ? 'qy-pill-open' : 'qy-pill-ok')}>{r.evalQuestionId && r.status !== 'Open' ? '已存测试题' : STATUS_LABEL[r.status]}</span>
            </button>
          ))}
          {!list.isLoading && rows.length === 0 && <div className="qy-empty">{status === 'open' ? '没有待处理的。' : '这里还没有。'}</div>}
          <p className="qy-foot">「系统发现」：查过资料但一条都没用上、或者回答里的数字在原文里核对不上的，没人点也会进来。</p>
        </div>
        {current ? <Detail key={current} id={current} pos={idx} total={rows.length}
          onPrev={idx > 0 ? () => setSel(rows[idx - 1].id) : undefined}
          onNext={idx >= 0 && idx < rows.length - 1 ? () => setSel(rows[idx + 1].id) : undefined} />
          : <div className="qy-detail qy-empty">左边点一条看详情。</div>}
      </div>
    </div>
  );
}

function Kpi({ k, v, d, tone }: { k: string; v: number; d: string; tone?: 'ok' | 'bad' }) {
  return <div className="qy-kpi"><span>{k}</span><b className={tone ?? ''}>{v}</b><span>{d}</span></div>;
}

function Detail({ id, pos, total, onPrev, onNext }: { id: string; pos: number; total: number; onPrev?: () => void; onNext?: () => void }) {
  const qc = useQueryClient();
  const nav = useNavigate();
  const q = useQuery({ queryKey: ['quality-issue', id], queryFn: () => get<IssueDetail>(`/api/quality/issues/${id}`) });
  const [category, setCategory] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [form, setForm] = useState<QuestionDraft | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  if (q.isLoading) return <div className="qy-detail"><Spinner text="正在载入…" /></div>;
  if (!q.data) return <div className="qy-detail"><ErrorBox message="这一条没载入" /></div>;
  const d = q.data;
  const cat = category ?? d.category;
  const p = d.payload;
  const cards: (PayloadCard & { used: boolean })[] = [
    ...(p?.evidence?.used ?? []).map((c) => ({ ...c, used: true })),
    ...(p?.evidence?.other ?? []).map((c) => ({ ...c, used: false })),
  ].filter((c) => c.kind === 'doc').sort((a, b) => (b.score ?? -1) - (a.score ?? -1));
  const zeroSearches = (p?.searches ?? []).filter((x) => x.found === 0);

  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['quality-issue', id] });
    void qc.invalidateQueries({ queryKey: ['quality-issues'] });
    void qc.invalidateQueries({ queryKey: ['quality-summary'] });
  };
  const update = async (body: { category?: string | null; status?: string; note?: string | null }, done: string) => {
    setBusy(true); setMsg(null);
    try { await put(`/api/quality/issues/${id}`, body); setMsg(done); refresh(); }
    catch (e) { setMsg(e instanceof ApiError ? e.message : '没改成'); } finally { setBusy(false); }
  };
  const pickCategory = (c: string) => { setCategory(c); void update({ category: c }, ''); };

  return (
    <div className="qy-detail">
      <div className="qy-dh">
        <span className={'pill ' + (d.status === 'Open' ? 'qy-pill-open' : 'qy-pill-ok')}>{STATUS_LABEL[d.status]}</span>
        <span className="hint">{when(d.createdAt)}{pos >= 0 ? ` · 第 ${pos + 1} / ${total} 条` : ''}</span>
        <span className="qy-nav">
          <button type="button" className="gbtn" disabled={!onPrev} onClick={onPrev}>上一条</button>
          <button type="button" className="gbtn" disabled={!onNext} onClick={onNext}>下一条</button>
        </span>
      </div>

      <div className="qy-ask">{d.question}</div>
      <Answer text={d.answer ?? ''} />

      {d.source === 'Feedback' ? (
        <div className="qy-who-box">
          <b>{d.person ?? '提问人'}</b><span className="hint">{d.dept ? `${d.dept} · ` : ''}说「{REASON_LABEL[d.reason ?? 'other'] ?? '其他'}」{d.notify ? ' · 要处理完告诉 TA' : ''}</span>
          {d.comment && <p>「{d.comment}」</p>}
        </div>
      ) : (
        <div className="qy-who-box"><b>系统发现</b><span className="hint">{SOURCE_LABEL[d.source]} · 提问人 {d.person ?? '—'}{d.dept ? `（${d.dept}）` : ''}</span></div>
      )}

      {p?.steps && p.steps.length > 0 && (
        <section>
          <div className="qy-sec"><b>当时怎么查的</b><span>查了 {p.steps.length} 步{p.seconds ? ` · 用时 ${Math.round(p.seconds)} 秒` : ''}</span></div>
          {p.steps.map((s) => {
            const bad = looksEmpty(s);
            return (
              <div key={s.id} className="qy-step">
                <Icon name={bad ? 'x' : 'check'} size={14} stroke={2.2} className={bad ? 'bad' : 'ok'} />
                <b>{s.label}</b><code>{s.detail ?? ''}</code><span>{s.result ?? ''}</span>
              </div>
            );
          })}
        </section>
      )}

      {(cards.length > 0 || zeroSearches.length > 0) && (
        <section>
          <div className="qy-sec"><b>搜到的资料</b><span>按相关度排；低于 {d.threshold.toFixed(2)} 的算「没找到」，不给模型看</span></div>
          {zeroSearches.map((x, i) => (
            <div key={'z' + i} className={'qy-hit' + (x.top > 0 && x.top >= d.threshold - 0.1 ? ' near' : '')}>
              <span className="qy-hit-t">搜「{x.query}」{x.hints && x.hints.length > 0 ? <i>最接近的：{x.hints.slice(0, 3).join('、')}</i> : null}</span>
              <Bar v={x.top} /><code>{x.top.toFixed(2)}</code><span className="qy-hit-u">{x.top > 0 ? '低于门槛' : '没搜到'}</span>
            </div>
          ))}
          {cards.slice(0, 8).map((c) => (
            <div key={c.n} className="qy-hit">
              <span className="qy-hit-t"><b>{c.title}</b><i>{[c.section, c.pageNo != null ? `第 ${c.pageNo} 页` : null].filter(Boolean).join(' · ')}</i></span>
              {c.score != null ? <><Bar v={c.score} /><code>{c.score.toFixed(2)}</code></> : <span className="qy-hit-na">打开原文</span>}
              <span className="qy-hit-u">{c.used ? '用上了' : '没用上'}</span>
            </div>
          ))}
        </section>
      )}

      {d.facts.length > 0 && (
        <section>
          <div className="qy-sec"><b>系统看出来的</b><span>按这条回答的记录核对，不是猜的</span></div>
          {d.facts.map((f, i) => <div key={i} className="qy-fact"><span>{i + 1}</span>{f}</div>)}
        </section>
      )}

      <section>
        <div className="qy-sec"><b>错在哪一步</b><span>选一个，下面给对应的修法</span></div>
        <div className="q-chips">
          {CATEGORIES.map((c) => (
            <button key={c.code} type="button" className={'q-chip' + (cat === c.code ? ' on' : '')} disabled={busy}
              onClick={() => pickCategory(c.code)}>{c.label}</button>
          ))}
        </div>
        {cat && <Fix category={cat} detail={d} cards={cards} onReask={() => nav('/chat', { state: { ask: d.question } })} />}
      </section>

      <div className="qy-ft">
        <button type="button" className="gbtn" disabled={!!d.evalQuestionId} onClick={() => setForm({
          question: d.question, expectDocs: [], expectProjects: [], expectPoints: [], expectAsk: false, dept: d.dept ?? '', issueId: d.id,
          note: d.source === 'Feedback' ? `来自 ${d.person ?? '员工'} 的反馈` : '来自系统发现',
          // 当时搜到的资料，加上低于门槛没给模型看的「最接近的」那几份
          candidates: [...new Set([...zeroSearches.flatMap((x) => x.hints ?? []), ...cards.map((c) => c.title)])].slice(0, 6),
        })}>{d.evalQuestionId ? '已存为测试题' : '存为测试题'}</button>
        {d.notify && d.status === 'Open' && (
          <input className="qy-note" placeholder="处理说明，会告诉提问人（比如：加上了「华理」的叫法）"
            value={note ?? d.resolveNote ?? ''} onChange={(e) => setNote(e.target.value)} />
        )}
        {msg && <span className="hint">{msg}</span>}
        <span className="qy-ft-r">
          {d.status === 'Open' ? (
            <>
              <button type="button" className="gbtn" disabled={busy} onClick={() => void update({ status: 'dismissed', category: cat ?? 'not_issue' }, '已标为不是问题')}>不是问题</button>
              <button type="button" className="pbtn" disabled={busy} onClick={() => void update({ status: 'resolved', note: note ?? d.resolveNote }, d.notify ? '已处理，会告诉提问人' : '已处理')}>标为已处理</button>
            </>
          ) : (
            <button type="button" className="gbtn" disabled={busy} onClick={() => void update({ status: 'open' }, '已改回待处理')}>改回待处理</button>
          )}
        </span>
      </div>
      {form && <QuestionForm draft={form} onClose={() => setForm(null)} onSaved={(code) => { setForm(null); setMsg(`已存为测试题 ${code}`); refresh(); }} />}
    </div>
  );
}

function Answer({ text }: { text: string }) {
  const [open, setOpen] = useState(false);
  const long = text.length > 360;
  return (
    <div className="qy-ans">
      {long && !open ? text.slice(0, 360) + '…' : text || '（没有回答正文）'}
      {long && <button type="button" className="q-link" onClick={() => setOpen(!open)}>{open ? '收起' : '展开全文'}</button>}
    </div>
  );
}

function Bar({ v }: { v: number }) {
  return <span className="qy-bar" aria-hidden="true"><i style={{ width: `${Math.max(0, Math.min(1, v)) * 100}%` }} /></span>;
}

/** 选了「错在哪一步」之后的修法。叫法不同的可以当场加别名；其余给去处。 */
function Fix({ category, detail, cards, onReask }: { category: string; detail: IssueDetail; cards: PayloadCard[]; onReask: () => void }) {
  const reask = (
    <div className="qy-fix-row"><span className="hint">改完用同样的问题再问一次，看这次答没答对</span>
      <button type="button" className="gbtn" onClick={onReask}><Icon name="refresh" size={13} style={{ verticalAlign: '-2px', marginRight: 4 }} />重问</button></div>
  );
  const docs = [...new Map(cards.filter((c) => c.docId).map((c) => [c.docId, c])).values()].slice(0, 4);
  return (
    <div className="qy-fix">
      {category === 'alias' && <AliasForm />}
      {category === 'missing' && (
        <p>资料里没有这方面的内容：到 <Link to="/admin/corpus">语料管理</Link> 上传，或者请提问人把资料传上来。{detail.comment ? `提问人说：「${detail.comment}」` : ''}</p>
      )}
      {category === 'parse' && (
        <>
          <p>打开原文和解析出来的文字对照，表格乱了、扫描件没认出字的，在语料管理里重新解析。</p>
          {docs.map((c) => <Link key={c.docId!} className="qy-doc" to={`/admin/corpus/${c.docId}/preview`}>{c.title} ›</Link>)}
        </>
      )}
      {category === 'crowded' && (
        <p>资料搜到了，但排在后面没给模型看。可以在 <Link to="/admin/settings">系统设置</Link> 里调大「每次搜索给几段」或调低「没找到」门槛；改完到「测试题与准确率」跑一遍，看别的题有没有变差。</p>
      )}
      {category === 'model' && (
        <p>资料给对了，模型没答好。存为测试题；以后换模型、开关「先思考」时跑一遍对比。</p>
      )}
      {category !== 'not_issue' && reask}
    </div>
  );
}

const KINDS: [string, string][] = [['common_term', '常用说法'], ['customer_name', '客户'], ['device_type', '设备']];

function AliasForm() {
  const [alias, setAlias] = useState('');
  const [value, setValue] = useState('');
  const [kind, setKind] = useState('common_term');
  const [done, setDone] = useState<string[]>([]);
  const [err, setErr] = useState<string | null>(null);
  const add = async () => {
    setErr(null);
    try {
      await post('/api/quality/aliases', { alias: alias.trim(), value: value.trim(), kind });
      setDone((x) => [...x, `${alias.trim()} → ${value.trim()}`]); setAlias(''); setValue('');
    } catch (e) { setErr(e instanceof ApiError ? e.message : '没加上'); }
  };
  return (
    <>
      <div className="qy-alias">
        <input aria-label="问题里的叫法" placeholder="问题里的叫法，如「哈氏釜」" value={alias} onChange={(e) => setAlias(e.target.value)} />
        <span aria-hidden="true">→</span>
        <input aria-label="公司的标准说法" placeholder="标准说法，如「C276 反应釜」" value={value} onChange={(e) => setValue(e.target.value)} />
        <select aria-label="类型" value={kind} onChange={(e) => setKind(e.target.value)}>
          {KINDS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
        </select>
        <button type="button" className="gbtn" disabled={!alias.trim() || !value.trim()} onClick={() => void add()}>加上</button>
      </div>
      {err && <span className="q-err">{err}</span>}
      {done.map((x) => <div key={x} className="qy-added"><Icon name="check" size={13} stroke={2.2} />已加别名：{x}</div>)}
    </>
  );
}
