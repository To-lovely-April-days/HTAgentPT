// 把回答做成简单的 PPT：封面一页，每个二级标题（或每段要点）一页，表格单独一页，最后一页参考资料。
// 在浏览器里生成，不经服务器。
import PptxGenJS from 'pptxgenjs';

type Slide = { title: string; bullets: string[]; table?: string[][] };

const clean = (s: string) => s.replace(/\*\*/g, '').replace(/`/g, '').replace(/\[([^\]]+)\]\((?:cite|flag):[^)]*\)/g, '[$1]').trim();

export function splitSlides(title: string, md: string): Slide[] {
  const slides: Slide[] = [];
  let cur: Slide = { title, bullets: [] };
  const lines = md.split('\n');
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i];
    const h = /^#{1,3}\s+(.*)/.exec(l);
    if (h) {
      if (cur.bullets.length || cur.table) slides.push(cur);
      cur = { title: clean(h[1]), bullets: [] };
      continue;
    }
    if (l.trim().startsWith('|') && /^\s*\|?\s*:?-{2,}/.test(lines[i + 1] ?? '')) {
      const cells = (x: string) => x.trim().replace(/^\||\|$/g, '').split('|').map((c) => clean(c));
      const rows = [cells(l)];
      i += 2;
      while (i < lines.length && lines[i].trim().startsWith('|')) rows.push(cells(lines[i++]));
      i--;
      if (cur.bullets.length || cur.table) slides.push(cur);
      slides.push({ title: cur.title, bullets: [], table: rows.slice(0, 14) });
      cur = { title: cur.title, bullets: [] };
      continue;
    }
    const t = clean(l.replace(/^\s*([-*]|\d+[.、)])\s+/, ''));
    if (!t) continue;
    cur.bullets.push(t);
    if (cur.bullets.length >= 7) { slides.push(cur); cur = { title: cur.title + '（续）', bullets: [] }; }
  }
  if (cur.bullets.length || cur.table) slides.push(cur);
  return slides;
}

export async function buildPpt(title: string, md: string, sources: string[]) {
  const pptx = new PptxGenJS();
  pptx.layout = 'LAYOUT_WIDE';
  const font = 'Microsoft YaHei';
  const cover = pptx.addSlide();
  cover.addText(title, { x: 0.8, y: 2.6, w: 11.7, h: 1.2, fontSize: 34, bold: true, fontFace: font, color: '1F1F1F' });
  cover.addText(new Date().toLocaleDateString('zh-CN'), { x: 0.8, y: 3.9, w: 11.7, h: 0.5, fontSize: 14, fontFace: font, color: '777777' });
  for (const s of splitSlides(title, md)) {
    const sl = pptx.addSlide();
    sl.addText(s.title, { x: 0.6, y: 0.35, w: 12.1, h: 0.8, fontSize: 24, bold: true, fontFace: font, color: '1F1F1F' });
    if (s.table) {
      sl.addTable(s.table.map((r, ri) => r.map((c) => ({ text: c, options: { bold: ri === 0, fill: { color: ri === 0 ? 'F1F1EF' : 'FFFFFF' } } }))),
        { x: 0.6, y: 1.3, w: 12.1, fontSize: 12, fontFace: font, border: { type: 'solid', color: 'DDDDDA', pt: 0.75 }, autoPage: false });
    } else {
      sl.addText(s.bullets.map((b) => ({ text: b, options: { bullet: true, breakLine: true } })),
        { x: 0.7, y: 1.3, w: 11.9, h: 5.6, fontSize: 16, fontFace: font, color: '333333', valign: 'top', paraSpaceAfter: 8 });
    }
  }
  if (sources.length) {
    const sl = pptx.addSlide();
    sl.addText('参考资料', { x: 0.6, y: 0.35, w: 12.1, h: 0.8, fontSize: 24, bold: true, fontFace: font });
    sl.addText(sources.slice(0, 14).join('\n'), { x: 0.7, y: 1.3, w: 11.9, h: 5.6, fontSize: 12, fontFace: font, color: '555555', valign: 'top' });
  }
  await pptx.writeFile({ fileName: `${title}.pptx` });
}
