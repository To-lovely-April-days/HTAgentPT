// 首页：一行问候、输入框（8-bit 小人住在它的上沿）、「今天有点意思」、最下面最多 3 件正事。
// 问候按时段换、每天由模型现写；有意思的内容每天换，跟这个人常碰的材料和设备有关；正事的数字由系统算。
import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post } from '../../../lib/api';
import { Icon } from '../../../components/Icons';

interface Line { title: string; sub: string }
interface WorkItem { id: string; emoji: string; title: string; badge: string; tone: string; detail: string; action: string; prompt: string }
export interface HomeToday {
  name: string; date: string; lines: Record<string, Line>;
  fun: {
    knowledge: { title: string; body: string } | null;
    quiz: { question: string; options: string[]; answer: number; explain: string } | null;
    history: { year: string; text: string } | null;
    number: { value: string; text: string } | null;
  };
  work: WorkItem[]; quiet: string[]; recent: string[]; offWork: string; refreshLeft: number; generated: boolean;
  /** 模型还在后台写，这次先给的系统台词；过几秒再取 */
  pending: boolean;
}

function slotOf(d: Date): string {
  const h = d.getHours();
  if (d.getDay() === 5 && h >= 14 && h < 20) return 'friday';
  return h < 5 ? 'late' : h < 11 ? 'morning' : h < 14 ? 'noon' : h < 17 ? 'afternoon' : h < 20 ? 'evening' : 'late';
}

/** 模型的话还没到时先用的。 */
function defaultLine(slot: string, name: string): Line {
  const greet = slot === 'morning' ? '早上好' : slot === 'noon' ? '中午好' : slot === 'late' ? '这么晚还在' : slot === 'evening' ? '晚上好' : '下午好';
  return { title: `${greet}，${name}`, sub: '' };
}

const WEEK = '日一二三四五六';
function clockLine(now: Date): string {
  return `周${WEEK[now.getDay()]} · ${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
}

function useNow(ms = 30_000): Date {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => { const t = setInterval(() => setNow(new Date()), ms); return () => clearInterval(t); }, [ms]);
  return now;
}

const TONE: Record<string, string> = { up: 'hb-up', warn: 'hb-warn', ok: 'hb-ok', info: 'hb-info' };
const DOT: Record<string, string> = { up: '#C8102E', warn: '#F5B800', ok: '#16A34A', info: '#3F3F46' };

function Quiz({ q }: { q: NonNullable<HomeToday['fun']['quiz']> }) {
  const [pick, setPick] = useState<number | null>(null);
  return (
    <div className="fun-card fun-quiz">
      <span className="fun-k">猜一猜</span>
      <b className="fun-q">{q.question}</b>
      {q.options.map((o, i) => {
        const state = pick == null ? '' : i === q.answer ? ' right' : i === pick ? ' wrong' : ' dim';
        return (
          <button key={i} type="button" className={'fun-opt' + state} disabled={pick != null} onClick={() => setPick(i)}>
            <i>{'ABCD'[i]}</i><span>{o}</span>{pick != null && i === q.answer && <em><Icon name="check" size={14} stroke={2.2} style={{ verticalAlign: '-2px' }} /></em>}{pick === i && i !== q.answer && <em><Icon name="x" size={14} stroke={2.2} style={{ verticalAlign: '-2px' }} /></em>}
          </button>
        );
      })}
      {pick != null && <span className={'fun-exp' + (pick === q.answer ? ' ok' : '')}>{pick === q.answer ? '答对了！' : '差一点。'}{q.explain}</span>}
    </div>
  );
}

export function Home({ fallbackName, composer, resume, onPick, drafts, onDrafts }: {
  fallbackName: string; composer: ReactNode; resume?: ReactNode; onPick: (q: string) => void;
  /** 没填完的文档份数，和打开它们的方法 */
  drafts: number; onDrafts: () => void;
}) {
  const qc = useQueryClient();
  // 模型写今天的内容要一阵子：后端先回系统台词（pending），这里每 4 秒再取，写好了自动换上；最多等 3 分钟
  const q = useQuery({
    queryKey: ['home-today'], queryFn: () => get<HomeToday>('/api/home/today'), staleTime: 30 * 60_000, retry: false,
    refetchInterval: (query) => (query.state.data?.pending && query.state.dataUpdateCount < 45 ? 4000 : false),
  });
  const fixed = useQuery({ queryKey: ['feedback-resolved'], queryFn: () => get<{ id: string; question: string; note: string | null }[]>('/api/qa-sessions/feedback/resolved'), retry: false });
  const data = q.data;
  const now = useNow();
  const slot = slotOf(now);
  const name = data?.name || fallbackName;
  const line = data?.lines[slot] ?? defaultLine(slot, name);
  const [refreshing, setRefreshing] = useState(false);
  const [liked, setLiked] = useState(false);
  const fun = data?.fun;
  const hasFun = !!(fun && (fun.knowledge || fun.quiz || fun.history || fun.number));

  const refresh = async () => {
    setRefreshing(true);
    try {
      // 换一组也是后台写：没写好前先留着旧的，写好了再换
      let r = await get<HomeToday>('/api/home/today?refresh=true');
      for (let i = 0; r.pending && i < 45; i++) {
        await new Promise((ok) => setTimeout(ok, 4000));
        r = await get<HomeToday>('/api/home/today');
      }
      qc.setQueryData(['home-today'], r); setLiked(false);
    }
    catch { /* 换不了就保持原样 */ }
    finally { setRefreshing(false); }
  };

  return (
    <div className="hm">
      <header className="hm-hero">
        <span className="hm-clock">{clockLine(now)}</span>
        <h1 className="hm-title">{line.title}</h1>
        {line.sub && <p className="hm-sub">{line.sub}</p>}
      </header>

      {composer}

      {(q.isPending || data?.pending || hasFun) && (
        <section className="hm-fun" aria-labelledby="hm-fun-t">
          <div className="hm-sec-hd">
            <b id="hm-fun-t">今天有点意思</b>
            <span>每天换 · 跟你常碰的材料和设备有关</span>
            {data && data.refreshLeft > 0 && (
              <button type="button" className="hm-link" disabled={refreshing} onClick={() => void refresh()}>{refreshing ? '正在换…' : <>换一组<Icon name="refresh" size={13} style={{ verticalAlign: '-2px', marginLeft: 4 }} /></>}</button>
            )}
          </div>
          {(q.isPending || (data?.pending && !hasFun)) && <div className="hm-skel"><i /><i /></div>}
          {fun && (fun.knowledge || fun.quiz) && (
            <div className="fun-row">
              {fun.knowledge && (
                <div className="fun-card fun-know">
                  <span className="fun-k">今日知识</span>
                  <b className="fun-t">{fun.knowledge.title}</b>
                  <p>{fun.knowledge.body}</p>
                  <div className="fun-acts">
                    <button type="button" className={liked ? 'on' : ''} onClick={() => setLiked(!liked)}>原来如此<Icon name={liked ? 'check' : 'thumbUp'} size={13} style={{ verticalAlign: '-2px', marginLeft: 5 }} /></button>
                    <button type="button" onClick={() => onPick(`再详细讲讲：${fun.knowledge!.title}`)}>再讲详细点</button>
                  </div>
                </div>
              )}
              {fun.quiz && <Quiz key={fun.quiz.question} q={fun.quiz} />}
            </div>
          )}
          {fun && (fun.history || fun.number) && (
            <div className="fun-row two">
              {fun.history && (
                <div className="fun-card fun-mini">
                  <span className="fun-big red">{fun.history.year}</span>
                  <span><i>化工史上</i>{fun.history.text}</span>
                </div>
              )}
              {fun.number && (
                <div className="fun-card fun-mini">
                  <span className="fun-big">{fun.number.value}</span>
                  <span><i>一个数字</i>{fun.number.text}</span>
                </div>
              )}
            </div>
          )}
        </section>
      )}

      <section className="hm-work" aria-labelledby="hm-work-t">
        {/* 反馈过的问题处理好了：点一下用原来的问题再问一次 */}
        {(fixed.data ?? []).map((f) => (
          <button key={f.id} type="button" className="hm-row" onClick={() => {
            void post(`/api/qa-sessions/feedback/${f.id}/seen`).finally(() => void qc.invalidateQueries({ queryKey: ['feedback-resolved'] }));
            onPick(f.question);
          }}>
            <span className="hm-dot" style={{ background: DOT.ok }} aria-hidden="true" />
            <span className="hm-row-m">
              <span className="hm-row-t"><b>你反馈的问题处理好了</b></span>
              <span className="hm-row-d">「{f.question}」{f.note ? ` · ${f.note}` : ''}</span>
            </span>
            <span className="hm-row-a">再问一次</span>
          </button>
        ))}
        {(data?.work.length ?? 0) > 0 && (
          <div className="hm-sec-hd"><b id="hm-work-t">正事</b><span>{data!.work.length} 件，数字由系统核算</span></div>
        )}
        {data?.work.map((w) => (
          <button key={w.id} type="button" className="hm-row" onClick={() => onPick(w.prompt)}>
            <span className="hm-dot" style={{ background: DOT[w.tone] ?? DOT.info }} aria-hidden="true" />
            <span className="hm-row-m">
              <span className="hm-row-t"><b>{w.title}</b><i className={'hm-badge ' + (TONE[w.tone] ?? 'hb-info')}>{w.badge}</i></span>
              <span className="hm-row-d">{w.detail}</span>
            </span>
            <span className="hm-row-a">{w.action}</span>
          </button>
        ))}
        {data && data.work.length === 0 && data.quiet.length > 0 && (
          <div className="hm-quiet">
            <span id="hm-work-t">今天没有跟你有关的新变动，可以问问这些：</span>
            {data.quiet.map((x) => <button key={x} type="button" onClick={() => onPick(x)}><Icon name="forward" size={13} className="gi l" />{x}</button>)}
          </div>
        )}
        {drafts > 0 && <button type="button" className="hm-more" onClick={onDrafts}>还有 {drafts} 份文档没填完 · <b>去看看<Icon name="forward" size={13} className="gi" /></b></button>}
        {resume}
      </section>
    </div>
  );
}
