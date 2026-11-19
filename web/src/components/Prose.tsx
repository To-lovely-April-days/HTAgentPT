// 模型输出的正文渲染。对话模型答题时天然会用标记语法写标题、要点、表格，
// 按纯文本显示就是满屏星号和横杠——交给 react-markdown（+ GFM 表格/删除线）排版，
// 样式仍走本项目的设计令牌，不引入外部主题。
//
// 安全：不开 rehype-raw，语料或模型输出里混进的 HTML 标签只会当文字显示。
// 来源角标 [1]、[1,2] 由一个小的 remark 插件转成 cite: 协议的链接节点，
// 再由下面的 a 组件渲染成可点的角标——不拼字符串 HTML。
// flags：没在原文里核实到的数字（如「18–26 万元」），同一个插件把它们包成 flag: 节点，渲染成黄底虚线。
//
// 代码块按语言直接显示成能看、能用的样子（见 code/Fence）；公式用 KaTeX 排版；
// 正文里的化学式、单位顺手排好：H2O → H₂O、m3 → m³。
import { useMemo } from 'react';
import type { ComponentProps, ReactNode } from 'react';
import { Children, isValidElement } from 'react';
import Markdown, { defaultUrlTransform } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';
import rehypeKatex from 'rehype-katex';
import 'katex/dist/katex.min.css';
import { visit } from 'unist-util-visit';
import type { Root, Text, PhrasingContent } from 'mdast';
import { Fence } from './code/Fence';
import { chemText, normalizeMath } from './proseText';

function remarkChem() {
  return (tree: Root) => {
    visit(tree, 'text', (node: Text) => { node.value = chemText(node.value); });
  };
}

const CITE = /\[(\d{1,3}(?:\s*[,，、]\s*\d{1,3})*)\]/g;
const escapeRe = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** 正文里的 [1] [2,3] → cite:n 链接节点；flags 里的数字 → flag: 节点。放在 GFM 之后跑，表格单元格里一样认。 */
function remarkMarks(flags: string[]) {
  const flagRe = flags.length > 0
    ? new RegExp(flags.map((f) => escapeRe(f)).sort((a, b) => b.length - a.length).join('|'), 'g')
    : null;
  return () => (tree: Root) => {
    visit(tree, 'text', (node: Text, index, parent) => {
      if (!parent || index == null || parent.type === 'link') return;
      const value = node.value;
      const marks: { start: number; end: number; nodes: PhrasingContent[] }[] = [];
      CITE.lastIndex = 0;
      let m: RegExpExecArray | null;
      while ((m = CITE.exec(value)) !== null) {
        const nums = m[1].split(/[,，、\s]+/).filter(Boolean);
        marks.push({
          start: m.index, end: m.index + m[0].length,
          nodes: nums.map((n) => ({ type: 'link', url: `cite:${n}`, title: null, children: [{ type: 'text', value: n }] })),
        });
      }
      if (flagRe) {
        flagRe.lastIndex = 0;
        while ((m = flagRe.exec(value)) !== null) {
          const start = m.index, end = m.index + m[0].length;
          if (marks.some((x) => start < x.end && end > x.start)) continue;
          marks.push({ start, end, nodes: [{ type: 'link', url: 'flag:', title: null, children: [{ type: 'text', value: m[0] }] }] });
        }
      }
      if (marks.length === 0) return;
      marks.sort((a, b) => a.start - b.start);
      const out: PhrasingContent[] = [];
      let last = 0;
      for (const x of marks) {
        if (x.start > last) out.push({ type: 'text', value: value.slice(last, x.start) });
        out.push(...x.nodes);
        last = x.end;
      }
      if (last < value.length) out.push({ type: 'text', value: value.slice(last) });
      parent.children.splice(index, 1, ...out);
      return index + out.length;
    });
  };
}

/** 单元格里的纯文字（含角标里的数字）。 */
function textOf(n: ReactNode): string {
  let s = '';
  Children.forEach(n, (c) => {
    if (typeof c === 'string' || typeof c === 'number') s += String(c);
    else if (isValidElement<{ children?: ReactNode; href?: string }>(c))
      s += c.props.href?.startsWith('cite:') ? `[${textOf(c.props.children)}]` : textOf(c.props.children);
  });
  return s;
}
/** 像数字的格子（金额、区间、百分比、带单位的数）右对齐。 */
const NUMERIC = /^[\s¥$€≈约~<>≤≥+−-]*[\d.,]+(\s*[-–~至]\s*[\d.,]+)?\s*(%|‰|万|亿|元|万元|kg|g|t|m|mm|cm|L|ml|MPa|kPa|Pa|bar|psi|℃|°C|K|kW|W|rpm|h|min|s|天|个|台|套|件)?(\s*\[[\d,，、\s]+\])*\s*$/i;

/** 模型输出的一段文字 → 排好版的块。onCite 给了，来源角标就是可点的；focus 是当前高亮的那个编号。 */
export function Prose({ text, onCite, style, flags, focus, live }: {
  text: string; onCite?: (n: number) => void; style?: React.CSSProperties; flags?: string[]; focus?: number | null;
  /** 正在输出：代码块先按普通代码显示，写完再画图 */
  live?: boolean;
}) {
  const key = (flags ?? []).join('\u0001');
  const plugins = useMemo(() => [remarkGfm, [remarkMath, { singleDollarTextMath: true }], remarkChem, remarkMarks(key ? key.split('\u0001') : [])], [key]);
  const md = useMemo(() => normalizeMath(text), [text]);
  return (
    <div className="prose" style={style}>
      <Markdown
        remarkPlugins={plugins as never}
        rehypePlugins={[[rehypeKatex, { throwOnError: false, strict: 'ignore', output: 'htmlAndMathml' }]] as never}
        // 默认的地址过滤会把 cite:/flag: 当成不安全协议清空——只放行这两个内部标记，其余照默认过滤
        urlTransform={(url) => (url.startsWith('cite:') || url === 'flag:' ? url : defaultUrlTransform(url))}
        components={{
          a({ href, children }: ComponentProps<'a'>) {
            if (href === 'flag:') {
              return <span className="pflag" title="这个数字没在引用的原文里找到，请核对">{children}</span>;
            }
            const cite = href?.startsWith('cite:') ? Number(href.slice(5)) : null;
            if (cite == null || Number.isNaN(cite)) {
              // 正文里的普通链接：内网系统里不开新窗跳外站，只显示文字
              return <span className="plink">{children}</span>;
            }
            return onCite
              ? <button type="button" className={'pcite' + (focus === cite ? ' on' : '')} title={`看第 ${cite} 条依据的原文`}
                  onClick={() => onCite(cite)}>{children}</button>
              : <span className="pcite">{children}</span>;
          },
          table: (p: ComponentProps<'table'>) => <div className="ptable-wrap"><table className="ptable" {...p} /></div>,
          td: ({ children, ...p }: ComponentProps<'td'>) => {
            const num = NUMERIC.test(textOf(children).trim());
            return <td {...p} className={num ? 'num' : undefined}>{children}</td>;
          },
          h1: (p: ComponentProps<'div'>) => <div className="ph ph1" {...p} />,
          h2: (p: ComponentProps<'div'>) => <div className="ph ph2" {...p} />,
          h3: (p: ComponentProps<'div'>) => <div className="ph ph3" {...p} />,
          h4: (p: ComponentProps<'div'>) => <div className="ph ph4" {...p} />,
          ul: (p: ComponentProps<'ul'>) => <ul className="plist" {...p} />,
          ol: (p: ComponentProps<'ol'>) => <ol className="plist" {...p} />,
          blockquote: (p: ComponentProps<'blockquote'>) => <blockquote className="pquote" {...p} />,
          pre: ({ children }: ComponentProps<'pre'>) => {
            const c = Children.toArray(children)[0];
            if (!isValidElement<{ className?: string; children?: ReactNode }>(c)) return <pre className="pcode-pre">{children}</pre>;
            const lang = /language-([\w+#.-]+)/.exec(c.props.className ?? '')?.[1] ?? '';
            const code = textOf(c.props.children).replace(/\n$/, '');
            return <Fence lang={lang} code={code} live={live} />;
          },
          code: ({ className, ...p }: ComponentProps<'code'>) => <code className={className ? className : 'pcode'} {...p} />,
        }}
      >{md}</Markdown>
    </div>
  );
}
