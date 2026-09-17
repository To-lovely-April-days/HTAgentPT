// 模型输出的正文渲染。对话模型答题时天然会用标记语法写标题、要点、表格，
// 按纯文本显示就是满屏星号和横杠——交给 react-markdown（+ GFM 表格/删除线）排版，
// 样式仍走本项目的设计令牌，不引入外部主题。
//
// 安全：不开 rehype-raw，语料或模型输出里混进的 HTML 标签只会当文字显示。
// 来源角标 [1] 由一个小的 remark 插件转成 cite: 协议的链接节点，
// 再由下面的 a 组件渲染成可点的角标——不拼字符串 HTML。
import type { ComponentProps } from 'react';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { visit } from 'unist-util-visit';
import type { Root, Text, PhrasingContent } from 'mdast';

const CITE = /\[(\d{1,2})\]/g;

/** 正文里的 [1] [2] → cite:1 链接节点。放在 GFM 之后跑，表格单元格里的角标一样认。 */
function remarkCitation() {
  return (tree: Root) => {
    visit(tree, 'text', (node: Text, index, parent) => {
      if (!parent || index == null || parent.type === 'link') return;
      const value = node.value;
      CITE.lastIndex = 0;
      if (!CITE.test(value)) return;
      CITE.lastIndex = 0;
      const out: PhrasingContent[] = [];
      let last = 0;
      let m: RegExpExecArray | null;
      while ((m = CITE.exec(value)) !== null) {
        if (m.index > last) out.push({ type: 'text', value: value.slice(last, m.index) });
        out.push({
          type: 'link', url: `cite:${m[1]}`, title: null,
          children: [{ type: 'text', value: m[1] }],
        });
        last = m.index + m[0].length;
      }
      if (last < value.length) out.push({ type: 'text', value: value.slice(last) });
      parent.children.splice(index, 1, ...out);
      return index + out.length;
    });
  };
}

const PLUGINS = [remarkGfm, remarkCitation];

/** 模型输出的一段文字 → 排好版的块。onCite 给了，来源角标就是可点的。 */
export function Prose({ text, onCite, style }: {
  text: string; onCite?: (n: number) => void; style?: React.CSSProperties;
}) {
  return (
    <div className="prose" style={style}>
      <Markdown
        remarkPlugins={PLUGINS}
        components={{
          a({ href, children }: ComponentProps<'a'>) {
            const cite = href?.startsWith('cite:') ? Number(href.slice(5)) : null;
            if (cite == null || Number.isNaN(cite)) {
              // 正文里的普通链接：内网系统里不开新窗跳外站，只显示文字
              return <span className="plink">{children}</span>;
            }
            return onCite
              ? <button type="button" className="pcite" title={`看第 ${cite} 条来源`}
                  onClick={() => onCite(cite)}>{children}</button>
              : <span className="pcite">{children}</span>;
          },
          table: (p: ComponentProps<'table'>) => <table className="ptable" {...p} />,
          h1: (p: ComponentProps<'div'>) => <div className="ph ph1" {...p} />,
          h2: (p: ComponentProps<'div'>) => <div className="ph ph2" {...p} />,
          h3: (p: ComponentProps<'div'>) => <div className="ph ph3" {...p} />,
          h4: (p: ComponentProps<'div'>) => <div className="ph ph4" {...p} />,
          ul: (p: ComponentProps<'ul'>) => <ul className="plist" {...p} />,
          ol: (p: ComponentProps<'ol'>) => <ol className="plist" {...p} />,
          blockquote: (p: ComponentProps<'blockquote'>) => <blockquote className="pquote" {...p} />,
          code: (p: ComponentProps<'code'>) => <code className="pcode" {...p} />,
        }}
      >{text}</Markdown>
    </div>
  );
}
