// 语料对照预览（E1 增强，RAGFlow 式）：左侧渲染原件，右侧解析分块列表，点分块 → 左侧定位。
// 左侧按原件类型分三档：PDF 走 pdfjs 逐页渲染（懒渲染省内存，bbox 叠亮框）；
// Word 走 docx-preview 在浏览器里还原版面（表格、图片、样式都在，不出网、不需要转换服务），
// 点分块时按分块开头的文字在渲染结果里找到位置滚过去；其余格式（Excel/演示稿等）退化为文本版。
// bbox 坐标口径：解析引擎给的是 PDF 点单位、左上角原点——与 pdfjs scale=1 视口同基，
// 叠框只需乘显示缩放比。
import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import * as pdfjs from 'pdfjs-dist';
import type { PDFDocumentProxy } from 'pdfjs-dist';
import { get, fetchBlob, download, ApiError } from '../../lib/api';
import { AuthImage, ErrorBox, Spinner } from '../../components/Common';

pdfjs.GlobalWorkerOptions.workerSrc = new URL('pdfjs-dist/build/pdf.worker.min.mjs', import.meta.url).toString();

interface ChunkRow {
  id: number; seq: number; sectionPath: string | null; pageNo: number | null;
  bbox: string | null; text: string; embeddingModel: string | null; isActive: boolean;
}
interface ImgRow { id: number; caption: string | null; pageNo: number | null; bbox: string | null; seq: number; }
interface Target { page: number; bbox: number[] | null; ts: number; }

const PAGE_WIDTH = 700; // 左栏页面显示宽度（px）

function parseBbox(s: string | null): number[] | null {
  if (!s) return null;
  const parts = s.split(',').map((x) => Number(x));
  return parts.length === 4 && parts.every((n) => Number.isFinite(n)) ? parts : null;
}

export default function DocPreviewPage() {
  const { docId = '' } = useParams();
  const state = (useLocation().state ?? null) as { title?: string; fileName?: string } | null;
  const chunks = useQuery({ queryKey: ['doc-chunks', docId], queryFn: () => get<ChunkRow[]>(`/api/documents/${docId}/chunks`) });
  const images = useQuery({ queryKey: ['doc-images', docId], queryFn: () => get<ImgRow[]>(`/api/files/${docId}/images`) });

  const [pdf, setPdf] = useState<PDFDocumentProxy | null>(null);
  // loading → pdf（逐页渲染）/ docx（还原版面）/ text（文本版兜底）
  const [mode, setMode] = useState<'loading' | 'pdf' | 'docx' | 'text'>('loading');
  const [forceText, setForceText] = useState(false);   // 用户手动切到文本版
  const docxRef = useRef<HTMLDivElement | null>(null);
  const docxBlob = useRef<Blob | null>(null);
  const [target, setTarget] = useState<Target | null>(null);
  const [activeChunk, setActiveChunk] = useState<number | null>(null);
  const pageRefs = useRef<Record<number, HTMLDivElement | null>>({});
  // 文本版（Office 等不能逐页渲染的原件）里各分块的位置，点右侧卡片时滚过去
  const chunkRefs = useRef<Record<number, HTMLDivElement | null>>({});

  useEffect(() => {
    let alive = true;
    let blobUrl: string | null = null;
    let task: ReturnType<typeof pdfjs.getDocument> | null = null;
    void (async () => {
      try {
        const blob = await fetchBlob(`/api/files/${docId}`);
        if (!alive) return;
        docxBlob.current = blob;
        // 看文件头定类型，不依赖文件名：%PDF / PK（OOXML 压缩包）
        const head = new Uint8Array(await blob.slice(0, 4).arrayBuffer());
        const isPdf = head[0] === 0x25 && head[1] === 0x50 && head[2] === 0x44 && head[3] === 0x46;
        if (isPdf) {
          blobUrl = URL.createObjectURL(blob);
          task = pdfjs.getDocument({ url: blobUrl });
          const doc = await task.promise;
          if (!alive) return;
          setPdf(doc);
          setMode('pdf');
          return;
        }
        const isZip = head[0] === 0x50 && head[1] === 0x4b;
        if (isZip) { setMode('docx'); return; }   // 真正的渲染在下一个 effect（要等容器挂上）
        setMode('text');
      } catch {
        if (alive) setMode('text');               // 取不到或不认得：文本版兜底，不报错打断
      }
    })();
    return () => {
      alive = false;
      if (task) void task.destroy();
      if (blobUrl) URL.revokeObjectURL(blobUrl);
    };
  }, [docId]);

  // Word 渲染：容器挂上之后才能画。不是 Word 的 OOXML（Excel/演示稿）会抛错 → 退文本版
  useEffect(() => {
    if (mode !== 'docx' || forceText || !docxRef.current || !docxBlob.current) return;
    let alive = true;
    const container = docxRef.current;
    const blob = docxBlob.current;
    container.innerHTML = '';
    // 渲染器按需加载：只有真的打开 Word 原件才下载这段代码，不压在主包里
    void import('docx-preview')
      .then(({ renderAsync }) => renderAsync(blob, container, undefined, {
        className: 'docx', inWrapper: true, breakPages: true,
        ignoreHeight: false, ignoreWidth: false, renderHeaders: true, renderFooters: true,
      }))
      .catch(() => { if (alive) setMode('text'); });
    return () => { alive = false; };
  }, [mode, forceText, docId]);

  const view = forceText ? 'text' : mode;

  /// Word 渲染结果里按文字定位：分块开头的一段字找到对应节点，滚过去并闪一下。
  /// docx 没有坐标可用（版面是浏览器排的），按文字找是唯一可靠的对法。
  const locateInDocx = (text: string) => {
    const container = docxRef.current;
    if (!container) return;
    const probe = text.replace(/[|\s]+/g, ' ').trim().slice(0, 14).trim();
    if (probe.length < 3) return;
    const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      if (!node.textContent?.includes(probe.slice(0, Math.min(8, probe.length)))) continue;
      const el = node.parentElement;
      if (!el) continue;
      el.scrollIntoView({ behavior: 'smooth', block: 'center' });
      const prev = el.style.backgroundColor;
      el.style.backgroundColor = 'rgba(217,119,6,.22)';
      window.setTimeout(() => { el.style.backgroundColor = prev; }, 2200);
      return;
    }
  };

  const jumpTo = (page: number | null, bbox: string | null, chunkId: number | null) => {
    setActiveChunk(chunkId);
    if (view === 'pdf') {
      if (page == null) return;
      setTarget({ page, bbox: parseBbox(bbox), ts: Date.now() });
      pageRefs.current[page]?.scrollIntoView({ behavior: 'smooth', block: 'start' });
      return;
    }
    if (view === 'docx') {
      const hit = (chunks.data ?? []).find((c) => c.id === chunkId);
      if (hit) locateInDocx(hit.text);
      return;
    }
    if (chunkId != null) chunkRefs.current[chunkId]?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  };

  const title = state?.title ?? '文档对照预览';
  const pageImages = useMemo(() => {
    const m = new Map<number, ImgRow[]>();
    for (const im of images.data ?? [])
      if (im.pageNo != null) m.set(im.pageNo, [...(m.get(im.pageNo) ?? []), im]);
    return m;
  }, [images.data]);

  return (
    <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
      <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 12, padding: '10px 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <Link to="/admin/corpus" style={{ fontSize: 12.5, color: 'var(--accent)', textDecoration: 'none' }}>← 语料管理</Link>
        <span style={{ fontSize: 13.5, fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{title}</span>
        <span className="hint" style={{ marginLeft: 'auto', flexShrink: 0 }}>
          分块 {chunks.data?.length ?? '…'} · 图片 {images.data?.length ?? '…'}
        </span>
        {mode === 'docx' && (
          <button className="gbtn" style={{ height: 26, flexShrink: 0 }} onClick={() => setForceText((v) => !v)}>
            {forceText ? '看原件版面' : '看文本版'}
          </button>
        )}
        <button className="gbtn" style={{ height: 26, flexShrink: 0 }}
          onClick={() => void download(`/api/files/${docId}`, title).catch((e) => alert(e instanceof ApiError ? e.message : '下载失败'))}>
          下载原件
        </button>
      </div>

      <div style={{ flexGrow: 1, display: 'flex', minHeight: 0 }}>
        {/* 左：原件 */}
        <div className="sc" style={{ flexGrow: 1, minWidth: 0, background: '#565c66', padding: '16px 0 24px' }}>
          {view === 'loading' && <div style={{ padding: 30 }}><Spinner text="载入原件…" /></div>}

          {/* Word 原件：在浏览器里还原版面（表格、图片、样式都在），点右侧分块按文字定位 */}
          <div ref={docxRef} className="docx-host"
            style={{ display: view === 'docx' ? 'block' : 'none', maxWidth: PAGE_WIDTH + 120, margin: '0 auto' }} />
          {/* Office 等不能逐页渲染的原件：左侧给解析出的文本版，仍然能与右侧分块一一对照。
              留一片空白比没有更糟——版面还原不了，内容是可以照着看的 */}
          {view === 'text' && (
            <div style={{ maxWidth: PAGE_WIDTH, margin: '0 auto 24px' }}>
              <div className="hint" style={{ color: '#d8dde4', padding: '0 4px 10px', lineHeight: 1.7 }}>
                原件版面不能在页面里还原（Word / Excel 这类文件的分页由打开时决定）。下面是解析出的文本版，
                与右侧分块一一对应；要看原样式点右上角「下载原件」。
              </div>
              <div style={{ background: 'var(--panel)', borderRadius: 6, padding: '26px 30px' }}>
                {(chunks.data ?? []).map((c) => (
                  <div key={c.id} ref={(el) => { chunkRefs.current[c.id] = el; }}
                    style={{
                      padding: '8px 10px', margin: '0 -10px 4px', borderRadius: 4,
                      background: activeChunk === c.id ? 'rgba(217,119,6,.12)' : 'transparent',
                      boxShadow: activeChunk === c.id ? 'inset 0 0 0 1.5px rgba(217,119,6,.5)' : 'none',
                      transition: 'background .15s',
                    }}>
                    {c.sectionPath && (
                      <div className="hint" style={{ marginBottom: 4 }}>{c.sectionPath}</div>
                    )}
                    <div style={{ fontSize: 13, lineHeight: 1.85, whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
                      {c.text}
                    </div>
                  </div>
                ))}
                {(images.data ?? []).length > 0 && (
                  <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', marginTop: 18, paddingTop: 14, borderTop: '1px solid var(--line-soft)' }}>
                    {(images.data ?? []).map((im) => (
                      <figure key={im.id} style={{ margin: 0 }}>
                        <AuthImage src={`/api/files/${docId}/images/${im.id}`} alt={im.caption ?? ''}
                          style={{ maxWidth: 240, maxHeight: 180, borderRadius: 4, border: '1px solid var(--line)' }} />
                        <figcaption className="hint" style={{ marginTop: 3, maxWidth: 240 }}>{im.caption ?? '图片'}</figcaption>
                      </figure>
                    ))}
                  </div>
                )}
              </div>
            </div>
          )}
          {view === 'pdf' && pdf && Array.from({ length: pdf.numPages }, (_, i) => (
            <PdfPage key={i + 1} pdf={pdf} pageNo={i + 1} width={PAGE_WIDTH}
              refCb={(el) => { pageRefs.current[i + 1] = el; }}
              highlight={target?.page === i + 1 ? target : null} />
          ))}
        </div>

        {/* 右：解析分块（点击回跳原文位置） */}
        <aside className="sc" style={{ width: 430, flexShrink: 0, background: 'var(--panel)', borderLeft: '1px solid var(--line)', padding: '12px 12px 20px' }}>
          <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 4 }}>解析分块</div>
          <div className="hint" style={{ marginBottom: 10, lineHeight: 1.6 }}>
            检索命中的就是这些块。点一块，左侧跳到它在原文里的位置{view === 'pdf' ? '并标出色框' : view === 'docx' ? '并高亮' : ''}。
          </div>
          {chunks.isLoading && <Spinner text="载入分块…" />}
          {chunks.isError && <ErrorBox message="分块载入失败" />}
          {(chunks.data ?? []).map((c) => (
            <div key={c.id} onClick={() => jumpTo(c.pageNo, c.bbox, c.id)}
              className="card"
              style={{
                padding: '9px 11px', marginBottom: 8, cursor: c.pageNo != null ? 'pointer' : 'default',
                border: `1px solid ${activeChunk === c.id ? 'var(--accent)' : 'var(--line)'}`,
                background: activeChunk === c.id ? 'var(--accent-bg)' : 'var(--panel)',
              }}>
              <div style={{ display: 'flex', gap: 7, alignItems: 'center', marginBottom: 4 }}>
                <span className="pill pill-neutral m">#{c.seq}</span>
                <span className="hint" style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {c.sectionPath ?? '—'}
                </span>
                {c.pageNo != null && <span className="m hint" style={{ marginLeft: 'auto', flexShrink: 0 }}>第 {c.pageNo} 页</span>}
              </div>
              <div style={{ fontSize: 11.5, lineHeight: 1.7, color: 'var(--ink-2)', whiteSpace: 'pre-wrap' }}>
                {c.text.length > 320 ? c.text.slice(0, 320) + '…' : c.text}
              </div>
              {c.pageNo != null && (pageImages.get(c.pageNo) ?? []).length > 0 && (
                <div style={{ display: 'flex', gap: 5, flexWrap: 'wrap', marginTop: 6 }}>
                  {(pageImages.get(c.pageNo) ?? []).map((im) => (
                    <AuthImage key={im.id} src={`/api/files/${docId}/images/${im.id}`} alt={im.caption ?? ''} title={im.caption ?? undefined}
                      style={{ maxHeight: 56, maxWidth: 100, borderRadius: 3, border: '1px solid var(--line)', background: '#fff' }} />
                  ))}
                </div>
              )}
            </div>
          ))}
          {chunks.data && chunks.data.length === 0 && <div className="hint">该文档还没有解析分块——先在语料管理提交解析。</div>}
        </aside>
      </div>
    </div>
  );
}

/** 单页：进入视口才真正渲染（200 页说明书全量画 canvas 会吃满内存）。 */
function PdfPage({ pdf, pageNo, width, refCb, highlight }: {
  pdf: PDFDocumentProxy; pageNo: number; width: number;
  refCb: (el: HTMLDivElement | null) => void;
  highlight: Target | null;
}) {
  const holder = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [visible, setVisible] = useState(false);
  const [size, setSize] = useState<{ w: number; h: number; scale: number } | null>(null);

  useEffect(() => {
    const el = holder.current;
    if (!el) return;
    const ob = new IntersectionObserver((es) => {
      if (es.some((e) => e.isIntersecting)) { setVisible(true); ob.disconnect(); }
    }, { rootMargin: '600px' });
    ob.observe(el);
    return () => ob.disconnect();
  }, []);

  useEffect(() => {
    if (!visible) return;
    let alive = true;
    void (async () => {
      const page = await pdf.getPage(pageNo);
      if (!alive) return;
      const base = page.getViewport({ scale: 1 });
      const scale = width / base.width;
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      const viewport = page.getViewport({ scale: scale * dpr });
      const canvas = canvasRef.current;
      if (!canvas) return;
      canvas.width = viewport.width;
      canvas.height = viewport.height;
      canvas.style.width = `${width}px`;
      canvas.style.height = `${base.height * scale}px`;
      setSize({ w: width, h: base.height * scale, scale });
      await page.render({ canvasContext: canvas.getContext('2d')!, viewport }).promise;
    })();
    return () => { alive = false; };
  }, [visible, pdf, pageNo, width]);

  const hl = highlight?.bbox && size
    ? {
        left: highlight.bbox[0] * size.scale,
        top: highlight.bbox[1] * size.scale,
        width: Math.max(6, (highlight.bbox[2] - highlight.bbox[0]) * size.scale),
        height: Math.max(6, (highlight.bbox[3] - highlight.bbox[1]) * size.scale),
      }
    : null;

  return (
    <div ref={(el) => { holder.current = el; refCb(el); }}
      style={{ position: 'relative', width, margin: '0 auto 14px', background: '#fff', boxShadow: '0 2px 10px rgba(0,0,0,.28)', minHeight: size?.h ?? width * 1.35, scrollMarginTop: 12 }}>
      <canvas ref={canvasRef} style={{ display: 'block' }} />
      {highlight && hl && (
        <div key={highlight.ts} style={{
          position: 'absolute', ...hl, borderRadius: 3,
          border: '2px solid #d97706', background: 'rgba(217,119,6,.16)', pointerEvents: 'none',
        }} />
      )}
      {highlight && !hl && (
        <div key={highlight.ts} style={{ position: 'absolute', inset: 0, border: '3px solid #d97706', pointerEvents: 'none' }} />
      )}
      <div className="m" style={{ position: 'absolute', right: 6, bottom: 4, fontSize: 10.5, color: '#9aa3ae' }}>{pageNo}</div>
    </div>
  );
}
