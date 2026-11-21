// 把一张像素字符画画成 SVG：同一行里连在一起的同色格子合成一段，每种颜色一条 path，格子再多也很轻。
import { memo } from 'react';
import { H, PALETTE, POSES, W } from './sprite';
import type { Pose } from './sprite';

const cache = new Map<Pose, { fill: string; d: string }[]>();

function paths(pose: Pose) {
  const hit = cache.get(pose);
  if (hit) return hit;
  const byColor = new Map<string, string[]>();
  POSES[pose].forEach((row, y) => {
    let x = 0;
    while (x < row.length) {
      const ch = row[x];
      let end = x + 1;
      while (end < row.length && row[end] === ch) end++;
      if (ch !== '.') {
        const list = byColor.get(ch) ?? [];
        list.push(`M${x} ${y}h${end - x}v1h${x - end}z`);
        byColor.set(ch, list);
      }
      x = end;
    }
  });
  const out = [...byColor].map(([ch, ds]) => ({ fill: PALETTE[ch] ?? '#000', d: ds.join('') }));
  cache.set(pose, out);
  return out;
}

export const PixelSprite = memo(function PixelSprite({ pose, width = 32, className, title }: {
  pose: Pose; width?: number; className?: string; title?: string;
}) {
  const height = Math.round((width * H) / W);
  return (
    <svg className={className} width={width} height={height} viewBox={`0 0 ${W} ${H}`} shapeRendering="crispEdges"
      role={title ? 'img' : undefined} aria-label={title} aria-hidden={title ? undefined : true}>
      {paths(pose).map((p) => <path key={p.fill} d={p.d} fill={p.fill} />)}
    </svg>
  );
});
