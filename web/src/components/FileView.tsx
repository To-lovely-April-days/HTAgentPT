// 按原件类型把文件显示出来：Word 还原版式，PDF 交给浏览器自带的查看器，
// 表格出真表格（含多工作表），演示稿按页出文字大纲，其余给下载按钮——
// 看不了就说看不了，不装样子。
//
// 类型按文件头判，不认扩展名：语料是人上传的，名字不可靠。
// zip 家族（docx/xlsx/pptx）再按包内结构细分，都在浏览器里做，不出网也不加转换服务。
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

/** zip 包里放的是什么：按 Office 的固定目录判，比扩展名可靠。 */
async function officeKind(blob: Blob): Promise<'docx' | 'xlsx' | 'pptx' | 'other'> {
  const JSZip = (await import('jszip')).default;
  const zip = await JSZip.loadAsync(blob);
  if (zip.file('word/document.xml')) return 'docx';
  if (zip.file('xl/workbook.xml')) return 'xlsx';
  if (zip.folder('ppt/slides')?.file(/slide\d+\.xml$/).length) return 'pptx';
  return 'other';
}

/** 演示稿：浏览器里没有靠谱的版式还原，按页把文字理出来——
    能看清讲了什么，要看版面就下载。第一段当页标题。 */
async function pptOutline(blob: Blob): Promise<{ title: string; lines: string[] }[]> {
  const JSZip = (await import('jszip')).default;
  const zip = await JSZip.loadAsync(blob);
  const files = zip.folder('ppt/slides')?.file(/slide\d+\.xml$/) ?? [];
  const ordered = files.sort((a, b) =>
    (Number(a.name.match(/slide(\d+)/)?.[1]) || 0) - (Number(b.name.match(/slide(\d+)/)?.[1]) || 0));
  const out: { title: string; lines: string[] }[] = [];
  for (const f of ordered) {
    const xml = await f.async('string');
    const doc = new DOMParser().parseFromString(xml, 'application/xml');
    // 一个 <a:p> 是一段，段内可能被拆成多个 <a:t>，拼回去再算一行
    const lines = Array.from(doc.getElementsByTagName('a:p'))
      .map((para) => Array.from(para.getElementsByTagName('a:t')).map((t) => t.textContent ?? '').join('').trim())
      .filter((t) => t.length > 0);
    out.push({ title: lines[0] ?? '（无标题）', lines: lines.slice(1) });
  }
  return out;
}

export function FileView({ docId, fileName }: { docId: string; fileName: string }) {
  const box = useRef<HTMLDivElement>(null);
  const [state, setState] = useState<'loading' | 'docx' | 'pdf' | 'xlsx' | 'pptx' | 'other' | 'fail'>('loading');
  const [pdfUrl, setPdfUrl] = useState<string | null>(null);
  const [sheets, setSheets] = useState<{ name: string; html: string }[]>([]);
  const [sheetAt, setSheetAt] = useState(0);
  const [slides, setSlides] = useState<{ title: string; lines: string[] }[]>([]);
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
          const office = await officeKind(blob);
          if (!alive) return;
          if (office === 'docx') {
            try {
              if (box.current) await renderDocxInto(blob, box.current);
              if (alive) setState('docx');
            } catch { if (alive) setState('other'); }
            return;
          }
          if (office === 'xlsx') {
            const XLSX = await import('xlsx');
            const wb = XLSX.read(await blob.arrayBuffer(), { type: 'array' });
            if (!alive) return;
            setSheets(wb.SheetNames.map((name) => ({
              name, html: XLSX.utils.sheet_to_html(wb.Sheets[name], { id: `sh-${name}` }),
            })));
            setSheetAt(0);
            setState('xlsx');
            return;
          }
          if (office === 'pptx') {
            const outline = await pptOutline(blob);
            if (!alive) return;
            setSlides(outline);
            setState('pptx');
            return;
          }
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

      {state === 'xlsx' && sheets.length > 0 && (
        <div className="fv-xl">
          {sheets.length > 1 && (
            <div className="fv-tabs">
              {sheets.map((sh, i) => (
                <button key={sh.name} className={`fv-tab${i === sheetAt ? ' on' : ''}`}
                  onClick={() => setSheetAt(i)}>{sh.name}</button>
              ))}
            </div>
          )}
          {/* sheet_to_html 的产出是本地解析出来的表格标记，不含脚本 */}
          <div className="fv-sheet sc" dangerouslySetInnerHTML={{ __html: sheets[sheetAt].html }} />
        </div>
      )}

      {state === 'pptx' && (
        <div className="fv-ppt">
          <div className="hint" style={{ padding: '10px 14px 0' }}>
            演示稿在浏览器里只还原文字，版面与图请下载原件看。共 {slides.length} 页。
          </div>
          {slides.map((s2, i) => (
            <div key={i} className="fv-slide">
              <div className="fv-slide-n">第 {i + 1} 页</div>
              <div className="fv-slide-t">{s2.title}</div>
              {s2.lines.map((l, j) => <div key={j} className="fv-slide-l">{l}</div>)}
            </div>
          ))}
          <div style={{ padding: '4px 14px 16px' }}>
            <button className="gbtn" onClick={() => void download(`/api/files/${docId}`, fileName)}>下载原件 ↓</button>
          </div>
        </div>
      )}
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
