// 首页天气（公司所在城市，后台设置里填；全公司共用，30 分钟取一次）。没填城市或取不到时 ok=false，首页和小人都照旧。
import { useQuery } from '@tanstack/react-query';
import { get } from '../../lib/api';

export interface Weather {
  ok: boolean; city?: string; temp?: number; min?: number; max?: number;
  /** clear 晴 / cloudy 多云 / overcast 阴 / rain 雨 / storm 雷雨 / snow 雪 / fog 雾 / haze 霾 / wind 大风 */
  kind?: string; label?: string;
  /** 小人穿哪一套（见 outfit.ts） */
  outfit?: string; veryHot?: boolean; isDay?: boolean;
  /** 问候的小字要换成的提醒（下雨带伞、降温多穿……），没有就照用原来的 */
  tip?: string | null;
}

export function useWeather() {
  return useQuery({
    queryKey: ['home-weather'], queryFn: () => get<Weather>('/api/home/weather'),
    staleTime: 30 * 60_000, refetchInterval: 30 * 60_000, retry: false,
  });
}
