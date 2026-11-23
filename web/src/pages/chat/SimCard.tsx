// 流程模拟（DWSIM）的结果卡：物流表（列是物流、行是温度压力流量组成）、设备（热负荷、功率）、DWSIM 自己画的流程图、下载 DWSIM 文件。
// 我们自己的流程图（标准图例）是另一张 pfd 组件，排在这张卡上面。
import { useEffect, useState } from 'react';
import { ApiError, download, fetchBlobUrl } from '../../lib/api';
import type { ToolArtifact } from '../../lib/types';
import { Icon } from '../../components/Icons';
import { exportXlsx } from './blocks/util';

type Sim = Extract<ToolArtifact, { kind: 'sim' }>;

const fmt = (v: number, d = 3) => (Number.isFinite(v) ? String(Math.round(v * 10 ** d) / 10 ** d) : '—');

/** DWSIM 截图是固定 1920×1080 的大白底：裁掉四周空白再显示。 */
function useCropped(url: string | null): string | null | false {
  const [src, setSrc] = useState<string | null | false>(null);
  useEffect(() => {
    if (!url) return;
    let alive = true;
    let raw: string | null = null;
    fetchBlobUrl(url).then((u) => {
      raw = u;
      const img = new Image();
      img.onload = () => {
        if (!alive) return;
        const c = document.createElement('canvas');
        c.width = img.width; c.height = img.height;
        const g = c.getContext('2d');
        if (!g) { setSrc(u); return; }
        g.drawImage(img, 0, 0);
        const d = g.getImageData(0, 0, c.width, c.height).data;
        let x0 = c.width, y0 = c.height, x1 = 0, y1 = 0;
        for (let y = 0; y < c.height; y += 2)
          for (let x = 0; x < c.width; x += 2) {
            const i = (y * c.width + x) * 4;
            if (d[i] < 235 || d[i + 1] < 235 || d[i + 2] < 235) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
          }
        if (x1 <= x0 || y1 <= y0) { setSrc(u); return; }
        const pad = 24;
        x0 = Math.max(0, x0 - pad); y0 = Math.max(0, y0 - pad); x1 = Math.min(c.width, x1 + pad); y1 = Math.min(c.height, y1 + pad);
        const out = document.createElement('canvas');
        out.width = x1 - x0; out.height = y1 - y0;
        out.getContext('2d')!.drawImage(c, x0, y0, out.width, out.height, 0, 0, out.width, out.height);
        setSrc(out.toDataURL('image/png'));
      };
      img.onerror = () => { if (alive) setSrc(false); };
      img.src = u;
    }).catch(() => { if (alive) setSrc(false); });
    return () => { alive = false; if (raw) URL.revokeObjectURL(raw); };
  }, [url]);
  return src;
}

export function SimCard({ a }: { a: Sim }) {
  // 上面的流程图已经带了物流表，这里先看设备
  const [tab, setTab] = useState<'streams' | 'units' | 'dwsim'>('units');
  const [msg, setMsg] = useState<string | null>(null);
  const img = useCropped(tab === 'dwsim' ? a.imageUrl : null);
  const comps = a.compounds.filter((c) => a.streams.some((s) => (s.comp[c] ?? 0) > 0));
  const rows: [string, (s: Sim['streams'][number]) => string][] = [
    ['温度 ℃', (s) => fmt(s.T, 1)], ['压力 MPa(G)', (s) => fmt(s.P, 3)], ['质量流量 kg/h', (s) => fmt(s.mass, 3)],
    ['摩尔流量 kmol/h', (s) => fmt(s.mole, 4)], ['汽相分率', (s) => fmt(s.vf, 3)],
    ...comps.map((c) => [`${c} 摩尔分率`, (s: Sim['streams'][number]) => (s.comp[c] ? fmt(s.comp[c], 4) : '0')] as [string, (s: Sim['streams'][number]) => string]),
  ];
  const excel = () => void exportXlsx(a.title, [['物流', ...a.streams.map((s) => s.id + (s.label ? ` ${s.label}` : ''))],
    ['来自', ...a.streams.map((s) => s.from ?? '进料')], ['去往', ...a.streams.map((s) => s.to ?? '产品')],
    ...rows.map(([label, f]) => [label, ...a.streams.map(f)])]);

  return (
    <section className="tool-card sim">
      <div className="tool-card-hd">
        <span className="tool-ic calc" aria-hidden="true">模</span>
        <b>{a.title} · 流程模拟</b>
        <span className="sim-ok"><Icon name="check" size={12} stroke={2.6} />已收敛</span>
        <span className="hint">DWSIM · 物性方法 {a.package} · {a.seconds} 秒</span>
      </div>
      <div className="sim-tabs" role="tablist">
        {([['streams', `物流 ${a.streams.length}`], ['units', `设备 ${a.units.length}`], ...(a.imageUrl ? [['dwsim', 'DWSIM 原图']] : [])] as [typeof tab, string][]).map(([k, l]) => (
          <button key={k} type="button" role="tab" aria-selected={tab === k} className={'sim-tab' + (tab === k ? ' on' : '')} onClick={() => setTab(k)}>{l}</button>
        ))}
      </div>
      <div className="tool-card-body">
        {tab === 'streams' && (
          <div className="sim-tw">
            <table className="sim-t">
              <thead><tr><th>物流</th>{a.streams.map((s) => <th key={s.id} title={s.label || undefined}><span className="sim-sn">{s.id}</span>{s.label && <small>{s.label}</small>}</th>)}</tr></thead>
              <tbody>
                <tr className="sub"><td>来自<Icon name="forward" size={12} className="gi m" />去往</td>{a.streams.map((s) => <td key={s.id}>{s.from ?? '进料'}<Icon name="forward" size={12} className="gi m" />{s.to ?? '产品'}</td>)}</tr>
                {rows.map(([label, f]) => <tr key={label}><td>{label}</td>{a.streams.map((s) => <td key={s.id}>{f(s)}</td>)}</tr>)}
              </tbody>
            </table>
          </div>
        )}
        {tab === 'units' && (
          <div className="sim-units">
            {a.units.map((u) => (
              <div key={u.id} className="sim-unit">
                <div className="sim-unit-hd"><b>{u.id}</b><span>{u.label || u.typeName}</span><span className="hint">{u.label ? u.typeName : ''}</span>
                  {u.duty != null && <span className="sim-duty">{u.dutyKind} <b>{fmt(u.duty, 3)}</b> kW</span>}</div>
                {u.props.length > 0 && <div className="sim-props">{u.props.map((p) => <span key={p.name}>{p.name}：{p.value} {p.units}</span>)}</div>}
              </div>
            ))}
          </div>
        )}
        {tab === 'dwsim' && (img ? <img className="sim-img" src={img} alt="DWSIM 画的流程图" /> : <span className="hint">{img === false ? '图取不到了（可能过了保留期）' : '取图中…'}</span>)}
        {a.notes && <div className="hint">DWSIM 的提示：{a.notes}</div>}
        <div className="sim-foot">
          {a.fileUrl && (
            <button type="button" className="gbtn" onClick={() => void download(a.fileUrl!, `${a.title}.dwxmz`).catch((e) => setMsg(e instanceof ApiError ? e.message : '下载失败'))}>
              <Icon name="upload" size={13} style={{ transform: 'rotate(180deg)', verticalAlign: '-2px', marginRight: 4 }} />下载 DWSIM 文件
            </button>
          )}
          <button type="button" className="gbtn" onClick={excel}>导出物流表（Excel）</button>
          <span className="hint">模拟估算：物性方法和给定条件决定结果，正式设计请工艺工程师核对。DWSIM 文件可用 DWSIM 桌面版打开、接着改。</span>
          {msg && <span className="hint" style={{ color: 'var(--cls-conf)' }}>{msg}</span>}
        </div>
      </div>
    </section>
  );
}
