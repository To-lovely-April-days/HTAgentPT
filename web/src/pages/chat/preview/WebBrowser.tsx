// 网页浏览：回答里的网上来源、用户贴的网址都在这里打开，多个网页是多个标签页。
// 默认「阅读模式」：服务器去抓网页，只回正文、标题、表格、图片这些纯数据，这里按块排版——
// 网站的脚本不会在用户电脑上运行，用户电脑本身上不了外网也能看。回答引用的句子标黄。
// 「原网页」：网站允许被嵌入才显示原样，否则说明一下，给「新窗口打开」。
import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ApiError, fetchBlobUrl, get } from '../../../lib/api';
import { useAuth } from '../../../lib/auth';
import { Perm } from '../../../lib/types';
import { Spinner } from '../../../components/Common';
import { useCaps } from '../../../components/code/caps';
import UploadModal from '../../admin/UploadModal';
import { findMarks, quoteSentences } from './marks';
import { Icon } from '../../../components/Icons';

export interface WebTab { url: string; title?: string; label?: string; quotes?: string[] }

interface Block { type: string; text?: string | null; level?: number | null; rows?: string[][] | null; src?: string | null }
interface ReadResult {
  url: string; ok: boolean; note?: string | null; embeddable: boolean; title?: string; site?: string; published?: string | null;
  fetchedAt?: string; blocks?: Block[]; markdown?: string;
}

function Marked({ text, sentences, seen }: { text: string; sentences: string[]; seen: Set<number> }) {
  const marks = findMarks(text, sentences);
  if (marks.length === 0) return <>{text}</>;
  const out: ReactNode[] = [];
  let last = 0;
  marks.forEach((m, i) => {
    out.push(<Fragment key={`t${i}`}>{text.slice(last, m.start)}</Fragment>);
    const first = !seen.has(m.q);
    seen.add(m.q);
    out.push(<mark key={`m${i}`} className="wb-mark" data-q={first ? m.q : undefined}>{text.slice(m.start, m.end)}</mark>);
    last = m.end;
  });
  out.push(<Fragment key="end">{text.slice(last)}</Fragment>);
  return <>{out}</>;
}

function ReaderImage({ src, alt }: { src: string; alt: string }) {
  const [url, setUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let alive = true; let made: string | null = null;
    fetchBlobUrl(`/api/web/image?url=${encodeURIComponent(src)}`)
      .then((u) => { made = u; if (alive) setUrl(u); else URL.revokeObjectURL(u); })
      .catch(() => { if (alive) setFailed(true); });
    return () => { alive = false; if (made) URL.revokeObjectURL(made); };
  }, [src]);
  if (failed) return alt ? <div className="wb-img-miss">〔图：{alt}〕</div> : null;
  return (
    <figure className="wb-fig">
      {url ? <img src={url} alt={alt || '网页里的图片'} loading="lazy" /> : <div className="wb-img-wait" />}
      {alt && <figcaption>{alt}</figcaption>}
    </figure>
  );
}

function Reader({ data, sentences }: { data: ReadResult; sentences: string[] }) {
  const seen = new Set<number>();
  const blocks = data.blocks ?? [];
  const head = (
    <>
      <span className="wb-meta">{[data.site, data.published].filter(Boolean).join(' · ')}</span>
      <h2 className="wb-title">{data.title}</h2>
    </>
  );
  // 连续的列表项合成一个列表
  const groups: (Block | Block[])[] = [];
  for (const b of blocks) {
    const last = groups[groups.length - 1];
    if (b.type === 'li' && Array.isArray(last)) last.push(b);
    else groups.push(b.type === 'li' ? [b] : b);
  }
  return (
    <article className="wb-article">
      {head}
      {groups.map((g, i) => {
        if (Array.isArray(g)) return <ul key={i}>{g.map((b, j) => <li key={j}><Marked text={b.text ?? ''} sentences={sentences} seen={seen} /></li>)}</ul>;
        const t = g.text ?? '';
        switch (g.type) {
          case 'heading': {
            const H = (`h${Math.min(5, (g.level ?? 2) + 1)}`) as 'h3';
            return <H key={i}><Marked text={t} sentences={sentences} seen={seen} /></H>;
          }
          case 'quote': return <blockquote key={i}><Marked text={t} sentences={sentences} seen={seen} /></blockquote>;
          case 'code': return <pre key={i} className="wb-code">{t}</pre>;
          case 'image': return g.src ? <ReaderImage key={i} src={g.src} alt={t} /> : null;
          case 'table': {
            const rows = g.rows ?? [];
            const width = Math.max(...rows.map((r) => r.length));
            return (
              <div key={i} className="ptable-wrap"><table className="ptable">
                <thead><tr>{Array.from({ length: width }, (_, c) => <th key={c}>{rows[0]?.[c] ?? ''}</th>)}</tr></thead>
                <tbody>{rows.slice(1).map((r, ri) => (
                  <tr key={ri}>{Array.from({ length: width }, (_, c) => <td key={c}><Marked text={r[c] ?? ''} sentences={sentences} seen={seen} /></td>)}</tr>
                ))}</tbody>
              </table></div>
            );
          }
          default: return <p key={i}><Marked text={t} sentences={sentences} seen={seen} /></p>;
        }
      })}
    </article>
  );
}

export function WebBrowser({ tabs, active, onActivate, onCloseTab, onOpen, onClose }: {
  tabs: WebTab[]; active: number; onActivate: (i: number) => void; onCloseTab: (i: number) => void;
  onOpen: (url: string) => void; onClose: () => void;
}) {
  const caps = useCaps();
  const { profile } = useAuth();
  const canSave = profile?.permissions.includes(Perm.CorpusUpload) ?? false;
  const tab = tabs[Math.min(active, tabs.length - 1)];
  const [modes, setModes] = useState<Record<string, 'reader' | 'original'>>({});
  const mode = tab ? modes[tab.url] ?? 'reader' : 'reader';
  const [addr, setAddr] = useState<{ for: string; value: string } | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [saving, setSaving] = useState<File[] | null>(null);
  const body = useRef<HTMLDivElement>(null);

  const q = useQuery({
    queryKey: ['web-read', tab?.url], enabled: !!tab && caps.web, staleTime: 10 * 60_000, retry: false,
    queryFn: () => get<ReadResult>(`/api/web/read?url=${encodeURIComponent(tab!.url)}`),
  });
  const data = q.data;
  const sentences = useMemo(() => quoteSentences(tab?.quotes ?? []), [tab?.quotes]);
  const found = useMemo(() => {
    if (!data?.blocks) return [] as number[];
    const all = data.blocks.flatMap((b) => [b.text ?? '', ...(b.rows ?? []).flat()]).join('\n');
    return [...new Set(findMarks(all, sentences).map((m) => m.q))].sort((a, b) => a - b);
  }, [data, sentences]);

  useEffect(() => { body.current?.scrollTo({ top: 0 }); }, [tab?.url, mode]);
  // 打开时先滚到第一处引用
  useEffect(() => {
    if (mode !== 'reader' || found.length === 0) return;
    const t = setTimeout(() => body.current?.querySelector(`[data-q="${found[0]}"]`)?.scrollIntoView({ block: 'center' }), 60);
    return () => clearTimeout(t);
  }, [found, mode, tab?.url]);

  if (!tab) return null;
  const host = (() => { try { return new URL(tab.url).host; } catch { return tab.url; } })();
  const go = (n: number) => body.current?.querySelector(`[data-q="${n}"]`)?.scrollIntoView({ block: 'center', behavior: 'smooth' });
  const copy = async () => {
    try { await navigator.clipboard.writeText(tab.url); setMsg('已复制链接'); } catch { setMsg('复制失败'); }
    setTimeout(() => setMsg(null), 1500);
  };
  const save = () => {
    if (!data?.markdown) return;
    const name = `${(data.title ?? host).replace(/[\\/:*?"<>|]/g, ' ').slice(0, 60)}.md`;
    setSaving([new File([data.markdown], name, { type: 'text/markdown' })]);
  };
  const addrValue = addr?.for === tab.url ? addr.value : tab.url;

  return (
    <div className="pv-inner">
      <div className="wb-tabs" role="tablist" aria-label="打开的网页">
        {tabs.map((t, i) => (
          <span key={t.url} role="tab" aria-selected={t === tab} className={'wb-tab' + (t === tab ? ' on' : '')}>
            <button type="button" className="wb-tab-b" title={t.url} onClick={() => onActivate(i)}>
              {t.label && <span className="wb-n">{t.label}</span>}
              <span className="wb-tab-t">{t.title || (() => { try { return new URL(t.url).host; } catch { return t.url; } })()}</span>
            </button>
            <button type="button" className="wb-tab-x" aria-label="关闭这个网页" onClick={() => onCloseTab(i)}><Icon name="x" size={11} stroke={2.2} style={{ display: 'block' }} /></button>
          </span>
        ))}
        <span className="cb-sp" />
        <button type="button" className="refs-x" aria-label="关闭网页浏览" onClick={onClose}><Icon name="x" size={14} style={{ display: 'block' }} /></button>
      </div>
      <div className="wb-bar">
        <span className="wb-nav">
          <button type="button" aria-label="上一个网页" disabled={active <= 0} onClick={() => onActivate(active - 1)}>‹</button>
          <button type="button" aria-label="下一个网页" disabled={active >= tabs.length - 1} onClick={() => onActivate(active + 1)}>›</button>
        </span>
        <form className="wb-addr" onSubmit={(e) => { e.preventDefault(); const v = addrValue.trim(); if (/^https?:\/\//i.test(v) && v !== tab.url) onOpen(v); setAddr(null); }}>
          <span className={'wb-lock' + (tab.url.startsWith('https') ? ' ok' : '')} aria-hidden="true"><Icon name="dot" size={10} style={{ display: 'block' }} /></span>
          <input value={addrValue} aria-label="网址" spellCheck={false} onChange={(e) => setAddr({ for: tab.url, value: e.target.value })} />
        </form>
        <span className="pv-seg small">
          <button type="button" className={mode === 'reader' ? 'on' : ''} aria-pressed={mode === 'reader'} onClick={() => setModes({ ...modes, [tab.url]: 'reader' })}>阅读模式</button>
          <button type="button" className={mode === 'original' ? 'on' : ''} aria-pressed={mode === 'original'} onClick={() => setModes({ ...modes, [tab.url]: 'original' })}>原网页</button>
        </span>
      </div>
      <div className="pv-tools">
        {msg && <span className="cb-msg">{msg}</span>}
        <span className="cb-sp" />
        <button type="button" className="cb-btn" onClick={() => void q.refetch()}>刷新</button>
        <button type="button" className="cb-btn" onClick={() => window.open(tab.url, '_blank', 'noopener,noreferrer')}>新窗口打开</button>
        <button type="button" className="cb-btn" onClick={() => void copy()}>复制链接</button>
        {canSave && <button type="button" className="cb-btn" disabled={!data?.ok} title="存成文档，走正常的上传审核后入库" onClick={save}>收进资料库</button>}
      </div>
      {mode === 'reader' && sentences.length > 0 && data?.ok && (
        <div className="wb-cite">
          {found.length > 0 ? (
            <>
              <b>回答引用了这页 {found.length} 处</b>
              {found.map((n, i) => <button key={n} type="button" onClick={() => go(n)}>{'①②③④⑤⑥⑦⑧⑨⑩'[i] ?? i + 1} {sentences[n].slice(0, 12)}… ›</button>)}
            </>
          ) : <b>回答用的是搜索摘要，正文里没找到原句</b>}
        </div>
      )}
      <div className="wb-body sc" ref={body}>
        {!caps.web ? (
          <div className="wb-empty">
            <b>没有在系统里读网页的权限</b>
            <span>可以直接在新窗口打开这个网页。</span>
            <button type="button" className="pbtn" onClick={() => window.open(tab.url, '_blank', 'noopener,noreferrer')}>新窗口打开</button>
          </div>
        ) : mode === 'original' ? (
          data && !data.embeddable ? (
            <div className="wb-empty">
              <b>这个网站不让嵌入别的页面里显示</b>
              <span>很多网站（搜索引擎、问答社区、不少厂家官网）都这样。可以看阅读模式，或者在新窗口打开原网页。</span>
              <button type="button" className="pbtn" onClick={() => window.open(tab.url, '_blank', 'noopener,noreferrer')}>新窗口打开</button>
            </div>
          ) : (
            <div className="wb-orig">
              <div className="pv-note">原网页由你的电脑直接打开（隔离运行）；电脑上不了外网时会是空白，可以切回阅读模式。</div>
              <iframe title={`原网页：${host}`} src={tab.url} sandbox="allow-scripts allow-forms allow-popups allow-same-origin" referrerPolicy="no-referrer" />
            </div>
          )
        ) : q.isPending ? (
          <div style={{ padding: 20 }}><Spinner text="服务器正在读这个网页…" /></div>
        ) : q.isError ? (
          <div className="wb-empty"><b>读不了</b><span>{q.error instanceof ApiError ? q.error.message : '网页读取失败'}</span></div>
        ) : data && !data.ok ? (
          <div className="wb-empty">
            <b>阅读模式读不出来</b><span>{data.note}</span>
            <button type="button" className="pbtn" onClick={() => window.open(tab.url, '_blank', 'noopener,noreferrer')}>新窗口打开</button>
          </div>
        ) : data ? <Reader data={data} sentences={sentences} /> : null}
      </div>
      <div className="pv-ft">
        {data?.fetchedAt && <span>抓取于 {new Date(data.fetchedAt).toLocaleString('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })} · </span>}
        网上的内容，需自行核实。阅读模式由服务器抓取，去掉了广告、脚本和导航。
      </div>
      {saving && <UploadModal mode="staff" initialFiles={saving} onClose={() => setSaving(null)} onUploaded={() => { setSaving(null); setMsg('已提交，审核通过后入库'); }} />}
    </div>
  );
}
