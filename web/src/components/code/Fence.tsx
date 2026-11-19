// 回答里一个代码块按语言变成能看、能用的样子。模型照常写 ```xxx，不加新规定。
// 正在输出时（live）先按普通代码显示，写完再画图——半截的图只会报错闪烁。
import { CodeBlock } from './CodeBlock';
import { CsvView, JsonView } from './DataViews';
import { EChartView, MermaidView, SmilesView, SvgView } from './Views';
import { htmlTitle, usePreview } from './preview';

function HtmlCard({ code, live }: { code: string; live?: boolean }) {
  const open = usePreview();
  const lines = code.split('\n').length;
  if (!open) return <CodeBlock code={code} lang="html" />;
  return (
    <div className="html-card">
      <span className="html-card-ic" aria-hidden="true">网页</span>
      <span className="html-card-t">
        <b>{htmlTitle(code)}</b>
        <span>{live ? `正在写… ${lines} 行` : `HTML · ${lines} 行 · 在隔离的沙箱里运行`}</span>
      </span>
      <button type="button" className="pbtn" disabled={live} onClick={() => open({ kind: 'html', code })}>打开预览</button>
    </div>
  );
}

export function Fence({ lang, code, live }: { lang: string; code: string; live?: boolean }) {
  const l = lang.toLowerCase();
  if (l === 'html' || l === 'htm') return <HtmlCard code={code} live={live} />;
  if (live) return <CodeBlock code={code} lang={l} />;
  switch (l) {
    case 'mermaid': return <MermaidView code={code} />;
    case 'svg': return <SvgView code={code} />;
    case 'xml': return code.trimStart().startsWith('<svg') ? <SvgView code={code} /> : <CodeBlock code={code} lang="xml" />;
    case 'csv': case 'tsv': return <CsvView code={code} lang={l} />;
    case 'json': return code.length < 300_000 ? <JsonView code={code} /> : <CodeBlock code={code} lang="json" />;
    case 'echarts': return <EChartView code={code} />;
    case 'smiles': return <SmilesView code={code} />;
    default: return <CodeBlock code={code} lang={l} />;
  }
}
