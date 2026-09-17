// 按原件类型把文件显示出来：Word 还原版式，PDF 交给浏览器自带的查看器，
// 其余（老 Office、图纸等）给一个下载按钮——看不了就说看不了，不装样子。
//
// 类型按文件头判，不认扩展名：语料是人上传的，名字不可靠。
import { useEffect, useRef, useState } from 'react';
import { download, fetchBlob, ApiError } from '../lib/api';
import { ErrorBox, Spinner } from './Common';

/** 把 docx 渲染进指定容器。新的一版画好再整体替换，切换时不白一下。 */
export async function renderDocxInto(blob: Blob, el: HTMLElement) {
  const { renderAsync } = await import('docx-preview');
  const stage = document.createElement('div');
  await renderAsync(blob, stage, undefined, {
    className: 'docx', inWrapper: true, breakPages: true,
    renderHeaders: true, renderFooters: true,
  });
  el.replaceChildren(...Array.from(stage.childNodes));
}

async function sniff(blob: Blob): Promise<'pdf' | 'zip' | 'other'> {
  const head = new Uint8Array(await blob.slice(0, 4).arrayBuffer());
  const s = String.fromCharCode(...head);
  if (s.startsWith('%PDF')) return 'pdf';
  if (head[0] === 0x50 && head[1] === 0x4b) return 'zip';   // docx/xlsx/pptx 都是 zip
  return 'other';
}

export function FileView({ docId, fileName }: { docId: string; fileName: string }) {
  const box = useRef<HTMLDivElement>(null);
  const [state, setState] = useState<'loading' | 'docx' | 'pdf' | 'other' | 'fail'>('loading');
  const [pdfUrl, setPdfUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    let url: string | null = null;
    setState('loading');
    setError(null);
    void (async () => {
      try {
        const blob = await fetchBlob(`/api/files/${docId}`);
        if (!alive) return;
        const kind = await sniff(blob);
        if (!alive) return;
        if (kind === 'pdf') {
          url = URL.createObjectURL(blob);
          setPdfUrl(url);
          setState('pdf');
          return;
        }
        if (kind === 'zip') {
          // zip 里可能是 docx，也可能是表格或演示稿——渲染不出来就退回下载
          try {
            if (box.current) await renderDocxInto(blob, box.current);
            if (alive) setState('docx');
            return;
          } catch { if (alive) setState('other'); return; }
        }
        setState('other');
      } catch (e) {
        if (!alive) return;
        setError(e instanceof ApiError ? e.message : '原件打不开');
        setState('fail');
      }
    })();
    return () => { alive = false; if (url) URL.revokeObjectURL(url); };
  }, [docId]);

  return (
    <div className="fv">
      {state === 'loading' && <div style={{ padding: 14 }}><Spinner text="打开原件…" /></div>}
      {state === 'fail' && <div style={{ padding: 14 }}><ErrorBox message={error ?? '原件打不开'} /></div>}
      {state === 'pdf' && pdfUrl && <iframe className="fv-pdf" src={pdfUrl} title={fileName} />}
      {state === 'other' && (
        <div style={{ padding: 16, textAlign: 'center' }}>
          <div className="hint" style={{ marginBottom: 8 }}>这个格式没法在浏览器里还原版式。</div>
          <button className="pbtn" onClick={() => void download(`/api/files/${docId}`, fileName)}>
            下载原件 ↓
          </button>
        </div>
      )}
      <div ref={box} className="fv-doc" style={{ display: state === 'docx' ? 'block' : 'none' }} />
    </div>
  );
}
