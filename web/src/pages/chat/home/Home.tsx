// 首页：居中一列，只有三样——问候（日期、一句问好、一句生活上的话）、输入框（下面一行常用技能）、需要你看的（最多 3 件）。
// 问候按时段换、每天由模型现写，过节换成节日祝福；「需要你看的」把处理好的反馈、没填完的文档、系统算出的正事排在一起，
// 点过的当天不再出现；一件都没有就整块不显示；都点完了收成一行，小人欢呼一下。
import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post } from '../../../lib/api';
import type { SessionRowT } from '../../../lib/types';
import type { SkillRow } from '../composerUtil';
import { Icon } from '../../../components/Icons';
import { festivalOf } from '../../../components/pet/days';

interface Line { title: string; sub: string }
interface WorkItem { id: string; emoji: string; title: string; badge: string; tone: string; detail: string; action: string; prompt: string }
export interface HomeToday {
  name: string; date: string; lines: Record<string, Line>;
  work: WorkItem[]; recent: string[]; offWork: string; refreshLeft: number; generated: boolean;
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
const pad = (n: number) => String(n).padStart(2, '0');
const dateLine = (d: Date) => `${d.getMonth() + 1} 月 ${d.getDate()} 日 · 周${WEEK[d.getDay()]} · ${pad(d.getHours())}:${pad(d.getMinutes())}`;

function useNow(ms = 30_000): Date {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => { const t = setInterval(() => setNow(new Date()), ms); return () => clearInterval(t); }, [ms]);
  return now;
}

function ago(iso: string): string {
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return '';
  const min = Math.round((Date.now() - t) / 60000);
  if (min < 1) return '刚刚';
  if (min < 60) return `${min} 分钟前`;
  if (min < 24 * 60) return `${Math.round(min / 60)} 小时前`;
  if (min < 48 * 60) return '昨天';
  return iso.slice(5, 10).replace('-', '/');
}

// 今天点过哪几件（只记在这台电脑的浏览器里；存不了就当没点过）
interface Seen { day: string; keys: string[]; cheered: boolean }
const SEEN_KEY = 'hm-seen';
function loadSeen(): Seen {
  const day = new Date().toDateString();
  try {
    const s = JSON.parse(localStorage.getItem(SEEN_KEY) ?? 'null') as Seen | null;
    if (s && s.day === day && Array.isArray(s.keys)) return s;
  } catch { /* 读不了 */ }
  return { day, keys: [], cheered: false };
}
function saveSeen(s: Seen) { try { localStorage.setItem(SEEN_KEY, JSON.stringify(s)); } catch { /* 存不了 */ } }

const TONE: Record<string, string> = { up: 'up', warn: 'warn', ok: 'ok', info: 'info' };

interface Item { key: string; tone: string; title: string; badge?: string; detail: string; action: string; done?: boolean; run: () => void }

export function Home({ fallbackName, composer, onPick, skills, onSkill, drafts, onOpenDoc }: {
  fallbackName: string; composer: ReactNode; onPick: (q: string) => void;
  /** 常用：技能管理里放进菜单的技能，首页摆前 4 个 */
  skills: SkillRow[]; onSkill: (s: SkillRow) => void;
  /** 没填完的文档 */
  drafts: SessionRowT[]; onOpenDoc: (id: string) => void;
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
  const fest = festivalOf(now);
  const line = fest ? { title: `${fest.title}，${name}`, sub: fest.sub } : data?.lines[slot] ?? defaultLine(slot, name);
  const [seen, setSeen] = useState(loadSeen);

  const items: Item[] = [
    // 反馈过的问题处理好了：点一下用原来的问题再问一次
    ...(fixed.data ?? []).map((f) => ({
      key: 'fb:' + f.id, tone: 'ok', done: true, title: '你反馈的问题处理好了',
      detail: `「${f.question}」${f.note ? ` · ${f.note}` : ''}`, action: '再问一次',
      run: () => { void post(`/api/qa-sessions/feedback/${f.id}/seen`).finally(() => void qc.invalidateQueries({ queryKey: ['feedback-resolved'] })); onPick(f.question); },
    })),
    ...drafts.slice(0, 2).map((d) => ({
      key: 'doc:' + d.id, tone: 'info', title: d.templateName, badge: `填了 ${d.done}/${d.total}`,
      detail: `还没填完 · 上次 ${ago(d.updatedAt)}`, action: '接着填', run: () => onOpenDoc(d.id),
    })),
    ...(data?.work ?? []).map((w) => ({
      key: `w:${w.id}:${w.title}`, tone: TONE[w.tone] ?? 'info', title: w.title, badge: w.badge, detail: w.detail, action: w.action,
      run: () => onPick(w.prompt),
    })),
  ];
  const ready = !!data && !fixed.isPending;
  const left = items.filter((i) => !seen.keys.includes(i.key));
  const allDone = ready && items.length > 0 && left.length === 0;

  // 都点完了：小人欢呼一下（一天一次）
  useEffect(() => {
    if (!allDone || seen.cheered) return;
    const t = setTimeout(() => {
      window.dispatchEvent(new CustomEvent('htpet', { detail: 'cheer' }));
      const s = { ...seen, cheered: true }; setSeen(s); saveSeen(s);
    }, 900);
    return () => clearTimeout(t);
  }, [allDone, seen]);

  const open = (it: Item) => {
    const s = { ...seen, keys: [...seen.keys, it.key] };
    setSeen(s); saveSeen(s);
    it.run();
  };

  const pickSkill = (s: SkillRow) => {
    onSkill(s);
    document.querySelector<HTMLTextAreaElement>('textarea.composer-input')?.focus();
  };

  return (
    <div className="hm">
      <header className="hm-hero">
        <span className="hm-date tn">{dateLine(now)}</span>
        <h1 className="hm-title">{line.title}</h1>
        {line.sub && <p className="hm-sub">{line.sub}</p>}
      </header>

      {composer}

      {skills.length > 0 && (
        <div className="hm-skills" aria-label="常用技能">
          {skills.slice(0, 4).map((s) => (
            <button key={s.code} type="button" title={s.description} onClick={() => pickSkill(s)}>
              {s.name}{s.ppt && <span className="beta-tag" title="试用版：还在完善，做出来的 PPT 请检查后再用">Beta</span>}
            </button>
          ))}
        </div>
      )}

      {ready && left.length > 0 && (
        <section className="hm-work" aria-labelledby="hm-work-t">
          <div className="hm-sec-hd"><b id="hm-work-t">需要你看的</b><span>{left.length > 3 ? `最要紧的 3 件，还有 ${left.length - 3} 件` : `${left.length} 件`}</span></div>
          <div className="hm-list">
            {left.slice(0, 3).map((it) => (
              <button key={it.key} type="button" className="hm-row" onClick={() => open(it)}>
                {it.done
                  ? <span className="hm-ok" aria-hidden="true"><Icon name="check" size={12} stroke={3} /></span>
                  : <span className={'hm-dot ' + it.tone} aria-hidden="true" />}
                <span className="hm-row-m">
                  <span className="hm-row-t"><b>{it.title}</b>{it.badge && <i className={'hm-badge tn ' + it.tone}>{it.badge}</i>}</span>
                  <span className="hm-row-d">{it.detail}</span>
                </span>
                <span className="hm-row-a">{it.action}<Icon name="forward" size={14} /></span>
              </button>
            ))}
          </div>
        </section>
      )}
      {allDone && (
        <div className="hm-done" role="status"><Icon name="check" size={14} stroke={2.6} />今天要看的都看完了</div>
      )}
    </div>
  );
}
