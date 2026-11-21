// 网页浏览：回答里的网上来源、用户贴的网址都在这里打开，多个网页是多个标签页。
// 默认「阅读模式」：服务器去抓网页，只回正文、标题、表格、图片这些纯数据，这里按块排版——
// 网站的脚本不会在用户电脑上运行，用户电脑本身上不了外网也能看。回答引用的句子标黄。
// 「原网页」：网站允许被嵌入才显示原样，否则说明一下，给「新窗口打开」。
// 「可操作」：服务器上跑的真浏览器（内置浏览器），助手和你都能点、能填；标签上的像素小人表示谁在操作。
import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ApiError, del, fetchBlobUrl, get, post } from '../../../lib/api';
import { useAuth } from '../../../lib/auth';
import { Perm } from '../../../lib/types';
import { Spinner } from '../../../components/Common';
import { useCaps } from '../../../components/code/caps';
import UploadModal from '../../admin/UploadModal';
import { findMarks, quoteSentences } from './marks';
import { Icon } from '../../../components/Icons';
import { PixelSprite } from '../../../components/pet/PixelSprite';
import { tintOf } from '../../../components/pet/tints';
import { LiveView } from './LiveView';

/** 一个网页标签。live：内置浏览器里的标签号（「可操作」的网页才有）。 */
export interface WebTab { url: string; title?: string; label?: string; quotes?: string[]; live?: string }

interface LiveTab { tab: string; url: string; title: string; rev: number; who: number | null; live: boolean; done: boolean; label: string | null }
interface LiveState { alive: boolean; stopped: boolean; tabs: LiveTab[] }
type Mode = 'reader' | 'original' | 'live';

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


const keyOf = (t: WebTab) => t.live ?? t.url;
const hostOf = (u: string) => { try { return new URL(u).host; } catch { return u; } };
const FAV = ['#1F3A5F', '#0E6E5C', '#1D4A94', '#8A2E08', '#5C5C64', '#6A48F6', '#8F3D74', '#0E8A8A'];
const favColor = (h: string) => FAV[[...h].reduce((a, c) => a + c.charCodeAt(0), 0) % FAV.length];

/** 标签上的小人：谁在操作（主助手红，子代理换颜色），正在动的带蓝点、做完打勾。 */
function Who({ m }: { m?: LiveTab }) {
  if (!m || m.who === null) return null;
  return (
    <span className="wb-who" title={m.who === 0 ? '主助手' : `子代理 ${m.who}`}>
      <PixelSprite pose="idle" width={13} tint={tintOf(m.who)} />
      {m.live && <i className="wb-live" />}
      {!m.live && m.done && <i className="wb-done"><Icon name="check" size={9} stroke={3} /></i>}
    </span>
  );
}

export function WebBrowser({ tabs, active, sessionId, onActivate, onCloseTab, onOpen, onSetLive, onAdopt, onClose }: {
  tabs: WebTab[]; active: number; sessionId: string | null; onActivate: (i: number) => void; onCloseTab: (i: number) => void;
  onOpen: (url: string) => void; onSetLive: (i: number, live: string, url?: string, title?: string) => void;
  onAdopt: (tabs: WebTab[]) => void; onClose: () => void;
}) {
  const caps = useCaps();
  const { profile } = useAuth();
  const canSave = profile?.permissions.includes(Perm.CorpusUpload) ?? false;
  const tab = tabs[Math.min(active, tabs.length - 1)];
  const [modes, setModes] = useState<Record<string, Mode>>({});
  const mode: Mode = tab ? modes[keyOf(tab)] ?? (tab.live ? 'live' : 'reader') : 'reader';
  const [addr, setAddr] = useState<{ for: string; value: string } | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const [menu, setMenu] = useState(false);
  const [all, setAll] = useState(false);
  const [saving, setSaving] = useState<File[] | null>(null);
  const body = useRef<HTMLDivElement>(null);
  const addrInput = useRef<HTMLInputElement>(null);
  const canLive = !!caps.browser && !!sessionId;
  const anyLive = tabs.some((t) => t.live);

  // 内置浏览器的标签、谁在操作：开着可操作的网页时每秒看一次
  const state = useQuery({
    queryKey: ['browser-state', sessionId], enabled: canLive && anyLive, retry: false,
    refetchInterval: (q) => ((q.state.data as LiveState | undefined)?.tabs.some((t) => t.live) || mode === 'live' ? 1000 : 4000),
    queryFn: () => get<LiveState>(`/api/browser/${sessionId}/state`),
  });
  const marks = useMemo(() => new Map((state.data?.tabs ?? []).map((t) => [t.tab, t])), [state.data]);
  // 浏览器里新冒出来的标签（网页自己开的新窗口、助手刚开的）也放进来
  useEffect(() => {
    const known = new Set(tabs.map((t) => t.live).filter(Boolean));
    const extra = (state.data?.tabs ?? []).filter((t) => !known.has(t.tab)).map((t) => ({ url: t.url, title: t.title, live: t.tab }));
    if (extra.length > 0) onAdopt(extra);
  }, [state.data, tabs, onAdopt]);

  const readUrl = tab && mode === 'reader' ? tab.url : null;
  const q = useQuery({
    queryKey: ['web-read', readUrl], enabled: !!readUrl && caps.web, staleTime: 10 * 60_000, retry: false,
    queryFn: () => get<ReadResult>(`/api/web/read?url=${encodeURIComponent(readUrl!)}`),
  });
  const data = q.data;
  const sentences = useMemo(() => quoteSentences(tab?.quotes ?? []), [tab?.quotes]);
  const found = useMemo(() => {
    if (!data?.blocks) return [] as number[];
    const all2 = data.blocks.flatMap((b) => [b.text ?? '', ...(b.rows ?? []).flat()]).join('\n');
    return [...new Set(findMarks(all2, sentences).map((m) => m.q))].sort((a, b) => a - b);
  }, [data, sentences]);

  useEffect(() => { body.current?.scrollTo({ top: 0 }); }, [tab?.url, mode]);
  useEffect(() => {
    if (mode !== 'reader' || found.length === 0) return;
    const t = setTimeout(() => body.current?.querySelector(`[data-q="${found[0]}"]`)?.scrollIntoView({ block: 'center' }), 60);
    return () => clearTimeout(t);
  }, [found, mode, tab?.url]);

  if (!tab) return null;
  const live = tab.live ? marks.get(tab.live) : undefined;
  const url = live?.url || tab.url;
  const host = hostOf(url);
  const flash = (m: string) => { setMsg(m); setTimeout(() => setMsg(null), 1800); };
  const go = (n: number) => body.current?.querySelector(`[data-q="${n}"]`)?.scrollIntoView({ block: 'center', behavior: 'smooth' });
  const copy = async () => { try { await navigator.clipboard.writeText(url); flash('已复制链接'); } catch { flash('复制失败'); } };
  const save = () => {
    if (!data?.markdown) return;
    const name = `${(data.title ?? host).replace(/[\\/:*?"<>|]/g, ' ').slice(0, 60)}.md`;
    setSaving([new File([data.markdown], name, { type: 'text/markdown' })]);
  };
  const setMode = async (m: Mode) => {
    setMenu(false);
    if (m === 'live' && !tab.live) {
      if (!canLive) { flash(sessionId ? '内置浏览器没开，或你的角色不能用' : '先在对话里问一句，再用「可操作」'); return; }
      try {
        const r = await post<{ tab: string; url: string; title: string }>(`/api/browser/${sessionId}/open`, { url: tab.url });
        onSetLive(active, r.tab, r.url, r.title);
        setModes((x) => ({ ...x, [r.tab]: 'live' }));
      } catch (e) { flash(e instanceof ApiError ? e.message : '没打开'); }
      return;
    }
    setModes((x) => ({ ...x, [keyOf(tab)]: m }));
  };
  const nav = async (action: 'back' | 'forward' | 'reload') => {
    if (mode === 'live' && tab.live) {
      try { await post(`/api/browser/${sessionId}/t/${tab.live}/nav`, { action }); void state.refetch(); }
      catch (e) { flash(e instanceof ApiError ? e.message : '没成功'); }
    } else if (action === 'reload') void q.refetch();
  };
  const submitAddr = async (v: string) => {
    let u = v.trim();
    if (!u) return;
    if (!/^https?:\/\//i.test(u)) u = 'https://' + u;
    setAddr(null);
    if (mode === 'live' && tab.live && u !== url) {
      try { await post(`/api/browser/${sessionId}/open`, { url: u, tab: tab.live }); void state.refetch(); }
      catch (e) { flash(e instanceof ApiError ? e.message : '没打开'); }
    } else if (u !== tab.url) onOpen(u);
  };
  const close = (i: number) => {
    const t = tabs[i];
    if (t.live && sessionId) void del(`/api/browser/${sessionId}/t/${t.live}`).catch(() => {});
    onCloseTab(i);
  };
  const stop = async () => {
    try { await post(`/api/browser/${sessionId}/stop`, {}); void state.refetch(); flash('助手停下了，现在由你操作'); }
    catch (e) { flash(e instanceof ApiError ? e.message : '没停下'); }
  };
  const addrValue = addr?.for === keyOf(tab) ? addr.value : url;
  const many = tabs.length > 4;

  return (
    <div className="pv-inner" style={{ position: 'relative' }}>
      <div className="wb-tabs" role="tablist" aria-label="打开的网页">
        {tabs.map((t, i) => {
          const m = t.live ? marks.get(t.live) : undefined;
          const title = m?.title || t.title || hostOf(m?.url || t.url);
          const on = i === Math.min(active, tabs.length - 1);
          const mini = many && !on;
          const h = hostOf(m?.url || t.url);
          return (
            <span key={keyOf(t)} role="tab" aria-selected={on} className={'wb-tab' + (on ? ' on' : '') + (mini ? ' mini' : '')} title={`${title}\n${m?.url || t.url}`}>
              <button type="button" className="wb-tab-b" onClick={() => { onActivate(i); setMenu(false); }}>
                <span className="wb-fav" style={{ background: favColor(h) }}>{(title.trim()[0] ?? h[0] ?? '·').toUpperCase()}</span>
                {t.label && !mini && <span className="wb-n">{t.label}</span>}
                {!mini && <span className="wb-tab-t">{title}</span>}
                <Who m={m} />
              </button>
              {!mini && <button type="button" className="wb-tab-x" aria-label="关闭这个网页" onClick={() => close(i)}><Icon name="x" size={11} stroke={2.2} style={{ display: 'block' }} /></button>}
            </span>
          );
        })}
        <button type="button" className="wb-plus" aria-label="打开新网页" title="打开新网页" onClick={() => { setAddr({ for: keyOf(tab), value: '' }); setTimeout(() => addrInput.current?.focus(), 0); }}>＋</button>
        {many && <button type="button" className="wb-all" aria-expanded={all} onClick={() => setAll(!all)}>全部 {tabs.length} 个 ▾</button>}
        <button type="button" className="wb-close" style={many ? undefined : { marginLeft: 'auto' }} aria-label="关闭网页浏览" onClick={onClose}><Icon name="x" size={14} style={{ display: 'block' }} /></button>
      </div>
      {all && (
        <div className="wb-drop" role="menu">
          {tabs.map((t, i) => {
            const m = t.live ? marks.get(t.live) : undefined;
            const title = m?.title || t.title || hostOf(m?.url || t.url);
            return (
              <div key={keyOf(t)} className={'wb-drop-r' + (i === active ? ' on' : '')}>
                <button type="button" onClick={() => { onActivate(i); setAll(false); }}>
                  <span className="wb-fav" style={{ background: favColor(hostOf(m?.url || t.url)) }}>{(title.trim()[0] ?? '·').toUpperCase()}</span>
                  <span className="wb-drop-t">{title}</span>
                  <span className="wb-drop-s">{m?.live ? '在操作' : m?.done ? '做完了' : t.live ? '可操作' : '阅读'}</span>
                  <Who m={m} />
                </button>
                <button type="button" className="wb-tab-x" aria-label="关闭这个网页" onClick={() => close(i)}><Icon name="x" size={11} stroke={2.2} style={{ display: 'block' }} /></button>
              </div>
            );
          })}
        </div>
      )}
      <div className="wb-bar">
        <span className="wb-nav">
          <button type="button" aria-label="后退" disabled={mode !== 'live'} onClick={() => void nav('back')}>←</button>
          <button type="button" aria-label="前进" disabled={mode !== 'live'} onClick={() => void nav('forward')}>→</button>
          <button type="button" aria-label="刷新" disabled={mode === 'original'} onClick={() => void nav('reload')}>↻</button>
        </span>
        <form className="wb-addr" onSubmit={(e) => { e.preventDefault(); void submitAddr(addrValue); }}>
          <span className={'wb-lock' + (url.startsWith('https') ? ' ok' : '')} aria-hidden="true"><Icon name="dot" size={10} style={{ display: 'block' }} /></span>
          <input ref={addrInput} value={addrValue} aria-label="网址" spellCheck={false} placeholder="输入网址，回车打开"
            onChange={(e) => setAddr({ for: keyOf(tab), value: e.target.value })} />
        </form>
        <span className="pv-seg small" role="group" aria-label="怎么看这个网页">
          <button type="button" className={mode === 'reader' ? 'on' : ''} aria-pressed={mode === 'reader'} onClick={() => void setMode('reader')}>阅读</button>
          <button type="button" className={mode === 'original' ? 'on' : ''} aria-pressed={mode === 'original'} onClick={() => void setMode('original')}>原网页</button>
          {caps.browser && <button type="button" className={mode === 'live' ? 'on' : ''} aria-pressed={mode === 'live'} title="服务器上的真浏览器：助手和你都能点、能填" onClick={() => void setMode('live')}>可操作</button>}
        </span>
        <span className="wb-more-w">
          <button type="button" className={'wb-more' + (menu ? ' on' : '')} aria-label="更多" aria-expanded={menu} onClick={() => setMenu(!menu)}>···</button>
          {menu && (
            <div className="wb-menu" role="menu">
              <button type="button" onClick={() => { setMenu(false); void nav('reload'); }}>刷新</button>
              <button type="button" onClick={() => { setMenu(false); window.open(url, '_blank', 'noopener,noreferrer'); }}>在新窗口打开</button>
              <button type="button" onClick={() => { setMenu(false); void copy(); }}>复制链接</button>
              {canSave && mode === 'reader' && <><i /><button type="button" disabled={!data?.ok} title="存成文档，走正常的上传审核后入库" onClick={() => { setMenu(false); save(); }}>收进资料库</button></>}
            </div>
          )}
        </span>
      </div>
      {mode === 'live' && live?.live && (
        <div className="wb-strip ai">
          <PixelSprite pose="idle" width={13} tint={tintOf(live.who ?? 0)} />
          <span>{live.who ? `子代理 ${live.who}` : '助手'}正在操作 · {live.label ?? '…'}</span>
          <button type="button" onClick={() => void stop()}>停下</button>
        </div>
      )}
      {mode === 'live' && !live?.live && tab.live && (
        <div className="wb-strip me"><span>你在操作 · 直接在画面上点、打字；问助手「这页」它能看到</span></div>
      )}
      {msg && <div className="wb-msg">{msg}</div>}
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
        {mode === 'live' ? (
          tab.live && sessionId ? (
            <LiveView sessionId={sessionId} tab={tab.live} rev={live?.rev ?? 0} busy={!!live?.live} onMsg={(m) => setMsg(m)} />
          ) : <div className="wb-empty"><b>这个网页还没在内置浏览器里打开</b></div>
        ) : !caps.web ? (
          <div className="wb-empty">
            <b>没有在系统里读网页的权限</b>
            <span>可以直接在新窗口打开这个网页。</span>
            <button type="button" className="pbtn" onClick={() => window.open(url, '_blank', 'noopener,noreferrer')}>新窗口打开</button>
          </div>
        ) : mode === 'original' ? (
          data && !data.embeddable ? (
            <div className="wb-empty">
              <b>这个网站不让嵌入别的页面里显示</b>
              <span>很多网站（搜索引擎、问答社区、不少厂家官网）都这样。可以看阅读模式{caps.browser ? '，或者用「可操作」在内置浏览器里打开' : ''}，或者在新窗口打开原网页。</span>
              <button type="button" className="pbtn" onClick={() => window.open(url, '_blank', 'noopener,noreferrer')}>新窗口打开</button>
            </div>
          ) : (
            <div className="wb-orig">
              <div className="pv-note">原网页由你的电脑直接打开（隔离运行）；电脑上不了外网时会是空白，可以切回阅读模式。</div>
              <iframe title={`原网页：${host}`} src={url} sandbox="allow-scripts allow-forms allow-popups allow-same-origin" referrerPolicy="no-referrer" />
            </div>
          )
        ) : q.isPending ? (
          <div style={{ padding: 20 }}><Spinner text="服务器正在读这个网页…" /></div>
        ) : q.isError ? (
          <div className="wb-empty"><b>读不了</b><span>{q.error instanceof ApiError ? q.error.message : '网页读取失败'}</span></div>
        ) : data && !data.ok ? (
          <div className="wb-empty">
            <b>阅读模式读不出来</b><span>{data.note}</span>
            <button type="button" className="pbtn" onClick={() => window.open(url, '_blank', 'noopener,noreferrer')}>新窗口打开</button>
          </div>
        ) : data ? <Reader data={data} sentences={sentences} /> : null}
      </div>
      <div className="pv-ft">
        {mode === 'live' ? '内置浏览器跑在公司服务器上：密码、验证码由你自己在画面上填；提交、确认这类按钮助手会先问你。'
          : <>{data?.fetchedAt && <span>抓取于 {new Date(data.fetchedAt).toLocaleString('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })} · </span>}
            网上的内容，需自行核实。阅读模式由服务器抓取，去掉了广告、脚本和导航。</>}
      </div>
      {saving && <UploadModal mode="staff" initialFiles={saving} onClose={() => setSaving(null)} onUploaded={() => { setSaving(null); flash('已提交，审核通过后入库'); }} />}
    </div>
  );
}
