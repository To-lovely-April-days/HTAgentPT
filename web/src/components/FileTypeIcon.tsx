// 文件类型图标：Word、Excel、PPT、PDF、图片各一枚 Lucide 线框图标，配各自的颜色（Word 蓝、Excel 绿、PPT 橙、PDF 红）。
// 不用微软、Adobe 的官方标志（商标，且界面图标统一用 Lucide）。卡片上用「方块」样式（图标 + 扩展名），按钮、小标签里用「小图标」样式。
import { Icon } from './Icons';

export type FileKind = 'word' | 'excel' | 'ppt' | 'pdf' | 'image' | 'cad' | 'web' | 'other';

const KIND: Record<FileKind, { icon: string; fg: string; bg: string }> = {
  word: { icon: 'file', fg: '#2A5699', bg: '#E9EFF8' },
  excel: { icon: 'fileSheet', fg: '#1D6F42', bg: '#E6F2EC' },
  ppt: { icon: 'slides', fg: '#B5471B', bg: '#FBEDE6' },
  pdf: { icon: 'fileType', fg: '#B42318', bg: '#FDECEC' },
  image: { icon: 'image', fg: '#6B3FA0', bg: '#F1EBF8' },
  cad: { icon: 'box', fg: '#4A4A4A', bg: '#F1F1EF' },
  web: { icon: 'link', fg: '#2A4A85', bg: '#E8EEF8' },
  other: { icon: 'file', fg: '#4A4A4A', bg: '#F1F1EF' },
};

/** 文件名或扩展名（「报价单.xlsx」「XLSX」「pdf」都行）→ 种类。 */
export function fileKind(nameOrExt: string): FileKind {
  const s = (nameOrExt || '').toLowerCase();
  const ext = s.includes('.') ? s.slice(s.lastIndexOf('.') + 1) : s;
  if (['doc', 'docx', 'docm', 'dotx', 'wps', 'rtf', 'odt'].includes(ext)) return 'word';
  if (['xls', 'xlsx', 'xlsm', 'csv', 'et', 'ods'].includes(ext)) return 'excel';
  if (['ppt', 'pptx', 'potx', 'odp'].includes(ext)) return 'ppt';
  if (ext === 'pdf') return 'pdf';
  if (['png', 'jpg', 'jpeg', 'webp', 'gif', 'bmp', 'svg'].includes(ext)) return 'image';
  if (['dxf', 'dwg', 'step', 'stp'].includes(ext)) return 'cad';
  if (ext === '网页' || ext === 'web' || ext === 'html') return 'web';
  return 'other';
}

function extOf(nameOrExt: string): string {
  const s = nameOrExt || '';
  return (s.includes('.') ? s.slice(s.lastIndexOf('.') + 1) : s).toUpperCase().slice(0, 4);
}

/** 卡片上的方块：浅底色 + 彩色图标 + 扩展名。 */
export function FileTile({ name, size = 34 }: { name: string; size?: number }) {
  const k = KIND[fileKind(name)];
  const ext = extOf(name);
  return (
    <span className="ft-tile" style={{ width: size, height: size + 4, background: k.bg, color: k.fg }} aria-hidden="true">
      <Icon name={k.icon} size={Math.round(size * 0.5)} stroke={1.9} />
      {ext && <b>{ext}</b>}
    </span>
  );
}

/** 按钮、小标签里的小图标：只有彩色图标。 */
export function FileGlyph({ name, size = 14 }: { name: string; size?: number }) {
  const k = KIND[fileKind(name)];
  return <Icon name={k.icon} size={size} stroke={2} style={{ color: k.fg, flexShrink: 0 }} />;
}
