import { PixelSprite } from './pet/PixelSprite';
// 线框图标：统一 24 网格、描边 1.8，颜色跟随文字（currentColor）。
// 只收界面里真用到的几枚，不引图标库——内网离线部署，少一个依赖少一处打包问题。
import type { CSSProperties, ReactElement } from 'react';

const PATHS: Record<string, ReactElement> = {
  plus: <path d="M12 5v14M5 12h14" />,
  book: <path d="M5 4.5A1.5 1.5 0 0 1 6.5 3H19v15H6.5A1.5 1.5 0 0 0 5 19.5zM5 19.5A1.5 1.5 0 0 0 6.5 21H19v-3M9 7h6" />,
  home: <path d="M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z" />,
  folder: <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />,
  file: <><path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z" /><path d="M14 3v6h6M8 13h8M8 17h5" /></>,
  wrench: <path d="M14.7 6.3a4 4 0 0 0-5.4 5.4L3 18l3 3 6.3-6.3a4 4 0 0 0 5.4-5.4l-2.5 2.5-2.4-.6-.6-2.4z" />,
  ticket: <><rect x="4" y="4" width="16" height="16" rx="3" /><path d="M8 9h8M8 13h8M8 17h4" /></>,
  shield: <path d="M12 3l8 3v6c0 4.5-3.4 8.3-8 9-4.6-.7-8-4.5-8-9V6z" />,
  chat: <path d="M4 5h16v11H9l-5 4z" />,
  settings: <><circle cx="12" cy="12" r="3" /><path d="M12 2v3M12 19v3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M2 12h3M19 12h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1" /></>,
  panel: <><rect x="3" y="4" width="18" height="16" rx="3" /><path d="M9 4v16" /></>,
  up: <path d="M12 19V5M5 12l7-7 7 7" />,
  logout: <path d="M15 4h3a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3M10 17l-5-5 5-5M5 12h11" />,
  user: <><circle cx="12" cy="8" r="4" /><path d="M4 21c1.5-4 4.5-6 8-6s6.5 2 8 6" /></>,
  clip: <path d="M21 11.5l-8.5 8.5a5 5 0 0 1-7-7L14 4.5a3.3 3.3 0 0 1 4.7 4.7l-8.5 8.5a1.7 1.7 0 0 1-2.4-2.4L15.5 7.5" />,
  x: <path d="M6 6l12 12M18 6L6 18" />,
  upload: <path d="M12 16V4M6 10l6-6 6 6M4 20h16" />,
  check: <path d="M5 12.5l4.5 4.5L19 7.5" />,
  thumbUp: <path d="M7 10v10H4.5a1 1 0 0 1-1-1v-8a1 1 0 0 1 1-1zM7 10l3.6-6.3a1.8 1.8 0 0 1 3.3 1.2L13.3 9H19a2 2 0 0 1 2 2.3l-1.2 6.9A2.2 2.2 0 0 1 17.6 20H7" />,
  thumbDown: <g transform="rotate(180 12 12)"><path d="M7 10v10H4.5a1 1 0 0 1-1-1v-8a1 1 0 0 1 1-1zM7 10l3.6-6.3a1.8 1.8 0 0 1 3.3 1.2L13.3 9H19a2 2 0 0 1 2 2.3l-1.2 6.9A2.2 2.2 0 0 1 17.6 20H7" /></g>,
  refresh: <path d="M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7" />,
  eye: <><path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z" /><circle cx="12" cy="12" r="3" /></>,
  slides: <><rect x="3" y="4" width="18" height="12" rx="1.5" /><path d="M12 16v4M8 20h8M7 8h6M7 11h4" /></>,
  play: <path d="M8 5.5l11 6.5-11 6.5z" fill="currentColor" />,
  boxOff: <rect x="3.5" y="3.5" width="17" height="17" rx="4" />,
  boxOn: <><rect x="3.5" y="3.5" width="17" height="17" rx="4" fill="currentColor" /><path d="M8 12.3l2.8 2.8L16.3 9" stroke="#fff" /></>,
  dot: <circle cx="12" cy="12" r="4" fill="currentColor" stroke="none" />,
  // 思考档位：自动 / 快速 / 标准 / 深度
  auto: <><circle cx="12" cy="12" r="3" /><path d="M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M5.6 18.4l2.1-2.1M16.3 7.7l2.1-2.1" /></>,
  bolt: <path d="M13 3L5 13.5h6L10 21l8-10.5h-6z" />,
  bulb: <path d="M9 18h6M10 21h4M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2V16h5v-.1c0-.8.4-1.5 1-2A6 6 0 0 0 12 3z" />,
  layers: <><path d="M12 3l9 5-9 5-9-5z" /><path d="M3 12.5l9 5 9-5" /><path d="M3 17l9 5 9-5" /></>,
  down: <path d="M6 9l6 6 6-6" />,
  right: <path d="M9 6l6 6-6 6" />,
  left: <path d="M15 6l-6 6 6 6" />,
  // 对话的「项目」：一个盒子
  box: <><path d="M3.5 7.5L12 3l8.5 4.5v9L12 21l-8.5-4.5z" /><path d="M3.5 7.5L12 12l8.5-4.5M12 12v9" /></>,
  search: <><circle cx="11" cy="11" r="6.5" /><path d="M16 16l4.5 4.5" /></>,
  link: <path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1" />,
  edit: <path d="M4 20h4L19 9l-4-4L4 16zM13.5 6.5l4 4" />,
  warn: <path d="M12 3l10 18H2zM12 10v5M12 18v.5" />,
};

export function Icon({ name, size = 17, stroke = 1.8, style, className }: {
  name: keyof typeof PATHS | string; size?: number; stroke?: number; style?: CSSProperties; className?: string;
}) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" style={style} className={className}
      strokeWidth={stroke} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {PATHS[name] ?? null}
    </svg>
  );
}

/** 助手头像：HTLAB 吉祥物的 8-bit 像素版。 */
export function BrandMark({ size = 30 }: { size?: number }) {
  return (
    <div className="chat-avatar mascot" style={{ width: size }} aria-hidden="true">
      <PixelSprite pose="idle" width={size} />
    </div>
  );
}
