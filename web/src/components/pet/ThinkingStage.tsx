// 回答顶部的「舞台」：小人跳进回答里干活，旁边一个像素对话框说它在干嘛，下面一步一个列出走过的步骤。
// 答完整个舞台收起（换回「查了几步」那一行）。动作由 thinking.ts 按系统正在做的事挑。
import { useEffect, useRef, useState } from 'react';
import type { AssistantStep } from '../../lib/types';
import { PixelIcon, PixelSprite } from './PixelSprite';
import { ACTS, HOLD_MS, pickAct } from './thinking';
import type { Act, Live } from './thinking';
import { Icon } from '../Icons';

const SIZE = 44;

export function ThinkingStage({ steps, hasText }: { steps: AssistantStep[]; hasText: boolean }) {
  const live = useRef<Live>({ steps, hasText, startedAt: 0, firstTextAt: null, quietSince: 0 });
  const [shown, setShown] = useState<{ act: Act; say: string; sub: string }>({ act: 'scratch', say: ACTS.scratch.say, sub: '' });
  const since = useRef(0);

  // 记下最新的步骤和出字情况；步骤数变了或最后一步结束了，就从这时起重新计「多久没动静」
  useEffect(() => {
    const l = live.current;
    const now = Date.now();
    if (!l.startedAt) { l.startedAt = now; l.quietSince = now; since.current = now; }
    const lastBefore = l.steps[l.steps.length - 1];
    const lastNow = steps[steps.length - 1];
    if (steps.length !== l.steps.length || (lastNow && lastBefore?.status !== lastNow.status)) l.quietSince = now;
    if (hasText && l.firstTextAt == null) l.firstTextAt = now;
    l.steps = steps; l.hasText = hasText;
  }, [steps, hasText]);

  // 每 250ms 看一眼该做什么；当前动作没停够 1.5 秒就先不换
  useEffect(() => {
    const t = setInterval(() => {
      const now = Date.now();
      const next = pickAct(live.current, now);
      setShown((cur) => {
        if (cur.act === next.act && cur.say === next.say && cur.sub === next.sub) return cur;
        if (cur.act !== next.act && now - since.current < HOLD_MS) return cur;
        if (cur.act !== next.act) since.current = now;
        return next;
      });
    }, 250);
    return () => clearInterval(t);
  }, []);

  const def = ACTS[shown.act];
  const unit = SIZE / 32;
  return (
    <div className="ts">
      <div className="ts-row">
        <div className={'ts-pet' + (shown.act === 'idea' ? ' bob' : '')} key={shown.act} style={{ ['--ts-speed' as string]: `${def.speed}s` }}>
          <PixelSprite pose={def.frames[0]} width={SIZE} className="ts-f1" />
          {def.frames[1] !== def.frames[0] && <PixelSprite pose={def.frames[1]} width={SIZE} className="ts-f2" />}
          {def.icon && <PixelIcon icon={def.icon} unit={unit} className={'ts-ic ts-ic-' + def.icon} />}
        </div>
        <div className="ts-say" role="status" aria-live="polite">
          <b>{shown.say}</b>
          {shown.sub && <span>{shown.sub}</span>}
          {shown.act !== 'idea' && <i aria-hidden="true">▍</i>}
        </div>
      </div>
      {steps.length > 0 && (
        <div className="ts-trail">
          {steps.map((s) => (
            <span key={s.id} className={'ts-tr ' + s.status}>
              <i aria-hidden="true"><Icon name={s.status === 'running' ? 'dot' : s.status === 'error' ? 'x' : 'check'} size={11} stroke={2.4} /></i>
              <b>{s.label}</b>
              {s.status !== 'running' && s.result && <span>{s.result}</span>}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}
