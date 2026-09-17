// 模型输出的正文渲染。对话模型答题时天然会用标记语法写标题、要点、表格，
// 按纯文本显示就是满屏星号和横杠——这里把常用的那一小撮解析成真正的排版。
//
// 只生成 React 节点，不往 DOM 里塞任何字符串 HTML：语料里混进标签也只会当文字显示。
// 支持：标题、要点与编号列表、表格、引用、分隔线、粗体、行内代码、来源角标 [1]。
import React from 'react';

/** 行内：**粗**、`代码`、来源角标 [1]。角标给了 onCite 就是可点的。 */
function inline(text: string, onCite?: (n: number) => void, keyBase = ''): React.ReactNode[] {
  const out: React.ReactNode[] = [];
  const re = /\*\*(.+?)\*\*|`([^`]+?)`|\[(\d{1,2})\]/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    if (m.index > last) out.push(text.slice(last, m.index));
    const k = `${keyBase}-${m.index}`;
    if (m[1] !== undefined) out.push(<strong key={k}>{m[1]}</strong>);
    else if (m[2] !== undefined) out.push(<code key={k} className="pcode">{m[2]}</code>);
    else {
      const n = Number(m[3]);
      out.push(onCite
        ? <button key={k} type="button" className="pcite" title={`看第 ${n} 条来源`} onClick={() => onCite(n)}>{n}</button>
        : <span key={k} className="pcite">{n}</span>);
    }
    last = m.index + m[0].length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

const H = /^(#{1,4})\s+(.*)$/;
const BULLET = /^\s*[-*·]\s+(.*)$/;
const NUMBER = /^\s*(\d{1,2})[.、)]\s+(.*)$/;
const QUOTE = /^>\s?(.*)$/;
const RULE = /^\s*([-*_])\s*\1\s*\1[\s\-*_]*$/;
const ROW = /^\s*\|(.+)\|\s*$/;
const SEP = /^[\s|:\-—]+$/;   // 表格的分隔行 |---|---|

const cells = (line: string) => line.replace(/^\s*\|/, '').replace(/\|\s*$/, '').split('|').map((c) => c.trim());

/** 模型输出的一段文字 → 排好版的块。 */
export function Prose({ text, onCite, style }: {
  text: string; onCite?: (n: number) => void; style?: React.CSSProperties;
}) {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  const blocks: React.ReactNode[] = [];
  let para: string[] = [];

  const flushPara = () => {
    if (para.length === 0) return;
    blocks.push(<p key={`p${blocks.length}`}>{inline(para.join('\n'), onCite, `p${blocks.length}`)}</p>);
    para = [];
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    if (line.trim() === '') { flushPara(); continue; }

    if (RULE.test(line)) { flushPara(); blocks.push(<hr key={`h${blocks.length}`} />); continue; }

    const h = H.exec(line);
    if (h) {
      flushPara();
      const lv = Math.min(h[1].length, 4);
      blocks.push(<div key={`t${blocks.length}`} className={`ph ph${lv}`}>{inline(h[2], onCite, `t${i}`)}</div>);
      continue;
    }

    // 表格：连续的 | … | 行，第二行是分隔行才当表格，否则按普通文字
    if (ROW.test(line) && i + 1 < lines.length && ROW.test(lines[i + 1]) && SEP.test(cells(lines[i + 1]).join(''))) {
      flushPara();
      const head = cells(line);
      const body: string[][] = [];
      let j = i + 2;
      for (; j < lines.length && ROW.test(lines[j]); j++) body.push(cells(lines[j]));
      blocks.push(
        <table key={`tb${blocks.length}`} className="ptable">
          <thead><tr>{head.map((c, x) => <th key={x}>{inline(c, onCite, `th${i}${x}`)}</th>)}</tr></thead>
          <tbody>{body.map((r, y) => (
            <tr key={y}>{head.map((_, x) => <td key={x}>{inline(r[x] ?? '', onCite, `td${y}${x}`)}</td>)}</tr>
          ))}</tbody>
        </table>,
      );
      i = j - 1;
      continue;
    }

    // 列表：连续同类行合成一个列表
    if (BULLET.test(line) || NUMBER.test(line)) {
      flushPara();
      const ordered = !BULLET.test(line);
      const items: string[] = [];
      let j = i;
      for (; j < lines.length; j++) {
        const b = BULLET.exec(lines[j]);
        const n = NUMBER.exec(lines[j]);
        if (!ordered && b) items.push(b[1]);
        else if (ordered && n) items.push(n[2]);
        else break;
      }
      const li = items.map((t, x) => <li key={x}>{inline(t, onCite, `li${i}${x}`)}</li>);
      blocks.push(ordered
        ? <ol key={`l${blocks.length}`} className="plist">{li}</ol>
        : <ul key={`l${blocks.length}`} className="plist">{li}</ul>);
      i = j - 1;
      continue;
    }

    const q = QUOTE.exec(line);
    if (q) {
      flushPara();
      const items: string[] = [q[1]];
      let j = i + 1;
      for (; j < lines.length; j++) {
        const nq = QUOTE.exec(lines[j]);
        if (!nq) break;
        items.push(nq[1]);
      }
      blocks.push(<blockquote key={`q${blocks.length}`} className="pquote">{inline(items.join('\n'), onCite, `q${i}`)}</blockquote>);
      i = j - 1;
      continue;
    }

    para.push(line);
  }
  flushPara();

  return <div className="prose" style={style}>{blocks}</div>;
}
