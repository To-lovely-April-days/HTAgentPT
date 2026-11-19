// 网页预览：模型写的 HTML 在隔离的沙箱里运行。
// 沙箱不给 allow-same-origin：页面的来源是「空」，读不到本系统的登录信息和数据、调不了系统接口，也跳不走当前页面。
// React、Vue、ECharts、Tailwind 等常用 CDN 地址换成本系统 /vendor/ 下的副本，内网也能用。
import { useEffect, useMemo, useRef, useState } from 'react';
import { CodeBlock } from '../../../components/code/CodeBlock';
import { downloadText } from '../../../components/code/text';
import { htmlTitle } from '../../../components/code/preview';
import { inlineVendors, openInWindow, prepareHtml, SANDBOX } from './html';

const DEVICES = [
  { key: 'desktop', label: '桌面', width: null },
  { key: 'tablet', label: '平板', width: 768 },
  { key: 'phone', label: '手机', width: 390 },
] as const;

export function HtmlPreview({ code, versions, onPick, onClose }: {
  code: string; versions: string[]; onPick: (code: string) => void; onClose: () => void;
}) {
  const [tab, setTab] = useState<'preview' | 'code'>('preview');
  const [device, setDevice] = useState<(typeof DEVICES)[number]['key']>('desktop');
  const [nonce, setNonce] = useState(0);
  const [msg, setMsg] = useState<string | null>(null);
  const box = useRef<HTMLDivElement>(null);
  const prepared = useMemo(() => prepareHtml(code), [code]);
  // 常用库的代码写进页面后再显示（取库是异步的）
  const [ready, setReady] = useState<{ for: string; html: string } | null>(null);
  useEffect(() => {
    let alive = true;
    void inlineVendors(prepared.html).then((html) => { if (alive) setReady({ for: prepared.html, html }); });
    return () => { alive = false; };
  }, [prepared.html]);
  const doc = ready?.for === prepared.html ? ready.html : null;
  const title = htmlTitle(code);
  const idx = versions.lastIndexOf(code);
  const width = DEVICES.find((d) => d.key === device)?.width ?? null;

  const copy = async () => {
    try { await navigator.clipboard.writeText(code); setMsg('已复制代码'); } catch { setMsg('复制失败'); }
    setTimeout(() => setMsg(null), 1500);
  };
  const full = () => { void box.current?.requestFullscreen?.(); };

  return (
    <div className="pv-inner" ref={box}>
      <div className="pv-hd">
        <span className="pv-ic" aria-hidden="true">网页</span>
        <b className="pv-t" title={title}>{title}</b>
        {versions.length > 1 && idx >= 0 && (
          <select className="pv-ver" value={idx} aria-label="版本" onChange={(e) => onPick(versions[Number(e.target.value)])}>
            {versions.map((_, i) => <option key={i} value={i}>第 {i + 1} 版{i === versions.length - 1 ? '（最新）' : ''}</option>)}
          </select>
        )}
        <span className="pv-seg" role="tablist">
          <button type="button" role="tab" aria-selected={tab === 'preview'} className={tab === 'preview' ? 'on' : ''} onClick={() => setTab('preview')}>预览</button>
          <button type="button" role="tab" aria-selected={tab === 'code'} className={tab === 'code' ? 'on' : ''} onClick={() => setTab('code')}>代码</button>
        </span>
        <button type="button" className="refs-x" aria-label="关闭预览" onClick={onClose}>✕</button>
      </div>
      <div className="pv-tools">
        {tab === 'preview' && (
          <span className="pv-seg small">
            {DEVICES.map((d) => (
              <button key={d.key} type="button" className={device === d.key ? 'on' : ''} aria-pressed={device === d.key} onClick={() => setDevice(d.key)}>{d.label}</button>
            ))}
          </span>
        )}
        <span className="cb-sp" />
        {msg && <span className="cb-msg">{msg}</span>}
        {tab === 'preview' && <button type="button" className="cb-btn" onClick={() => setNonce(nonce + 1)}>刷新</button>}
        <button type="button" className="cb-btn" onClick={full}>全屏</button>
        <button type="button" className="cb-btn" disabled={!doc} onClick={() => { if (doc) openInWindow(doc, title); }}>新窗口打开</button>
        <button type="button" className="cb-btn" onClick={() => downloadText(`${title}.html`, code, 'text/html')}>下载</button>
        <button type="button" className="cb-btn" onClick={() => void copy()}>复制</button>
      </div>
      {tab === 'preview' && prepared.external.length > 0 && (
        <div className="pv-note">页面引用了外网资源（{prepared.external.slice(0, 3).join('、')}{prepared.external.length > 3 ? ' 等' : ''}），内网电脑上可能加载不了。</div>
      )}
      {tab === 'preview' ? (
        <div className={'pv-stage' + (width ? ' framed' : '')}>
          {doc == null ? <div className="hint" style={{ padding: 16 }}>正在准备预览…</div> : (
            <iframe key={nonce} title={`网页预览：${title}`} sandbox={SANDBOX} srcDoc={doc}
              style={width ? { width, maxWidth: '100%' } : undefined} referrerPolicy="no-referrer" />
          )}
        </div>
      ) : (
        <div className="pv-code sc"><CodeBlock code={code} lang="html" bare unfold /></div>
      )}
      <div className="pv-ft">在隔离的沙箱里运行：读不到系统的登录信息和数据，也不能调用系统接口。</div>
    </div>
  );
}
