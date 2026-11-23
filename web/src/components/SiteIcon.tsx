// 网站图标：服务器代取（用户电脑不一定能上外网），同一个网站只取一次；取不到就用网站首字母的小色块。
import { useEffect, useState } from 'react';
import { fetchBlobUrl } from '../lib/api';

const cache = new Map<string, Promise<string | null>>();
const load = (host: string) => {
  let p = cache.get(host);
  if (!p) {
    p = fetchBlobUrl(`/api/web/favicon?host=${encodeURIComponent(host)}`).catch(() => null);
    cache.set(host, p);
  }
  return p;
};

const TONES = ['#1F3A5F', '#0E6E5C', '#1D4A94', '#8A2E08', '#5C5C64', '#6A48F6', '#8F3D74', '#0E8A8A'];
const toneOf = (h: string) => TONES[[...h].reduce((a, c) => a + c.charCodeAt(0), 0) % TONES.length];
/** 首字母：去掉 www. 之类的前缀。 */
const letterOf = (h: string) => (h.replace(/^(www|m|en|cn)\./i, '')[0] ?? '·').toUpperCase();

export const hostOf = (u: string) => { try { return new URL(u).host; } catch { return u; } };

export function SiteIcon({ host, size = 16 }: { host: string; size?: number }) {
  const [src, setSrc] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    setSrc(null);
    if (host) void load(host.toLowerCase()).then((u) => { if (alive) setSrc(u); });
    return () => { alive = false; };
  }, [host]);
  const box = { width: size, height: size, flex: `0 0 ${size}px` };
  if (src) return <img className="site-ic" src={src} alt="" aria-hidden="true" style={box} />;
  return (
    <span className="site-ic fb" aria-hidden="true" style={{ ...box, background: toneOf(host), fontSize: Math.round(size * 0.6) }}>
      {letterOf(host)}
    </span>
  );
}
