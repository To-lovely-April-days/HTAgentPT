// 周五下午到周日，首页「需要你看的」上面一张「这周我做成了」：问了几个问题、做好几份文件、查了几次资料，来回最多的是哪件事。
// 数字都是系统里真记下的；只给本人看，不排名。点「收起」这一周就不再出现，下周一自动没了。
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get } from '../../../lib/api';
import { PixelProp, PixelSprite } from '../../../components/pet/PixelSprite';

interface Week {
  show: boolean; from: string; to: string; questions: number; files: number; lookups: number;
  hardest: { title: string; rounds: number; files: number } | null;
}

const HIDE_KEY = 'hm-week-hide';
const md = (iso: string) => { const [, m, d] = iso.split('-').map(Number); return `${m} 月 ${d} 日`; };

export function WeekCard({ name }: { name: string }) {
  const q = useQuery({ queryKey: ['home-week'], queryFn: () => get<Week>('/api/home/week'), staleTime: 60 * 60_000, retry: false });
  const [hidden, setHidden] = useState(() => { try { return localStorage.getItem(HIDE_KEY); } catch { return null; } });
  const w = q.data;
  if (!w?.show || hidden === w.from) return null;
  const hide = () => { try { localStorage.setItem(HIDE_KEY, w.from); } catch { /* 存不了就只收这一次 */ } setHidden(w.from); };
  const h = w.hardest;
  return (
    <section className="wk" aria-label="这周我做成了">
      <div className="wk-hd">
        <span className="wk-pet" aria-hidden="true"><PixelSprite pose="cheer" width={36} /><PixelProp prop="trophy" unit={2} /></span>
        <span className="wk-t"><b>这周辛苦了{name ? `，${name}` : ''}</b><span>{md(w.from)} – {md(w.to)}</span></span>
        <button type="button" className="hm-link" onClick={hide}>收起</button>
      </div>
      <div className="wk-nums">
        <span><b className="tn">{w.questions}</b>个问题</span>
        <span><b className="tn">{w.files}</b>份做好的文件</span>
        <span><b className="tn">{w.lookups}</b>次查资料</span>
      </div>
      {h && (
        <p className="wk-note">来回最多的一件：<b>{h.title}</b>，来回 {h.rounds} 轮{h.files > 0 ? `，做出 ${h.files} 份文件` : ''}。</p>
      )}
    </section>
  );
}
