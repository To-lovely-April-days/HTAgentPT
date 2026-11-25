// 首页顶上跟着天气变的那一片：晴天一个像素太阳、多云飘几朵云、下雨雨丝、雷雨乌云偶尔亮一下、下雪飘雪花、雾几层雾带、大风刮叶子、晚上一段夜空和星星。
// 只占问候和输入框这一块，往下渐渐淡掉；不挡点击。开了「减少动态效果」的只显示不动（CSS 里关掉动画）。
import { useMemo } from 'react';
import { PixelProp } from '../../../components/pet/PixelSprite';

/** 一串位置固定的随机数（同一个种子每次一样，页面重画时雨点不乱跳）。 */
function seeded(n: number, seed: number) {
  let s = seed;
  return Array.from({ length: n }, () => { s = (s * 9301 + 49297) % 233280; return s / 233280; });
}

export function WeatherSky({ kind, night }: { kind: string; night: boolean }) {
  const r = useMemo(() => seeded(240, 7), []);
  const pick = (i: number) => r[i % r.length];
  const rain = kind === 'rain' || kind === 'storm';
  return (
    <div className={'hm-sky' + (night ? ' night' : '') + ' sky-' + kind} aria-hidden="true">
      {night && Array.from({ length: 12 }, (_, i) => (
        <i key={'s' + i} className="sky-star" style={{ left: `${4 + pick(i) * 92}%`, top: `${8 + pick(i + 40) * 30}%`, animationDelay: `-${pick(i + 80) * 2.4}s` }} />
      ))}
      {night && <PixelProp prop="crescent" unit={4} className="sky-moon" />}
      {!night && kind === 'clear' && <><span className="sky-glow" /><PixelProp prop="sun" unit={4} className="sky-sun" /></>}
      {(kind === 'cloudy' || kind === 'overcast' || kind === 'wind') && [0, 1, 2, ...(kind === 'overcast' ? [3, 4] : [])].map((i) => (
        <span key={'c' + i} className="sky-cloud" style={{ top: `${6 + i * 13}%`, animationDelay: `-${pick(i + 3) * 40}s`, animationDuration: `${kind === 'wind' ? 14 + i * 3 : 40 + i * 8}s` }}>
          <PixelProp prop="cloud" unit={i % 2 ? 2 : 3} />
        </span>
      ))}
      {kind === 'storm' && [0, 1, 2].map((i) => (
        <span key={'d' + i} className="sky-dark" style={{ left: `${8 + i * 32}%`, animationDelay: `-${i * 1.3}s` }}><PixelProp prop="darkCloud" unit={5} /></span>
      ))}
      {rain && Array.from({ length: kind === 'storm' ? 90 : 60 }, (_, i) => (
        <i key={'r' + i} className="sky-rain" style={{ left: `${pick(i) * 100}%`, animationDelay: `-${pick(i + 100) * 3}s`, animationDuration: `${0.7 + pick(i + 50) * 0.35}s` }} />
      ))}
      {kind === 'snow' && Array.from({ length: 40 }, (_, i) => (
        <span key={'f' + i} className="sky-snow" style={{ left: `${pick(i) * 100}%`, animationDelay: `-${pick(i + 100) * 8}s`, animationDuration: `${5 + pick(i + 50) * 3}s` }}>
          <PixelProp prop="flake" unit={2} />
        </span>
      ))}
      {(kind === 'fog' || kind === 'haze') && [0, 1, 2, 3].map((i) => (
        <i key={'g' + i} className={'sky-fog' + (kind === 'haze' ? ' haze' : '')} style={{ top: `${10 + i * 20}%`, animationDelay: `-${i * 4}s` }} />
      ))}
      {kind === 'wind' && Array.from({ length: 12 }, (_, i) => (
        <span key={'l' + i} className="sky-leaf" style={{ top: `${10 + pick(i) * 55}%`, animationDelay: `-${pick(i + 30) * 3}s`, animationDuration: `${2.2 + pick(i + 60)}s` }}>
          <PixelProp prop="leaf" unit={2} />
        </span>
      ))}
    </div>
  );
}

/** 输入框上沿：下雪积一层雪、旁边一个雪人；下雨溅小水花。 */
export function ComposerWeather({ kind }: { kind: string }) {
  if (kind === 'snow') return <><span className="hm-snowcap" aria-hidden="true" /><PixelProp prop="snowman" unit={3} className="hm-snowman" /></>;
  if (kind === 'rain' || kind === 'storm') {
    return <>{[8, 22, 41, 63, 79, 92].map((x, i) => <i key={x} className="hm-splash" style={{ left: `${x}%`, animationDelay: `-${i * 0.37}s` }} aria-hidden="true" />)}</>;
  }
  return null;
}
