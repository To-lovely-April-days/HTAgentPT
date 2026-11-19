// 网页预览的准备：CDN 引用的常用库换成本系统 /vendor/ 下的副本、补外壳、找外网资源；新窗口里同样放进沙箱。
// 库的代码直接写进页面（不是给个地址让沙箱自己去取）：沙箱的来源是「空」，浏览器会拦它访问内网地址，
// 连本系统的服务器也取不到；写进去就跟网络无关了。

export const SANDBOX = 'allow-scripts allow-forms allow-modals allow-popups';

const CDN = /https?:\/\/(?:unpkg\.com|cdn\.jsdelivr\.net\/(?:npm|gh)|cdnjs\.cloudflare\.com\/ajax\/libs|cdn\.bootcdn\.net\/ajax\/libs|cdn\.bootcss\.com|lib\.baomitu\.com|cdn\.staticfile\.(?:org|net)|registry\.npmmirror\.com|npm\.elemecdn\.com|cdn\.tailwindcss\.com)[^"'\s>)]*/gi;

function vendorFor(url: string): string | null {
  const u = url.toLowerCase();
  if (u.includes('cdn.tailwindcss.com') || u.includes('@tailwindcss/browser')) return 'tailwind.js';
  if (u.includes('react-dom')) return 'react-dom.js';
  if (/\/react@|\/react\/|\/react\.(production|development)/.test(u)) return 'react.js';
  if (u.includes('babel')) return 'babel.js';
  if (/\/vue@|\/vue\/|vue\.global/.test(u) && !u.includes('vue-router')) return 'vue.js';
  if (u.includes('echarts')) return 'echarts.js';
  return null;
}

const vendorCache = new Map<string, Promise<string | null>>();
function vendorText(name: string): Promise<string | null> {
  let p = vendorCache.get(name);
  if (!p) {
    p = fetch(`/vendor/${name}`).then((r) => (r.ok ? r.text() : null)).catch(() => null);
    vendorCache.set(name, p);
  }
  return p;
}

const SCRIPT_SRC = /<script\b([^>]*?)\bsrc\s*=\s*["']([^"']+)["']([^>]*)>\s*<\/script\s*>/gi;

/** 认得的 CDN 库：把 <script src="CDN"> 换成带库代码的 <script>。取不到的保持原样。 */
export async function inlineVendors(html: string): Promise<string> {
  const hits: { tag: string; attrs: string; name: string }[] = [];
  let m: RegExpExecArray | null;
  SCRIPT_SRC.lastIndex = 0;
  while ((m = SCRIPT_SRC.exec(html)) !== null) {
    CDN.lastIndex = 0;
    const name = CDN.test(m[2]) ? vendorFor(m[2]) : null;
    if (name) hits.push({ tag: m[0], attrs: (m[1] + m[3]).replace(/\s(integrity|crossorigin|referrerpolicy)\s*=\s*["'][^"']*["']/gi, ''), name });
  }
  let out = html;
  for (const h of hits) {
    const text = await vendorText(h.name);
    if (text == null) continue;
    out = out.replace(h.tag, () => `<script${h.attrs}>${text.replace(/<\/script/gi, '<\\/script')}</script>`);
  }
  return out;
}

/** 预览前的处理：不是完整页面的补上外壳；找出换不了的外网地址。认得的 CDN 库由 inlineVendors 写进页面。 */
export function prepareHtml(code: string): { html: string; external: string[] } {
  let html = code;
  if (!/<html[\s>]/i.test(html) && !/<body[\s>]/i.test(html)) {
    html = `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">`
      + `<style>body{font-family:system-ui,-apple-system,"PingFang SC","Microsoft YaHei",sans-serif;margin:16px;color:#141414}</style></head><body>${html}</body></html>`;
  } else if (!/<meta[^>]+charset/i.test(html)) {
    html = html.replace(/<head(\s[^>]*)?>/i, (m) => `${m}<meta charset="utf-8">`);
  }
  const external = new Set<string>();
  const re = /(?:src|href)\s*=\s*["'](https?:\/\/[^"']+)["']|url\(\s*["']?(https?:\/\/[^"')]+)/gi;
  let m: RegExpExecArray | null;
  while ((m = re.exec(html)) !== null) {
    const url = m[1] ?? m[2];
    CDN.lastIndex = 0;
    if (CDN.test(url) && vendorFor(url)) continue;
    try { external.add(new URL(url).host); } catch { /* 不是网址 */ }
  }
  return { html, external: [...external] };
}

/** 新窗口打开：新窗口里只放一个同样隔离的沙箱框，页面本身仍然拿不到本系统的任何东西。 */
export function openInWindow(html: string, title: string) {
  const w = window.open('', '_blank');
  if (!w) return;
  const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!));
  w.document.write(`<!doctype html><html><head><meta charset="utf-8"><title>${esc(title)}</title>`
    + `<style>html,body{margin:0;height:100%}iframe{border:0;width:100%;height:100%;display:block}</style></head><body><iframe sandbox="${SANDBOX}"></iframe></body></html>`);
  w.document.close();
  const f = w.document.querySelector('iframe');
  if (f) f.srcdoc = html;
  w.opener = null;
}

