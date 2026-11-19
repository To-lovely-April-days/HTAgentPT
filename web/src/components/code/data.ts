// 数据类代码块的解析：CSV、JSON 报错位置。

/** CSV / TSV 解析：认引号里的逗号、换行和 "" 转义。 */
export function parseCsv(text: string, sep?: string): string[][] {
  const d = sep ?? (text.split('\n', 1)[0].includes('\t') ? '\t' : text.split('\n', 1)[0].includes(';') && !text.split('\n', 1)[0].includes(',') ? ';' : ',');
  const rows: string[][] = [];
  let row: string[] = [], cell = '', q = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (q) {
      if (c === '"' && text[i + 1] === '"') { cell += '"'; i++; }
      else if (c === '"') q = false;
      else cell += c;
    } else if (c === '"' && cell === '') q = true;
    else if (c === d) { row.push(cell.trim()); cell = ''; }
    else if (c === '\n' || c === '\r') {
      if (c === '\r' && text[i + 1] === '\n') i++;
      row.push(cell.trim()); cell = '';
      if (row.some((x) => x !== '')) rows.push(row);
      row = [];
    } else cell += c;
  }
  row.push(cell.trim());
  if (row.some((x) => x !== '')) rows.push(row);
  return rows;
}

/** JSON.parse 的报错里有字符位置，换成第几行。 */
export function jsonErrorLine(code: string, e: unknown): number | null {
  const m = /position (\d+)/.exec(e instanceof Error ? e.message : '') ?? /line (\d+)/.exec(e instanceof Error ? e.message : '');
  if (!m) return null;
  if (/line/.test(m[0])) return Number(m[1]);
  return code.slice(0, Number(m[1])).split('\n').length;
}

