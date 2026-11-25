// 跟日子有关的：今天是不是过节（首页问候和小人的道具一起换）、小人被点一下说的生活话、到点下班和深夜。
// 只说生活，不提工作——首页老板、客户都可能看到。
import type { Prop } from './sprite';

export interface Festival { key: string; title: string; sub: string; prop: Prop }

const F = {
  newYear: { title: '元旦快乐', sub: '新的一年，身体健康，万事顺意。', prop: 'firework' },
  spring: { title: '新春快乐', sub: '回家路上注意安全，替我们向家里人问好。', prop: 'lantern' },
  lantern: { title: '元宵快乐', sub: '记得吃碗汤圆，团团圆圆。', prop: 'tangyuan' },
  dragon: { title: '端午安康', sub: '吃个粽子，歇一歇。', prop: 'zongzi' },
  moon: { title: '中秋快乐', sub: '月圆人团圆，今晚早点回家。', prop: 'moon' },
  national: { title: '国庆快乐', sub: '假期出行注意安全，玩得开心。', prop: 'flag' },
} satisfies Record<string, Omit<Festival, 'key'>>;

// 农历节日的公历日期（月-日）。表到 2030 年，往后再接着补。
const LUNAR: Record<number, { spring: string; lantern: string; dragon: string; moon: string }> = {
  2026: { spring: '02-17', lantern: '03-03', dragon: '06-19', moon: '09-25' },
  2027: { spring: '02-06', lantern: '02-20', dragon: '06-09', moon: '09-15' },
  2028: { spring: '01-26', lantern: '02-09', dragon: '05-28', moon: '10-03' },
  2029: { spring: '02-13', lantern: '02-27', dragon: '06-16', moon: '09-22' },
  2030: { spring: '02-03', lantern: '02-17', dragon: '06-05', moon: '09-12' },
};

const dayOf = (y: number, md: string) => new Date(y, Number(md.slice(0, 2)) - 1, Number(md.slice(3)));
const within = (d: Date, start: Date, days: number) => {
  const t = new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  return t >= start.getTime() && t < start.getTime() + days * 86_400_000;
};

/** 今天在不在节日里：元旦 1–3 日，春节除夕到初六，元宵、端午、中秋当天，国庆 1–7 日。 */
export function festivalOf(d: Date): Festival | null {
  const y = d.getFullYear();
  const hit = (key: keyof typeof F) => ({ key, ...F[key] });
  if (within(d, dayOf(y, '01-01'), 3)) return hit('newYear');
  if (within(d, dayOf(y, '10-01'), 7)) return hit('national');
  const l = LUNAR[y];
  if (!l) return null;
  const spring = dayOf(y, l.spring);
  if (within(d, new Date(spring.getTime() - 86_400_000), 7)) return hit('spring');
  if (within(d, dayOf(y, l.lantern), 1)) return hit('lantern');
  if (within(d, dayOf(y, l.dragon), 1)) return hit('dragon');
  if (within(d, dayOf(y, l.moon), 1)) return hit('moon');
  return null;
}

/** 很晚了：22 点以后、凌晨 5 点以前。 */
export const isLate = (d: Date) => d.getHours() >= 22 || d.getHours() < 5;

/** 下班时间：18–22 点。周五说周末愉快。 */
export function byeLine(d: Date): string | null {
  const h = d.getHours();
  if (h < 18 || h >= 22) return null;
  return d.getDay() === 5 ? '周末愉快，好好歇歇' : '辛苦啦，路上慢点';
}

const TALK: Record<string, string[]> = {
  morning: ['喝口水再忙吧，别太累', '早饭吃了吗？', '今天也慢慢来'],
  noon: ['该吃饭啦，吃完歇会儿', '午饭别凑合'],
  afternoon: ['起来活动下肩颈', '喝口水，看看远处', '坐久了起来走两步'],
  evening: ['辛苦啦，早点回家', '晚饭记得按时吃'],
  late: ['这么晚了，早点休息'],
};

/** 点小人一下它说的话：按时段，几句里随便挑一句。 */
export function talkLine(d: Date): string {
  const h = d.getHours();
  const slot = isLate(d) ? 'late' : h < 11 ? 'morning' : h < 14 ? 'noon' : h < 18 ? 'afternoon' : 'evening';
  const list = TALK[slot];
  return list[Math.floor(Math.random() * list.length)];
}
