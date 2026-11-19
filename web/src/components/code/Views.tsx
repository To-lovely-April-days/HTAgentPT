// 代码块的几种「直接显示」：Mermaid 图、SVG、ECharts 图、分子结构。都能看源码、下载图片。
import { useEffect, useId, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { CodeBlock } from './CodeBlock';
import { downloadText, serializeSvg, svgToPng } from './text';

/** 外框：标题、看源码 / 看图切换、下载。 */
export function ViewFrame({ label, code, lang, children, actions, error }: {
  label: string; code: string; lang: string; children: ReactNode; actions?: ReactNode; error?: string | null;
}) {
  const [src, setSrc] = useState(false);
  return (
    <div className="rv">
      <div className="cb-hd">
        <span className="cb-lang">{label}</span>
        {error && <span className="rv-err">{error}</span>}
        <span className="cb-sp" />
        {!src && actions}
        <button type="button" className={'cb-btn' + (src ? ' on' : '')} aria-pressed={src} onClick={() => setSrc(!src)}>{src ? '看图' : '源码'}</button>
      </div>
      {src || error ? <CodeBlock code={code} lang={lang} bare /> : <div className="rv-body">{children}</div>}
    </div>
  );
}

function ImgActions({ svg, name }: { svg: () => string | null; name: string }) {
  return (
    <>
      <button type="button" className="cb-btn" onClick={() => { const s = svg(); if (s) downloadText(`${name}.svg`, s, 'image/svg+xml'); }}>SVG</button>
      <button type="button" className="cb-btn" onClick={() => { const s = svg(); if (s) void svgToPng(s, `${name}.png`); }}>PNG</button>
    </>
  );
}

let mermaidReady: Promise<typeof import('mermaid').default> | null = null;
function loadMermaid() {
  mermaidReady ??= import('mermaid').then(({ default: m }) => {
    // strict：图里的文字当文字，不执行脚本、不开链接
    m.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'neutral', fontFamily: 'inherit' });
    return m;
  });
  return mermaidReady;
}

export function MermaidView({ code }: { code: string }) {
  const id = 'mmd' + useId().replace(/[^a-zA-Z0-9]/g, '');
  const box = useRef<HTMLDivElement>(null);
  const [error, setError] = useState<string | null>(null);
  const [svg, setSvg] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    void loadMermaid().then(async (m) => {
      try {
        const { svg: out } = await m.render(id, code);
        if (!alive) return;
        setSvg(out); setError(null);
      } catch (e) {
        if (alive) setError('图的写法有误，下面是源码' + (e instanceof Error ? `（${e.message.split('\n')[0].slice(0, 60)}）` : ''));
        document.getElementById('d' + id)?.remove();
      }
    });
    return () => { alive = false; };
  }, [code, id]);
  return (
    <ViewFrame label="图" code={code} lang="mermaid" error={error}
      actions={<ImgActions name="流程图" svg={() => { const el = box.current?.querySelector('svg'); return el ? serializeSvg(el) : null; }} />}>
      {/* strict 模式的输出已由 mermaid 自带的 DOMPurify 清洗过 */}
      {svg ? <div ref={box} className="rv-mermaid" dangerouslySetInnerHTML={{ __html: svg }} /> : <div className="hint" style={{ padding: 12 }}>正在画图…</div>}
    </ViewFrame>
  );
}

/** SVG 用 img 显示：图片里的脚本、事件一律不会执行。 */
export function SvgView({ code }: { code: string }) {
  const src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(code);
  return (
    <ViewFrame label="SVG 图" code={code} lang="xml" actions={<ImgActions name="图" svg={() => code} />}>
      <img className="rv-svg" src={src} alt="SVG 图" />
    </ViewFrame>
  );
}

/** ECharts：配置是纯 JSON（不收函数），画成能交互的图。 */
export function EChartView({ code }: { code: string }) {
  const box = useRef<HTMLDivElement>(null);
  const chart = useRef<{ getDataURL: (o: object) => string; dispose: () => void; resize: () => void } | null>(null);
  const option = useMemo(() => { try { return JSON.parse(code) as object; } catch { return null; } }, [code]);
  const [bad, setBad] = useState<string | null>(null);
  const error = option ? (bad === code ? '图的配置有误，下面是源码' : null) : '配置不是合法的 JSON，下面是源码';
  useEffect(() => {
    if (!option) return;
    let alive = true;
    let ro: ResizeObserver | null = null;
    void import('echarts').then((echarts) => {
      if (!alive || !box.current) return;
      const c = echarts.init(box.current, undefined, { renderer: 'canvas' });
      try { c.setOption({ animation: false, ...option }); } catch { setBad(code); }
      chart.current = c;
      ro = new ResizeObserver(() => c.resize());
      ro.observe(box.current);
    });
    return () => { alive = false; ro?.disconnect(); chart.current?.dispose(); chart.current = null; };
  }, [code, option]);
  const png = () => {
    const url = chart.current?.getDataURL({ type: 'png', pixelRatio: 2, backgroundColor: '#fff' });
    if (!url) return;
    const a = document.createElement('a'); a.href = url; a.download = '图表.png'; a.click();
  };
  return (
    <ViewFrame label="图表" code={code} lang="json" error={error} actions={<button type="button" className="cb-btn" onClick={png}>PNG</button>}>
      <div ref={box} className="rv-echart" />
    </ViewFrame>
  );
}

/** SMILES → 结构式（smiles-drawer 画 SVG）。 */
export function MoleculeSvg({ smiles, width = 320, height = 220, onError }: { smiles: string; width?: number; height?: number; onError?: (m: string) => void }) {
  const ref = useRef<SVGSVGElement>(null);
  useEffect(() => {
    let alive = true;
    void import('smiles-drawer').then(({ default: SD }) => {
      if (!alive || !ref.current) return;
      ref.current.replaceChildren();
      const drawer = new SD.SvgDrawer({ width, height, bondThickness: 1.1, fontSizeLarge: 11, fontSizeSmall: 8, padding: 16 });
      SD.parse(smiles, (tree) => {
        try { drawer.draw(tree, ref.current, 'light'); } catch { onError?.('结构画不出来'); }
      }, () => onError?.('SMILES 写法有误'));
    });
    return () => { alive = false; };
  }, [smiles, width, height, onError]);
  return <svg ref={ref} className="mol-svg" width={width} height={height} role="img" aria-label={`分子结构 ${smiles}`} />;
}

export function SmilesView({ code }: { code: string }) {
  const smiles = code.trim().split(/\s+/)[0] ?? '';
  const [error, setError] = useState<string | null>(null);
  const wrap = useRef<HTMLDivElement>(null);
  return (
    <ViewFrame label="分子结构" code={code} lang="text" error={error}
      actions={<ImgActions name="分子结构" svg={() => { const el = wrap.current?.querySelector('svg'); return el ? serializeSvg(el) : null; }} />}>
      <div ref={wrap} className="rv-mol">
        <MoleculeSvg smiles={smiles} onError={setError} />
        <code className="pcode">{smiles}</code>
      </div>
    </ViewFrame>
  );
}
