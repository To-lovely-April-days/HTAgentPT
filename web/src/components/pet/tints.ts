// 像素小人的颜色：主助手是红色（不换），分头查的子代理 1、2、3… 是蓝、绿、黄、紫……（换掉头带和衣服上的红色）。
export const AGENT_TINTS: readonly (readonly [string, string])[] = [
  ['#2F6FD6', '#1D4A94'], ['#1E9E63', '#126B42'], ['#E39B00', '#9A6400'], ['#7A4FD0', '#4F2F96'], ['#0E8A8A', '#095E5E'], ['#C2410C', '#8A2E08'],
];

/** who：0 主助手（返回 undefined，用原来的红色），1、2… 子代理。 */
export const tintOf = (who: number): readonly [string, string] | undefined => (who > 0 ? AGENT_TINTS[(who - 1) % AGENT_TINTS.length] : undefined);
