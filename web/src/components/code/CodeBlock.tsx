// 回答里的代码块：高亮、行号、复制、下载、换行开关，长代码先折叠；Python 能直接「运行」（接现有的隔离计算环境）。
// 高亮用 highlight.js（按需加载）；它输出的 HTML 已经把代码转义过，只多了着色用的 span，可以直接放进页面。
import { useEffect, useMemo, useState } from 'react';
import { ApiError, post } from '../../lib/api';
import { useCaps } from './caps';
import { downloadText, splitLines } from './text';
import { Icon } from '../Icons';

const EXT: Record<string, string> = {
  python: 'py', py: 'py', javascript: 'js', js: 'js', typescript: 'ts', ts: 'ts', tsx: 'tsx', jsx: 'jsx', html: 'html', css: 'css',
  json: 'json', sql: 'sql', bash: 'sh', sh: 'sh', shell: 'sh', powershell: 'ps1', csharp: 'cs', cs: 'cs', java: 'java', go: 'go',
  c: 'c', cpp: 'cpp', xml: 'xml', yaml: 'yml', yml: 'yml', markdown: 'md', md: 'md', csv: 'csv', vb: 'vb', matlab: 'm', r: 'r', ini: 'ini',
};
const LABEL: Record<string, string> = { py: 'Python', js: 'JavaScript', ts: 'TypeScript', cs: 'C#', sh: 'Shell', sql: 'SQL', html: 'HTML', css: 'CSS' };
const FOLD_AT = 24;
const FOLD_SHOW = 16;

const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function useHighlight(code: string, lang: string): string[] {
  const plain = useMemo(() => code.split('\n').map(esc), [code]);
  const [lines, setLines] = useState<{ key: string; lines: string[] } | null>(null);
  const key = lang + '\u0000' + code;
  useEffect(() => {
    let alive = true;
    if (code.length > 200_000) return;
    void import('highlight.js/lib/common').then(({ default: hljs }) => {
      if (!alive) return;
      const language = hljs.getLanguage(lang) ? lang : undefined;
      try {
        const html = language ? hljs.highlight(code, { language, ignoreIllegals: true }).value : hljs.highlightAuto(code).value;
        setLines({ key, lines: splitLines(html) });
      } catch { /* 高亮失败就显示纯文字 */ }
    });
    return () => { alive = false; };
  }, [code, lang, key]);
  return lines?.key === key ? lines.lines : plain;
}

interface RunResult { stdout: string; error: string | null; images: string[]; seconds: number }

export function CodeBlock({ code, lang, title, maxHeight, bare, unfold }: {
  code: string; lang: string; title?: string; maxHeight?: number; bare?: boolean;
  /** 一开始就全部展开（预览面板里看代码） */
  unfold?: boolean;
}) {
  const l = lang.toLowerCase();
  const lines = useHighlight(code, l);
  const [wrap, setWrap] = useState(false);
  const [open, setOpen] = useState(!!unfold);
  const [msg, setMsg] = useState<string | null>(null);
  const [run, setRun] = useState<RunResult | null>(null);
  const [running, setRunning] = useState(false);
  const caps = useCaps();
  const ext = EXT[l] ?? 'txt';
  const folded = !open && lines.length > FOLD_AT;
  const shown = folded ? lines.slice(0, FOLD_SHOW) : lines;
  const canRun = (l === 'python' || l === 'py') && caps.python;

  const copy = async () => {
    try { await navigator.clipboard.writeText(code); setMsg('已复制'); } catch { setMsg('复制失败'); }
    setTimeout(() => setMsg(null), 1500);
  };
  const exec = async () => {
    setRunning(true); setRun(null);
    try { setRun(await post<RunResult>('/api/web/python', { code })); }
    catch (e) { setRun({ stdout: '', error: e instanceof ApiError ? e.message : '运行失败', images: [], seconds: 0 }); }
    finally { setRunning(false); }
  };

  return (
    <div className={'cb' + (bare ? ' bare' : '')}>
      <div className="cb-hd">
        <span className="cb-lang">{title ?? LABEL[ext] ?? (l || '代码')}</span>
        <span className="cb-count">{lines.length} 行</span>
        <span className="cb-sp" />
        {msg && <span className="cb-msg">{msg}</span>}
        {canRun && <button type="button" className="cb-btn run" disabled={running} onClick={() => void exec()}>{running ? '运行中…' : <><Icon name="play" size={11} style={{ verticalAlign: '-1px', marginRight: 4 }} />运行</>}</button>}
        <button type="button" className={'cb-btn' + (wrap ? ' on' : '')} aria-pressed={wrap} onClick={() => setWrap(!wrap)}>换行</button>
        <button type="button" className="cb-btn" onClick={() => void copy()}>复制</button>
        <button type="button" className="cb-btn" onClick={() => downloadText(`代码.${ext}`, code)}>下载</button>
      </div>
      <div className={'cb-body sc' + (wrap ? ' wrap' : '')} style={maxHeight ? { maxHeight } : undefined}>
        {shown.map((h, i) => (
          <div key={i} className="cb-line">
            <span className="cb-ln" aria-hidden="true">{i + 1}</span>
            <span className="cb-code hljs" dangerouslySetInnerHTML={{ __html: h || ' ' }} />
          </div>
        ))}
      </div>
      {lines.length > FOLD_AT && (
        <button type="button" className="cb-fold" onClick={() => setOpen(!open)}>
          {open ? '收起 ▴' : `展开全部 ${lines.length} 行 ▾`}
        </button>
      )}
      {run && (
        <div className="cb-run">
          <div className="cb-run-hd">运行结果{run.seconds > 0 && <span> · {run.seconds.toFixed(1)} 秒</span>}</div>
          {run.stdout && <pre className="cb-out">{run.stdout}</pre>}
          {run.error && <pre className="cb-out err">{run.error}</pre>}
          {run.images.map((src, i) => <img key={i} className="tool-img" src={src} alt={`运行生成的图 ${i + 1}`} />)}
          {!run.stdout && !run.error && run.images.length === 0 && <div className="hint">运行完了，没有输出（要用 print 打印结果）。</div>}
        </div>
      )}
    </div>
  );
}
