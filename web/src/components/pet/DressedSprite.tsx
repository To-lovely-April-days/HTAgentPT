// 穿着衣服的小人：原来的像素图上叠一层层衣服（见 outfit.ts）。dressing 时一层层按顺序放出来（穿衣动画），平时直接穿好。
import { memo } from 'react';
import { H, W } from './sprite';
import type { Pose } from './sprite';
import { PixelSprite, paths } from './PixelSprite';
import { dress } from './outfit';
import type { Outfit } from './outfit';

export const DressedSprite = memo(function DressedSprite({ pose, outfit, width, dressing }: {
  pose: Pose; outfit: Outfit; width: number;
  /** 正在穿：每一层按自己的时间动一下；换一个值（比如时间戳）会从头再穿一遍 */
  dressing?: number | null;
}) {
  const unit = width / W;
  const { layers } = dress(pose, outfit);
  return (
    <span className="dz-wrap" style={{ width, height: Math.round(H * unit) }}>
      <PixelSprite pose={pose} width={width} />
      {layers.map((l) => (
        <svg key={l.key + (dressing ?? '')} className={dressing ? 'dz dz-' + l.anim : 'dz'} aria-hidden="true"
          width={l.w * unit} height={l.h * unit} viewBox={`0 0 ${l.w} ${l.h}`} shapeRendering="crispEdges"
          style={{ left: l.x * unit, top: l.y * unit, ...(dressing ? { animationDelay: `${l.delay}s`, animationDuration: `${l.dur}s` } : null) }}>
          {paths('dz:' + l.key, l.rows).map((p) => <path key={p.ch} d={p.d} fill={p.fill} />)}
        </svg>
      ))}
    </span>
  );
});
