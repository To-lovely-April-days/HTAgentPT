// 代码块用的纯函数：高亮结果按行切开、把文字存成文件。

/** 高亮后的 HTML 按行切开：跨行的 span 在每行开头补开、结尾补关，每行单独成一段，行号才能和换行对齐。 */
export function splitLines(html: string): string[] {
  const out: string[] = [];
  const open: string[] = [];
  let line = '';
  const re = /<span[^>]*>|<\/span>|\n|[^<\n]+|</g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(html)) !== null) {
    const t = m[0];
    if (t === '\n') { out.push(line + '</span>'.repeat(open.length)); line = open.join(''); }
    else if (t.startsWith('<span')) { open.push(t); line += t; }
    else if (t === '</span>') { open.pop(); line += t; }
    else line += t;
  }
  out.push(line + '</span>'.repeat(open.length));
  return out;
}

export function downloadText(name: string, text: string, type = 'text/plain') {
  const url = URL.createObjectURL(new Blob([text], { type: `${type};charset=utf-8` }));
  const a = document.createElement('a');
  a.href = url; a.download = name; a.click();
  setTimeout(() => URL.revokeObjectURL(url), 2000);
}


/** SVG 文本 → PNG 下载（画到画布上再导出）。 */
export async function svgToPng(svg: string, name: string, scale = 2) {
  const img = new Image();
  const url = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
  await new Promise<void>((ok, fail) => { img.onload = () => ok(); img.onerror = () => fail(new Error('图片转换失败')); img.src = url; });
  const w = img.naturalWidth || 800, h = img.naturalHeight || 600;
  const c = document.createElement('canvas');
  c.width = w * scale; c.height = h * scale;
  const g = c.getContext('2d')!;
  g.fillStyle = '#fff'; g.fillRect(0, 0, c.width, c.height);
  g.drawImage(img, 0, 0, c.width, c.height);
  const a = document.createElement('a');
  a.href = c.toDataURL('image/png'); a.download = name; a.click();
}

/** 页面里一个 svg 元素 → 独立的 SVG 文本（补上命名空间和尺寸）。 */
export function serializeSvg(el: SVGSVGElement): string {
  const clone = el.cloneNode(true) as SVGSVGElement;
  clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
  const box = el.getBoundingClientRect();
  if (!clone.getAttribute('width')) clone.setAttribute('width', String(Math.round(box.width)));
  if (!clone.getAttribute('height')) clone.setAttribute('height', String(Math.round(box.height)));
  return new XMLSerializer().serializeToString(clone);
}

