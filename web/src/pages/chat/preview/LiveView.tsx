// 「可操作」：服务器上那个浏览器的画面。实时画面（服务器推过来的连续帧）优先；推不过来时退回到「每做一步刷新一张截图」。
// 你在画面上点、打字、滚动，转给服务器上的浏览器。助手正在操作这一页时你先别动（服务器会挡回来），点上面的「停下」就能接过来。
import { useEffect, useRef, useState } from 'react';
import type { MouseEvent } from 'react';
import { ApiError, fetchBlobUrl, post } from '../../../lib/api';
import { Spinner } from '../../../components/Common';

/** 读服务器推过来的连续帧（multipart：每段一个 JPEG，带 Content-Length），一帧一帧交出来。
 *  不用 <img> 直接放流：有的浏览器（包括一些内嵌浏览器）不放这种流。 */
async function readFrames(url: string, signal: AbortSignal, onFrame: (jpeg: Uint8Array) => void) {
  const r = await fetch(url, { signal });
  if (!r.ok || !r.body) throw new Error('没有实时画面');
  const reader = r.body.getReader();
  let buf = new Uint8Array(0);
  const find = (hay: Uint8Array, needle: number[], from: number) => {
    outer: for (let i = from; i <= hay.length - needle.length; i++) {
      for (let j = 0; j < needle.length; j++) if (hay[i + j] !== needle[j]) continue outer;
      return i;
    }
    return -1;
  };
  const LEN = [...'Content-Length: '].map((c) => c.charCodeAt(0));
  const END = [13, 10, 13, 10];
  for (;;) {
    const { value, done } = await reader.read();
    if (done) return;
    const next = new Uint8Array(buf.length + value.length);
    next.set(buf); next.set(value, buf.length);
    buf = next;
    for (;;) {
      const at = find(buf, LEN, 0);
      if (at < 0) break;
      const end = find(buf, END, at);
      if (end < 0) break;
      const n = parseInt(new TextDecoder().decode(buf.slice(at + LEN.length, end)), 10);
      if (!(n > 0)) { buf = buf.slice(end + 4); continue; }
      if (buf.length < end + 4 + n) break;
      onFrame(buf.slice(end + 4, end + 4 + n));
      buf = buf.slice(end + 4 + n);
    }
  }
}

const KEYS = new Set(['Enter', 'Backspace', 'Tab', 'Escape', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Delete', 'Home', 'End', 'PageUp', 'PageDown']);

export function LiveView({ sessionId, tab, rev, busy, onMsg }: {
  sessionId: string; tab: string; rev: number; busy: boolean; onMsg: (m: string | null) => void;
}) {
  const [img, setImg] = useState<{ key: string; url: string } | null>(null);
  const [stream, setStream] = useState<{ for: string; url: string } | null>(null);
  const [broken, setBroken] = useState<string | null>(null);
  const [focus, setFocus] = useState(false);
  const typed = useRef('');
  const box = useRef<HTMLDivElement>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const base = `/api/browser/${sessionId}/t/${tab}`;
  const key = `${tab}:${rev}`;
  const live = stream?.for === tab && broken !== tab ? stream.url : null;
  const [frame, setFrame] = useState<string | null>(null);

  // 实时画面：一帧一帧读，换成图片地址；读不了就退回截图
  useEffect(() => {
    if (!live) return;
    const ac = new AbortController();
    let last: string | null = null;
    readFrames(live, ac.signal, (jpeg) => {
      const u = URL.createObjectURL(new Blob([jpeg as BlobPart], { type: 'image/jpeg' }));
      setFrame(u);
      if (last) URL.revokeObjectURL(last);
      last = u;
    }).catch(() => { if (!ac.signal.aborted) setBroken(tab); });
    return () => { ac.abort(); if (last) URL.revokeObjectURL(last); };
  }, [live, tab]);

  // 实时画面：先换一张票（<img> 带不了登录头），10 分钟换一次，断了能接上
  useEffect(() => {
    let alive = true;
    const get = () => post<{ url: string }>(`${base}/ticket`, {})
      .then((r) => { if (alive) setStream({ for: tab, url: `${r.url}&n=${Date.now()}` }); })
      .catch(() => { if (alive) setBroken(tab); });
    void get();
    const t = setInterval(() => void get(), 10 * 60_000);
    return () => { alive = false; clearInterval(t); };
  }, [base, tab]);

  // 没有实时画面时：画面版本变了才重新取一张截图
  useEffect(() => {
    if (live) return;
    let alive = true;
    fetchBlobUrl(`${base}/shot?rev=${rev}`).then((u) => {
      if (alive) setImg((cur) => { if (cur) URL.revokeObjectURL(cur.url); return { key, url: u }; });
      else URL.revokeObjectURL(u);
    }).catch(() => { /* 标签刚关掉 */ });
    return () => { alive = false; };
  }, [base, rev, key, live]);
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);

  const send = async (body: Record<string, unknown>) => {
    try { await post(`${base}/input`, body); onMsg(null); }
    catch (e) { onMsg(e instanceof ApiError ? e.message : '没传过去'); }
  };
  const flush = () => {
    const t = typed.current;
    typed.current = '';
    if (t) void send({ kind: 'type', text: t });
  };
  const click = (e: MouseEvent<HTMLImageElement>) => {
    if (busy) { onMsg('助手正在操作这一页，先点「停下」再动手'); return; }
    box.current?.focus();          // 点了画面，接下来打的字都给网页（不然会落到对话输入框里）
    const r = e.currentTarget.getBoundingClientRect();
    flush();
    void send({ kind: 'click', x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height });
  };
  const src = live ? frame : img?.url ?? null;

  return (
    <div ref={box} className={'lv-view' + (focus ? ' focus' : '') + (busy ? ' busy' : '')} tabIndex={0} role="application" aria-label="网页画面：点击、打字会转给服务器上的浏览器"
      onFocus={() => setFocus(true)} onBlur={() => { setFocus(false); flush(); }}
      onKeyDown={(e) => {
        if (busy || e.metaKey || e.ctrlKey || e.altKey) return;
        if (KEYS.has(e.key)) { e.preventDefault(); flush(); void send({ kind: 'key', key: e.key }); return; }
        if (e.key.length === 1) {
          e.preventDefault();
          typed.current += e.key;
          if (timer.current) clearTimeout(timer.current);
          timer.current = setTimeout(flush, 250);
        }
      }}
      onPaste={(e) => { if (busy) return; const t = e.clipboardData.getData('text').slice(0, 500); if (t) { e.preventDefault(); flush(); void send({ kind: 'type', text: t }); } }}
      onWheel={(e) => { if (!busy) void send({ kind: 'scroll', dy: Math.round(e.deltaY * 2) }); }}>
      {src ? (
        <img src={src} alt="网页画面" draggable={false} onClick={click} />
      ) : <div style={{ padding: 20 }}><Spinner text="打开网页…" /></div>}
      {live && <span className="lv-badge">实时</span>}
      {!focus && src && !busy && <span className="lv-tip">点画面后可以直接打字、按回车、滚轮翻页</span>}
    </div>
  );
}
