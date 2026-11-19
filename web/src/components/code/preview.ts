// 预览面板（对话页右侧）的打开方式：回答里的网页卡片、网上来源、文档卡片、文件都经这里打开。
// 放在 components 下，正文渲染（Prose）和各种卡片都能用；对话页提供实现，别的页面没有面板时为 null。
import { createContext, useContext } from 'react';

export type PreviewItem =
  | { kind: 'html'; code: string }
  | { kind: 'web'; url: string; title?: string; label?: string; quotes?: string[] }
  | { kind: 'doc'; title: string; markdown: string }
  | { kind: 'file'; src: string; fileName: string };

export const PreviewCtx = createContext<((item: PreviewItem) => void) | null>(null);
export const usePreview = () => useContext(PreviewCtx);

/** 网页代码的标题：<title> 或第一个 <h1>，都没有就叫「网页」。 */
export function htmlTitle(code: string): string {
  const t = /<title[^>]*>([\s\S]*?)<\/title>/i.exec(code)?.[1] ?? /<h1[^>]*>([\s\S]*?)<\/h1>/i.exec(code)?.[1] ?? '';
  const s = t.replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').trim();
  return s.slice(0, 40) || '网页';
}

/** 回答文字里的 ```html 代码块，按出现顺序。 */
export function htmlBlocks(text: string): string[] {
  const out: string[] = [];
  const re = /```(?:html|htm)\s*\n([\s\S]*?)```/gi;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) if (m[1].trim().length > 20) out.push(m[1].replace(/\n$/, ''));
  return out;
}
