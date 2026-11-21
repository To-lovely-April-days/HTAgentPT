// 「可操作」：服务器上那个浏览器的画面。每做一步（助手或你）画面刷新一次；你在画面上点、打字、滚动，转给服务器上的浏览器。
// 助手正在操作这一页时你先别动（服务器会挡回来），点上面的「停下」就能接过来。
import { useEffect, useRef, useState } from 'react';
import { ApiError, fetchBlobUrl, post } from '../../../lib/api';
import { Spinner } from '../../../components/Common';

const KEYS = new Set(['Enter', 'Backspace', 'Tab', 'Escape', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Delete', 'Home', 'End', 'PageUp', 'PageDown']);

export function LiveView({ sessionId, tab, rev, busy, onMsg }: {
  sessionId: string; tab: string; rev: number; busy: boolean; onMsg: (m: string | null) => void;
}) {
  const [img, setImg] = useState<{ key: string; url: string } | null>(null);
  const [focus, setFocus] = useState(false);
  const typed = useRef('');
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const base = `/api/browser/${sessionId}/t/${tab}`;
  const key = `${tab}:${rev}`;

  // 画面版本变了才重新取截图
  useEffect(() => {
    let alive = true;
    fetchBlobUrl(`${base}/shot?rev=${rev}`).then((u) => {
      // 新的一张到了再换，旧的那张的地址回收掉
      if (alive) setImg((cur) => { if (cur) URL.revokeObjectURL(cur.url); return { key, url: u }; });
      else URL.revokeObjectURL(u);
    }).catch(() => { /* 标签刚关掉 */ });
    return () => { alive = false; };
  }, [base, rev, key]);
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

  return (
    <div className={'lv-view' + (focus ? ' focus' : '') + (busy ? ' busy' : '')} tabIndex={0} role="application" aria-label="网页画面：点击、打字会转给服务器上的浏览器"
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
      {img ? (
        <img src={img.url} alt="网页画面" draggable={false}
          onClick={(e) => {
            if (busy) { onMsg('助手正在操作这一页，先点「停下」再动手'); return; }
            const r = e.currentTarget.getBoundingClientRect();
            flush();
            void send({ kind: 'click', x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height });
          }} />
      ) : <div style={{ padding: 20 }}><Spinner text="打开网页…" /></div>}
      {!focus && img && !busy && <span className="lv-tip">点画面后可以直接打字、按回车、滚轮翻页</span>}
    </div>
  );
}
