// 分头查：助手把任务拆给几个子代理同时做。每个子代理一个像素小人（同一个小人，头带换成各自的颜色、带编号），
// 动作跟着它正在调的工具走（翻资料、查台账、算一算），交回来就举手；答完以后是静止的。点一行能看它自己查了哪几步。
import { useState } from 'react';
import { PixelSprite } from '../../components/pet/PixelSprite';
import { Icon } from '../../components/Icons';
import { ACTS, actOfTool } from '../../components/pet/thinking';
import type { Pose } from '../../components/pet/sprite';
import type { AgentsCardData } from '../../lib/types';
import { tintOf } from '../../components/pet/tints';

type Item = AgentsCardData['items'][number];

function frames(it: Item, live: boolean): [Pose, Pose, number] {
  if (it.status === 'done') return ['cheer', 'cheer', 1];
  if (it.status === 'partial' || it.status === 'error') return ['oops1', live ? 'oops2' : 'oops1', 0.4];
  if (it.status === 'queued') return ['sit', 'sit', 1];
  if (!live) return ['idle', 'idle', 1];
  const run = [...it.steps].reverse().find((s) => s.status === 'running');
  const def = ACTS[run ? actOfTool(run.tool) : 'scratch'];
  return [def.frames[0], def.frames[1], def.speed];
}

function Mascot({ it, live, size = 26 }: { it: Item; live: boolean; size?: number }) {
  const tint = tintOf(it.n)!;
  const [f1, f2, speed] = frames(it, live);
  return (
    <span className="ag-pet" style={{ ['--ag-speed' as string]: `${speed}s` }}>
      <PixelSprite pose={f1} width={size} tint={tint} className={f1 !== f2 ? 'ag-f1' : undefined} />
      {f1 !== f2 && <PixelSprite pose={f2} width={size} tint={tint} className="ag-f2" />}
      <i style={{ background: tint[0] }}>{it.n}</i>
    </span>
  );
}

const STATUS: Record<Item['status'], [string, string]> = {
  queued: ['排队', 'q'], running: ['在查', 'run'], done: ['完成', 'ok'], partial: ['没查完', 'bad'], error: ['没做成', 'bad'],
};

export function AgentsCard({ card, live }: { card: AgentsCardData; live: boolean }) {
  const [open, setOpen] = useState<number | null>(null);
  const done = card.items.filter((i) => i.status === 'done').length;
  const bad = card.items.filter((i) => i.status === 'partial' || i.status === 'error').length;
  const running = card.items.some((i) => i.status === 'running' || i.status === 'queued');
  return (
    <section className="ag" aria-label="分头查">
      <div className="ag-hd">
        <b>分头查：{card.items.length} 个子任务</b>
        <span className="hint">{running ? '同时进行…' : bad > 0 ? `${done} 个完成 · ${bad} 个没查完` : '都交回来了'}</span>
      </div>
      {card.items.map((it) => {
        const [label, tone] = STATUS[it.status];
        const isOpen = open === it.n;
        return (
          <div key={it.n} className="ag-row-w">
            <button type="button" className="ag-row" aria-expanded={isOpen} onClick={() => setOpen(isOpen ? null : it.n)}>
              <Mascot it={it} live={live} />
              <span className="ag-t">
                <b>{it.title}</b>
                <span>{it.note ?? (it.result ? it.result : `交回：${it.deliver}`)}</span>
              </span>
              <span className={'ag-st ' + tone}>{label}{it.status === 'running' && it.steps.length > 0 ? ` · ${it.steps[it.steps.length - 1].label}` : ''}</span>
              {it.steps.length > 0 && <Icon name="down" size={13} style={{ color: 'var(--ink-4)', transform: isOpen ? 'rotate(180deg)' : undefined }} />}
            </button>
            {isOpen && it.steps.length > 0 && (
              <div className="ag-steps">
                {it.steps.map((s) => (
                  <div key={s.id} className={'ag-step' + (s.status === 'error' ? ' err' : '')}>
                    <Icon name={s.status === 'running' ? 'dot' : s.status === 'error' ? 'x' : 'check'} size={11} stroke={2.4} />
                    <b>{s.label}</b><span>{s.detail || '—'}</span><em>{s.status === 'running' ? '…' : s.result ?? ''}</em>
                  </div>
                ))}
              </div>
            )}
          </div>
        );
      })}
    </section>
  );
}
