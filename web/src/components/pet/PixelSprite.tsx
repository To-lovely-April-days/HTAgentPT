// 把一张像素字符画画成 SVG：同一行里连在一起的同色格子合成一段，每种颜色一条 path，格子再多也很轻。
import { memo } from 'react';
import { H, ICONS, PALETTE, POSES, W } from './sprite';
import type { Icon, Pose } from './sprite';

const cache = new Map<string, { ch: string; fill: string; d: string }[]>();

function paths(key: string, rows: string[]) {
  const hit = cache.get(key);
  if (hit) return hit;
  const byColor = new Map<string, string[]>();
  rows.forEach((row, y) => {
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
  const out = [...byColor].map(([ch, ds]) => ({ ch, fill: PALETTE[ch] ?? '#000', d: ds.join('') }));
  cache.set(key, out);
  return out;
}

/** tint：把头带和衣服上的红色（R 亮、r 暗）换成别的颜色——分头查时每个子代理一个颜色。 */
export const PixelSprite = memo(function PixelSprite({ pose, width = 32, className, title, tint }: {
  pose: Pose; width?: number; className?: string; title?: string; tint?: readonly [string, string];
}) {
  const height = Math.round((width * H) / W);
  return (
    <svg className={className} width={width} height={height} viewBox={`0 0 ${W} ${H}`} shapeRendering="crispEdges"
      role={title ? 'img' : undefined} aria-label={title} aria-hidden={title ? undefined : true}>
      {paths(pose, POSES[pose]).map((p) => <path key={p.ch} d={p.d} fill={tint && p.ch === 'R' ? tint[0] : tint && p.ch === 'r' ? tint[1] : p.fill} />)}
    </svg>
  );
});

/** 头顶冒出来的小图标（问号、省略号、灯泡、汗滴、信号），格子和小人一样大。 */
export const PixelIcon = memo(function PixelIcon({ icon, unit, className }: { icon: Icon; unit: number; className?: string }) {
  const rows = ICONS[icon];
  const w = Math.max(...rows.map((r) => r.length));
  return (
    <svg className={className} width={w * unit} height={rows.length * unit} viewBox={`0 0 ${w} ${rows.length}`} shapeRendering="crispEdges" aria-hidden="true">
      {paths('icon:' + icon, rows).map((p) => <path key={p.ch} d={p.d} fill={p.fill} />)}
    </svg>
  );
});
