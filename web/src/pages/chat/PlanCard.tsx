// 回答里的执行计划：模型先列计划，每一步一个带序号的圈（做完打勾、正在做的外圈有彗星尾在转、没做的虚线圈），
// 上面一条按步数分段的进度条；正在做的那一步展开它调过的工具；最下面一行说正在干什么、这一下用了多久。
// 前提假设收在卡片底部；有没做的步骤时，底部说明为什么停、还差什么。答完收成一行（几步 · 用时 · 调用几次），点开还能看。
// 简单问题没有计划，照旧显示「查资料」记录（StepsCard）。
import { useEffect, useState } from 'react';
import type { AssistantStep, PlanState } from '../../lib/types';
import { Icon } from '../../components/Icons';

// 列计划、思考、翻看技能不挂在步骤下面给用户看
const QUIET = new Set(['plan', 'think', 'use_skill']);

/** 每秒走一下的「现在」（只在进行中走）。 */
function useNow(on: boolean) {
  const [now, set] = useState(() => Date.now());
  useEffect(() => {
    if (!on) return;
    const t = setInterval(() => set(Date.now()), 1000);
    return () => clearInterval(t);
  }, [on]);
  return now;
}

/** 时长：00:37、03:41。 */
const clock = (sec: number) => {
  const s = Math.max(0, Math.round(sec));
  return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
};
const since = (now: number, iso: string | null | undefined, end?: string | null) => (iso ? ((end ? Date.parse(end) : now) - Date.parse(iso)) / 1000 : 0);

type Status = PlanState['items'][number]['status'];

/** 步骤前面的圈：做完打勾，正在做的外圈有彗星尾，还没做的灰圈，没做的虚线圈。 */
function Mark({ s, n }: { s: Status; n: number }) {
  if (s === 'done') return <span className="pl-mk done" role="img" aria-label="做完"><Icon name="check" size={12} stroke={3} /></span>;
  const label = s === 'now' ? '正在做' : s === 'skip' ? '没做' : '还没做';
  return <span className={'pl-mk ' + s} role="img" aria-label={label}>{n}</span>;
}

export function PlanCard({ plan, steps, streaming, seconds }: { plan: PlanState; steps: AssistantStep[]; streaming: boolean; seconds?: number | null }) {
  const [open, setOpen] = useState<boolean | null>(null);
  const [peek, setPeek] = useState<Record<number, boolean>>({});
  const [assumeOpen, setAssumeOpen] = useState(true);
  const live = streaming && !plan.finished;
  const now = useNow(live);
  const byId = new Map(steps.map((s) => [s.id, s]));
  const done = plan.items.filter((i) => i.status === 'done').length;
  const skipped = plan.items.filter((i) => i.status === 'skip');
  const total = seconds ?? since(now, plan.started);
  const calls = steps.filter((s) => !QUIET.has(s.tool) && s.tool !== 'retry').length;
  const expanded = open ?? live;
  const running = [...steps].reverse().find((s) => s.status === 'running' && !QUIET.has(s.tool));
  // 当前这个工具从什么时候开始（网页这边记，给最下面一行计时）
  const rid = running?.id ?? null;
  const [mark, setMark] = useState<{ id: string | null; at: number }>({ id: rid, at: now });
  if (mark.id !== rid) setMark({ id: rid, at: now });
  const lastReason = plan.reasons.length > 0 ? plan.reasons[plan.reasons.length - 1] : null;
  const assumptions = plan.assumptions ?? [];
  const allDone = done === plan.items.length;

  if (!expanded) {
    return (
      <button type="button" className="pl-one" aria-expanded={false} onClick={() => setOpen(true)}>
        {allDone
          ? <span className="pl-mk done sm" aria-hidden="true"><Icon name="check" size={10} stroke={3.2} /></span>
          : <span className="pl-mk part sm" aria-hidden="true"><i /></span>}
        <b>{allDone ? `执行计划 · ${plan.items.length} 步全部完成` : `执行计划 · 完成 ${done} / ${plan.items.length} 步`}</b>
        <span className="hint tn">
          用时 {clock(total)}{calls > 0 ? ` · 调用工具 ${calls} 次` : ''}{plan.revisions > 0 ? ` · 计划调整 ${plan.revisions} 次` : ''}
          {skipped.length > 0 ? ` · ${skipped.length} 步没做` : ''}
        </span>
        <span className="hint pl-r">展开<Icon name="down" size={13} className="gi" /></span>
      </button>
    );
  }

  const state = live ? { cls: 'run', text: '进行中' } : allDone ? { cls: 'ok', text: '已完成' } : { cls: 'wait', text: skipped.length > 0 ? '有步骤暂缓' : '没做完' };
  return (
    <section className="pl" aria-label="执行计划">
      <div className="pl-hd">
        <b>执行计划</b>
        <span className="hint">
          共 {plan.items.length} 步 · 已完成 {done} 步{skipped.length > 0 ? ` · 暂缓 ${skipped.length} 步` : ''}{plan.revisions > 0 ? ` · 计划调整 ${plan.revisions} 次` : ''}
        </span>
        <span className={'pl-state ' + state.cls}>{state.cls === 'run' && <i aria-hidden="true" />}{state.text}</span>
        <span className="hint tn">{clock(total)}</span>
        {!live && <button type="button" className="hm-link" onClick={() => setOpen(false)}>收起<Icon name="chevUp" size={13} className="gi" /></button>}
      </div>
      <div className="pl-seg" aria-hidden="true">
        {plan.items.map((it, i) => <i key={i} className={it.status} />)}
      </div>
      {lastReason && live && <div className="pl-note">调整了计划：{lastReason}</div>}
      <ol className="pl-list">
        {plan.items.map((it, i) => {
          const sub = it.steps.map((id) => byId.get(id)).filter((s): s is AssistantStep => !!s && !QUIET.has(s.tool));
          const show = peek[i] ?? it.status === 'now';
          const secs = it.started ? since(now, it.started, it.ended) : 0;
          return (
            <li key={i} className={'pl-it ' + it.status}>
              <Mark s={it.status} n={i + 1} />
              <div className="pl-body">
                <span className="pl-t">{it.title}</span>
                {it.note && <span className="pl-sub-note">{it.status === 'skip' ? '暂缓：' : ''}{it.note}</span>}
                {show && sub.length > 0 && (
                  <div className="pl-calls">
                    {sub.map((s) => (
                      <div key={s.id} className={s.status === 'error' ? 'err' : undefined}>
                        <span className="k">{s.label}</span>
                        <span className="v">{s.detail || '—'}</span>
                        <span className="r tn">{s.status === 'running' ? '进行中…' : s.result ?? ''}</span>
                      </div>
                    ))}
                  </div>
                )}
              </div>
              <div className="pl-meta">
                {sub.length > 0 && (
                  <button type="button" className="pl-n" aria-expanded={show} onClick={() => setPeek({ ...peek, [i]: !show })}>
                    {sub.length} 次调用<Icon name={show ? 'chevUp' : 'down'} size={12} />
                  </button>
                )}
                <span className="tn">{(it.status === 'done' || it.status === 'now') && secs > 0 ? clock(secs) : ''}</span>
              </div>
            </li>
          );
        })}
      </ol>
      {live && (
        <div className="pl-live">
          <span className="comet sm" aria-hidden="true" />
          <span>{running ? `正在${running.label}${running.detail ? ' · ' + running.detail : ''}` : '正在想下一步'}</span>
          {running && <span className="pl-tm tn">{clock((now - mark.at) / 1000)}</span>}
        </div>
      )}
      {!live && skipped.length > 0 && (
        <div className="pl-pause">
          <Icon name="clock" size={16} />
          <div>
            <b>{skipped.length === plan.items.length - done ? `后面 ${skipped.length} 步没做` : `有 ${skipped.length} 步没做`}</b>
            <span>{skipped[0].note ? `原因：${skipped[0].note}` : '缺的信息补上后，说「接着做」就从这里继续。'}</span>
          </div>
        </div>
      )}
      {assumptions.length > 0 && (
        <div className="pl-assume">
          <button type="button" aria-expanded={assumeOpen} onClick={() => setAssumeOpen(!assumeOpen)}>
            <b>前提假设</b><span className="hint">{assumptions.length} 条 · 没说清的按常规做法定，不合适随时改</span>
            <Icon name={assumeOpen ? 'chevUp' : 'down'} size={13} className="pl-r" />
          </button>
          {assumeOpen && <ol>{assumptions.map((a) => <li key={a}>{a}</li>)}</ol>}
        </div>
      )}
    </section>
  );
}
