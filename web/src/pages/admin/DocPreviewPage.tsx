// 语料对照预览（E1 增强，RAGFlow 式）：左侧原件逐页渲染（pdfjs，懒渲染省内存），
// 右侧解析分块列表；点分块 → 左侧滚到对应页并按 bbox 叠亮框。
// bbox 坐标口径：解析引擎给的是 PDF 点单位、左上角原点——与 pdfjs scale=1 视口同基，
// 叠框只需乘显示缩放比。office 原件（解析走转换后版面）与无 bbox 的文档退化为列表对照。
import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import * as pdfjs from 'pdfjs-dist';
import type { PDFDocumentProxy } from 'pdfjs-dist';
import { get, fetchBlobUrl, download, ApiError } from '../../lib/api';
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
  const [pdfState, setPdfState] = useState<'loading' | 'ready' | 'unavailable'>('loading');
  const [target, setTarget] = useState<Target | null>(null);
  const [activeChunk, setActiveChunk] = useState<number | null>(null);
  const pageRefs = useRef<Record<number, HTMLDivElement | null>>({});

  useEffect(() => {
    let alive = true;
    let blobUrl: string | null = null;
    let task: ReturnType<typeof pdfjs.getDocument> | null = null;
    void (async () => {
      try {
        blobUrl = await fetchBlobUrl(`/api/files/${docId}`);
        task = pdfjs.getDocument({ url: blobUrl });
        const doc = await task.promise;
        if (!alive) return;
        setPdf(doc);
        setPdfState('ready');
      } catch {
        // 非 PDF 原件或渲染失败：退化为纯列表对照，不报错打断
        if (alive) setPdfState('unavailable');
      }
    })();
    return () => {
      alive = false;
      if (task) void task.destroy();
      if (blobUrl) URL.revokeObjectURL(blobUrl);
    };
  }, [docId]);

  const jumpTo = (page: number | null, bbox: string | null, chunkId: number | null) => {
    setActiveChunk(chunkId);
    if (page == null || pdfState !== 'ready') return;
    setTarget({ page, bbox: parseBbox(bbox), ts: Date.now() });
    pageRefs.current[page]?.scrollIntoView({ behavior: 'smooth', block: 'start' });
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
        <button className="gbtn" style={{ height: 26, flexShrink: 0 }}
          onClick={() => void download(`/api/files/${docId}`, title).catch((e) => alert(e instanceof ApiError ? e.message : '下载失败'))}>
          下载原件
        </button>
      </div>

      <div style={{ flexGrow: 1, display: 'flex', minHeight: 0 }}>
        {/* 左：原件 */}
        <div className="sc" style={{ flexGrow: 1, minWidth: 0, background: '#565c66', padding: '16px 0 24px' }}>
          {pdfState === 'loading' && <div style={{ padding: 30 }}><Spinner text="载入原件…" /></div>}
          {pdfState === 'unavailable' && (
            <div style={{ maxWidth: 560, margin: '40px auto', padding: '16px 18px', background: 'var(--panel)', borderRadius: 6, fontSize: 12.5, lineHeight: 1.8 }}>
              该原件不能在页面里逐页渲染（office 文件的解析坐标对应转换后版面，或文件由业务模块自动维护）。
              右侧分块与下方图片仍可对照查看；需要原文时点右上角「下载原件」。
              <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', marginTop: 14 }}>
                {(images.data ?? []).map((im) => (
                  <figure key={im.id} style={{ margin: 0 }}>
                    <AuthImage src={`/api/files/${docId}/images/${im.id}`} alt={im.caption ?? ''}
                      style={{ maxWidth: 240, maxHeight: 180, borderRadius: 4, border: '1px solid var(--line)' }} />
                    <figcaption className="hint" style={{ marginTop: 3 }}>{im.caption ?? '图片'}{im.pageNo != null ? ` · 第 ${im.pageNo} 页` : ''}</figcaption>
                  </figure>
                ))}
              </div>
            </div>
          )}
          {pdfState === 'ready' && pdf && Array.from({ length: pdf.numPages }, (_, i) => (
            <PdfPage key={i + 1} pdf={pdf} pageNo={i + 1} width={PAGE_WIDTH}
              refCb={(el) => { pageRefs.current[i + 1] = el; }}
              highlight={target?.page === i + 1 ? target : null} />
          ))}
        </div>

        {/* 右：解析分块（点击回跳原文位置） */}
        <aside className="sc" style={{ width: 430, flexShrink: 0, background: 'var(--panel)', borderLeft: '1px solid var(--line)', padding: '12px 12px 20px' }}>
          <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 4 }}>解析分块</div>
          <div className="hint" style={{ marginBottom: 10, lineHeight: 1.6 }}>
            检索命中的就是这些块。点一块，左侧跳到它在原文里的位置{pdfState === 'ready' ? '并标出色框' : ''}。
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
