import React from 'react';
import { fetchBlobUrl } from '../lib/api';
import type { Classification } from '../lib/types';
import { CLS_LABEL, CLS_PILL } from '../lib/types';

/** 密级徽标：三色只表密级，尺寸恒定（h19 · 0 6 · r3 · 11px · w500）。 */
export function ClsBadge({ cls }: { cls: Classification }) {
  return <span className={CLS_PILL[cls]}>{CLS_LABEL[cls]}</span>;
}

export function Spinner({ text }: { text?: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8, color: 'var(--ink-3)', fontSize: 12.5 }}>
      <span className="spin" style={{
        width: 14, height: 14, border: '2px solid var(--line-strong)',
        borderTopColor: 'var(--accent)', borderRadius: '50%', display: 'inline-block',
        animation: 'ht-spin .8s linear infinite',
      }} />
      {text ?? '加载中…'}
      <style>{'@keyframes ht-spin{to{transform:rotate(360deg)}}'}</style>
    </div>
  );
}

export function ErrorBox({ message }: { message: string }) {
  return (
    <div style={{
      padding: '10px 12px', borderRadius: 5, fontSize: 12.5, lineHeight: 1.7,
      background: 'var(--cls-conf-bg)', border: '1px solid var(--cls-conf-line)', color: 'var(--cls-conf)',
    }}>{message}</div>
  );
}

export function InfoBox({ children, tone = 'amber' }: { children: React.ReactNode; tone?: 'amber' | 'neutral' }) {
  const s = tone === 'amber'
    ? { background: 'var(--cls-int-bg)', border: '1px solid var(--cls-int-line)', color: 'var(--cls-int)' }
    : { background: '#f8fafc', border: '1px solid var(--line-soft)', color: 'var(--ink-2)' };
  return <div style={{ padding: '10px 12px', borderRadius: 5, fontSize: 12, lineHeight: 1.8, ...s }}>{children}</div>;
}

/** 带鉴权的图片（FR-4.9 来源出图）：普通 <img> 带不了 Authorization 头，
 * 经 fetch 转对象 URL 渲染；卸载即回收，失败给一行占位而不是裂图标。 */
export function AuthImage({ src, alt, style, onClick, title }: {
  src: string; alt?: string; style?: React.CSSProperties; onClick?: () => void; title?: string;
}) {
  const [url, setUrl] = React.useState<string | null>(null);
  const [failed, setFailed] = React.useState(false);
  React.useEffect(() => {
    let alive = true;
    let obj: string | null = null;
    setUrl(null);
    setFailed(false);
    fetchBlobUrl(src)
      .then((u) => { if (alive) { obj = u; setUrl(u); } else URL.revokeObjectURL(u); })
      .catch(() => { if (alive) setFailed(true); });
    return () => { alive = false; if (obj) URL.revokeObjectURL(obj); };
  }, [src]);
  if (failed) return <span className="hint">图片加载失败</span>;
  if (!url) return <span className="hint" style={{ fontSize: 11 }}>图片载入中…</span>;
  return <img src={url} alt={alt ?? ''} title={title} style={style} onClick={onClick} />;
}
