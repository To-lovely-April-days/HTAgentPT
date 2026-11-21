// 对话页右侧的预览面板：网页代码、网页浏览、长文档、文件，同一个位置轮流显示。
import { useMemo, useRef, useState } from 'react';
import { ApiError, postDownload } from '../../../lib/api';
import { Prose } from '../../../components/Prose';
import { FileView } from '../../../components/FileView';
import { HtmlPreview } from './HtmlPreview';
import { WebBrowser } from './WebBrowser';
import type { WebTab } from './WebBrowser';
import { Icon } from '../../../components/Icons';

export type Panel =
  | { kind: 'html'; code: string }
  | { kind: 'web' }
  | { kind: 'doc'; title: string; markdown: string }
  | { kind: 'file'; src: string; fileName: string };

/** 长文档：按文档排版看，左边目录，可导出 Word / PDF。 */
function DocPanel({ title, markdown, onClose }: { title: string; markdown: string; onClose: () => void }) {
  const body = useRef<HTMLDivElement>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const toc = useMemo(() => {
    const out: { level: number; text: string }[] = [];
    let fence = false;
    for (const l of markdown.split('\n')) {
      if (l.trimStart().startsWith('```')) fence = !fence;
      const m = !fence && /^(#{1,3})\s+(.+)$/.exec(l);
      if (m) out.push({ level: m[1].length, text: m[2].replace(/[*_`]/g, '').trim() });
    }
    return out;
  }, [markdown]);
  const jump = (i: number) => body.current?.querySelectorAll('.ph1, .ph2, .ph3')[i]?.scrollIntoView({ block: 'start', behavior: 'smooth' });
  const exp = async (kind: 'docx' | 'pdf') => {
    setMsg(null);
    try { await postDownload(`/api/export/${kind}`, { title, markdown, sources: [] }, `${title}.${kind}`); }
    catch (e) { setMsg(e instanceof ApiError ? e.message : '导出失败'); }
  };
  return (
    <div className="pv-inner">
      <div className="pv-hd">
        <span className="pv-ic doc" aria-hidden="true">文档</span>
        <b className="pv-t" title={title}>{title}</b>
        <span className="cb-sp" />
        {msg && <span className="cb-msg">{msg}</span>}
        <button type="button" className="cb-btn" onClick={() => void exp('docx')}>导出 Word</button>
        <button type="button" className="cb-btn" onClick={() => void exp('pdf')}>导出 PDF</button>
        <button type="button" className="refs-x" aria-label="关闭文档" onClick={onClose}><Icon name="x" size={14} style={{ display: 'block' }} /></button>
      </div>
      <div className="dp">
        {toc.length > 2 && (
          <nav className="dp-toc sc" aria-label="目录">
            {toc.map((t, i) => <button key={i} type="button" className={'l' + t.level} onClick={() => jump(i)}>{t.text}</button>)}
          </nav>
        )}
        <div className="dp-page sc" ref={body}><div className="dp-sheet"><Prose text={markdown} /></div></div>
      </div>
    </div>
  );
}

export function PreviewPanel({ panel, htmlVersions, webTabs, webActive, setPanel, setWebActive, closeWebTab, openWeb, onClose }: {
  panel: Panel; htmlVersions: string[]; webTabs: WebTab[]; webActive: number;
  setPanel: (p: Panel) => void; setWebActive: (i: number) => void; closeWebTab: (i: number) => void; openWeb: (url: string) => void; onClose: () => void;
}) {
  const [wide, setWide] = useState(false);
  return (
    <aside className={'pv' + (wide ? ' wide' : '')} aria-label="预览">
      <button type="button" className="pv-grip" title={wide ? '收窄' : '放宽'} aria-label={wide ? '收窄预览' : '放宽预览'} onClick={() => setWide(!wide)}>{wide ? '›' : '‹'}</button>
      {panel.kind === 'html' && <HtmlPreview code={panel.code} versions={htmlVersions} onPick={(code) => setPanel({ kind: 'html', code })} onClose={onClose} />}
      {panel.kind === 'web' && <WebBrowser tabs={webTabs} active={webActive} onActivate={setWebActive} onCloseTab={closeWebTab} onOpen={openWeb} onClose={onClose} />}
      {panel.kind === 'doc' && <DocPanel title={panel.title} markdown={panel.markdown} onClose={onClose} />}
      {panel.kind === 'file' && (
        <div className="pv-inner">
          <div className="pv-hd">
            <span className="pv-ic doc" aria-hidden="true">文件</span>
            <b className="pv-t" title={panel.fileName}>{panel.fileName}</b>
            <span className="cb-sp" />
            <button type="button" className="refs-x" aria-label="关闭文件预览" onClick={onClose}><Icon name="x" size={14} style={{ display: 'block' }} /></button>
          </div>
          <div className="pv-file sc"><FileView key={panel.src} src={panel.src} fileName={panel.fileName} /></div>
        </div>
      )}
    </aside>
  );
}
