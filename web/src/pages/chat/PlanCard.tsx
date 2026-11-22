// 回答里的任务清单：模型先列计划，做完一步打勾、正在做的转圈；每一步下面挂着它调过的工具；最下面一行说正在干什么、用了多久。
// 答完收成一行（做了几步 · 用时 · 计划改过几次），点开还能看。简单问题没有计划，照旧显示「查了几步」（StepsCard）。
import { useEffect, useState } from 'react';
import type { AssistantStep, PlanState } from '../../lib/types';
import { PixelSprite } from '../../components/pet/PixelSprite';
import { ACTS, actOfTool } from '../../components/pet/thinking';

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

const clock = (sec: number) => {
  const s = Math.max(0, Math.round(sec));
  return s < 60 ? `${s} 秒` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
};
const since = (now: number, iso: string | null | undefined, end?: string | null) => (iso ? ((end ? Date.parse(end) : now) - Date.parse(iso)) / 1000 : 0);

/** 状态记号：线框图标。 */
function Mark({ s }: { s: string }) {
  if (s === 'done') return <svg width="18" height="18" viewBox="0 0 18 18" aria-label="做完"><circle cx="9" cy="9" r="8" fill="#1C6B45" /><path d="M5.2 9.3l2.4 2.4 5-5" fill="none" stroke="#fff" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" /></svg>;
  if (s === 'now') return <svg className="pl-spin" width="18" height="18" viewBox="0 0 18 18" aria-label="正在做"><circle cx="9" cy="9" r="7" fill="none" stroke="#E4E4E1" strokeWidth="2" /><path d="M9 2a7 7 0 0 1 7 7" fill="none" stroke="#141414" strokeWidth="2" strokeLinecap="round" /></svg>;
  if (s === 'skip') return <svg width="18" height="18" viewBox="0 0 18 18" aria-label="没做"><circle cx="9" cy="9" r="7.5" fill="none" stroke="#C9C9C6" strokeWidth="1.4" /><path d="M5.5 9h7" stroke="#9A9A9A" strokeWidth="1.6" strokeLinecap="round" /></svg>;
  return <svg width="18" height="18" viewBox="0 0 18 18" aria-label="还没做"><circle cx="9" cy="9" r="7.5" fill="none" stroke="#B5B5B1" strokeWidth="1.4" strokeDasharray="2.6 2.2" /></svg>;
}

export function PlanCard({ plan, steps, streaming, seconds }: { plan: PlanState; steps: AssistantStep[]; streaming: boolean; seconds?: number | null }) {
  const [open, setOpen] = useState<boolean | null>(null);
  const [peek, setPeek] = useState<Record<number, boolean>>({});
  const live = streaming && !plan.finished;
  const now = useNow(live);
  const byId = new Map(steps.map((s) => [s.id, s]));
  const done = plan.items.filter((i) => i.status === 'done').length;
  const total = seconds ?? since(now, plan.started);
  const expanded = open ?? live;
  const running = [...steps].reverse().find((s) => s.status === 'running' && s.tool !== 'think' && s.tool !== 'plan');
  const act = ACTS[running ? actOfTool(running.tool) : 'scratch'];
  // 当前这个工具从什么时候开始（网页这边记，给最下面一行计时）
  const rid = running?.id ?? null;
  const [mark, setMark] = useState<{ id: string | null; at: number }>({ id: rid, at: now });
  if (mark.id !== rid) setMark({ id: rid, at: now });
  const lastReason = plan.reasons.length > 0 ? plan.reasons[plan.reasons.length - 1] : null;

  if (!expanded) {
    return (
      <button type="button" className="pl-one" aria-expanded={false} onClick={() => setOpen(true)}>
        <Mark s="done" /><b>做了 {done} 步</b>
        <span className="hint">· 用时 {clock(total)}{plan.revisions > 0 ? ` · 计划改过 ${plan.revisions} 次` : ''}{plan.items.some((i) => i.status === 'skip') ? ` · ${plan.items.filter((i) => i.status === 'skip').length} 步没做` : ''}</span>
        <span className="hint" style={{ marginLeft: 'auto' }}>展开 ▾</span>
      </button>
    );
  }
  return (
    <section className="pl" aria-label="任务清单">
      <div className="pl-hd">
        <b>要做 {plan.items.length} 步</b>
        <span className="hint">已完成 {done} 步{plan.revisions > 0 ? ` · 计划改过 ${plan.revisions} 次` : ''}</span>
        <span className="hint pl-r">用时 {clock(total)}</span>
        {!live && <button type="button" className="hm-link" onClick={() => setOpen(false)}>收起 ▴</button>}
      </div>
      <div className="pl-bar"><i style={{ width: `${(done / Math.max(1, plan.items.length)) * 100}%` }} /></div>
      {lastReason && live && <div className="pl-note">改了计划：{lastReason}</div>}
      {plan.items.map((it, i) => {
        const sub = it.steps.map((id) => byId.get(id)).filter((s): s is AssistantStep => !!s && s.tool !== 'plan' && s.tool !== 'think');
        const show = peek[i] ?? it.status === 'now';
        const secs = it.started ? since(now, it.started, it.ended) : null;
        return (
          <div key={i}>
            <div className={'pl-it ' + it.status}>
              <Mark s={it.status} />
              <span className="pl-t">{it.title}{it.note && <small>{it.note}</small>}</span>
              {sub.length > 0 && <button type="button" className="pl-n" aria-expanded={show} onClick={() => setPeek({ ...peek, [i]: !show })}>{sub.length} 次调用 {show ? '▴' : '▾'}</button>}
              <span className="pl-s">{secs != null && it.status !== 'todo' ? clock(secs) : ''}</span>
            </div>
            {show && sub.length > 0 && (
              <div className="pl-sub">
                {sub.map((s) => (
                  <div key={s.id} className={s.status === 'error' ? 'err' : undefined}>
                    <b>{s.label}</b><span className="v">{s.detail || '—'}</span><span>{s.status === 'running' ? '…' : s.result ?? ''}</span>
                  </div>
                ))}
              </div>
            )}
          </div>
        );
      })}
      {live && (
        <div className="pl-live">
          <span className="pl-pet" style={{ ['--ag-speed' as string]: `${act.speed}s` }}>
            <PixelSprite pose={act.frames[0]} width={22} className={act.frames[0] !== act.frames[1] ? 'ag-f1' : undefined} />
            {act.frames[0] !== act.frames[1] && <PixelSprite pose={act.frames[1]} width={22} className="ag-f2" />}
          </span>
          <span>正在：{running ? `${running.label}${running.detail ? ' · ' + running.detail : ''}` : '想下一步'}</span>
          {running && <span className="pl-tm">{clock((now - mark.at) / 1000)}</span>}
        </div>
      )}
    </section>
  );
}
