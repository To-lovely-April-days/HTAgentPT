// 回答引用的句子在网页正文里定位（去掉空白后比对，再映射回原文位置）。

const norm = (s: string) => s.replace(/\s+/g, '').toLowerCase();

/** 引用的那段拆成句子（够长的才找），用来在正文里定位。 */
export function quoteSentences(quotes: string[]): string[] {
  const out = new Set<string>();
  for (const q of quotes)
    for (const s of q.split(/(?<=[。！？!?；;\n])|(?<=\.)\s/)) {
      const t = s.trim();
      if (norm(t).length >= 8) out.add(t);
    }
  return [...out];
}

/** 在一段文字里找出要标黄的区间（按去掉空白后的文字比对，再映射回原文位置）。 */
export function findMarks(text: string, sentences: string[]): { start: number; end: number; q: number }[] {
  const map: number[] = [];
  let flat = '';
  for (let i = 0; i < text.length; i++) if (!/\s/.test(text[i])) { map.push(i); flat += text[i].toLowerCase(); }
  const marks: { start: number; end: number; q: number }[] = [];
  sentences.forEach((s, q) => {
    const n = norm(s).replace(/[。！？!?；;.]$/, '');
    if (n.length < 8) return;
    const at = flat.indexOf(n);
    if (at < 0) return;
    const start = map[at], end = map[at + n.length - 1] + 1;
    if (!marks.some((m) => start < m.end && end > m.start)) marks.push({ start, end, q });
  });
  return marks.sort((a, b) => a.start - b.start);
}

