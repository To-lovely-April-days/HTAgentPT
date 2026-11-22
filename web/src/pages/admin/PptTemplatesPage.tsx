// 后台「PPT 模板」：模板库里的每一套——封面、风格、来源和许可、认出了哪些页面类型、开关。
// 系统自带 30 套开源模板（SlidesCarnival，CC BY 4.0）；管理员也能上传，上传要写来源和许可，不许转发的网站拦下。
import { useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, del, get, patch as patchReq, post, upload } from '../../lib/api';
import { Thumb } from '../chat/PptTemplates';
import { Icon } from '../../components/Icons';

interface Row {
  id: string; name: string; tags: string[]; builtIn: boolean; enabled: boolean; source: string; license: string; credit: string | null;
  slides: number; previews: number; size: number; types: string[]; status: string; problem: string | null;
}
interface Data { items: Row[]; builtInTotal: number; builtInMissing: number; licenses: string[]; allTypes: { code: string; label: string }[] }

function UploadForm({ licenses, onDone }: { licenses: string[]; onDone: () => void }) {
  const file = useRef<HTMLInputElement>(null);
  const [v, setV] = useState({ name: '', tags: '', source: '', license: licenses[0] ?? 'CC BY 4.0', credit: '' });
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const submit = async () => {
    const f = file.current?.files?.[0];
    if (!f) { setMsg('先选一个 .pptx 文件'); return; }
    const fd = new FormData();
    fd.append('file', f); fd.append('name', v.name || f.name.replace(/\.pptx$/i, '')); fd.append('tags', v.tags);
    fd.append('source', v.source); fd.append('license', v.license); fd.append('credit', v.credit);
    setBusy(true); setMsg('分析每页、转缩略图中，大约半分钟…');
    try {
      const r = await upload<{ status: string; problem: string | null; slides: number; types: string[] }>('/api/ppt-templates/admin/upload', fd);
      setMsg(r.status === 'ready' ? `收下了：${r.slides} 页，认出 ${r.types.join('、')}` : `收下了，但用不了：${r.problem}`);
      onDone();
    } catch (e) { setMsg(e instanceof ApiError ? e.message : '上传失败'); }
    finally { setBusy(false); }
  };
  return (
    <div className="pt-up">
      <b>上传模板</b>
      <label className="pt-f"><span>文件（.pptx）</span><input ref={file} type="file" accept=".pptx" /></label>
      <label className="pt-f"><span>名字</span><input value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })} placeholder="不写就用文件名" /></label>
      <label className="pt-f"><span>风格（逗号隔开）</span><input value={v.tags} onChange={(e) => setV({ ...v, tags: e.target.value })} placeholder="商务, 科技" /></label>
      <label className="pt-f"><span>从哪下载的（网址）</span><input value={v.source} onChange={(e) => setV({ ...v, source: e.target.value })} placeholder="https://… ；公司自己做的写「公司自有」" /></label>
      <label className="pt-f"><span>许可</span>
        <select value={v.license} onChange={(e) => setV({ ...v, license: e.target.value })}>{licenses.map((l) => <option key={l}>{l}</option>)}</select>
      </label>
      <label className="pt-f"><span>署名（CC BY 必填）</span><input value={v.credit} onChange={(e) => setV({ ...v, credit: e.target.value })} placeholder="Presentation template by …" /></label>
      <button type="button" className="pbtn" disabled={busy} onClick={() => void submit()}>{busy ? '处理中…' : '上传'}</button>
      {msg && <div className="hint" style={{ lineHeight: 1.6 }}>{msg}</div>}
      <div className="pt-warn"><Icon name="warn" size={13} stroke={2} /><span>能入库的：SlidesCarnival（CC BY 4.0，要署名）、LibreOffice 社区模板（CC BY-SA）、公司自己做的。
        不能入库的：Slidesgo、SlidesMania、微软 / OfficePLUS 模板——只许个人用、不许放进共享的库。</span></div>
    </div>
  );
}

export default function PptTemplatesPage() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['ppt-admin'], queryFn: () => get<Data>('/api/ppt-templates/admin'), refetchInterval: (d) => (d.state.data?.builtInMissing ? 15_000 : false) });
  const [msg, setMsg] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const reload = () => { void qc.invalidateQueries({ queryKey: ['ppt-admin'] }); void qc.invalidateQueries({ queryKey: ['ppt-templates'] }); };
  const act = async (key: string, fn: () => Promise<unknown>) => {
    setBusy(key); setMsg(null);
    try { await fn(); reload(); } catch (e) { setMsg(e instanceof ApiError ? e.message : '没办成'); } finally { setBusy(null); }
  };
  const d = q.data;
  const items = d?.items ?? [];
  return (
    <>
      <div className="qy-tabs">
        <b>PPT 模板</b>
        <span className="hint">助手「做 PPT」用的模板库：员工在输入框「＋ → PPT 模板」里挑，助手按页面类型从模板里挑样页、换字、检查</span>
      </div>
      <div className="qy">
        {d && (
          <div className="qy-kpis">
            <div className="qy-kpi"><span>模板</span><b>{items.length}</b><span>套</span></div>
            <div className="qy-kpi"><span>员工能选</span><b>{items.filter((t) => t.enabled && t.status === 'ready').length}</b><span>套</span></div>
            <div className="qy-kpi"><span>系统自带</span><b>{d.builtInTotal - d.builtInMissing} / {d.builtInTotal}</b><span>已导入</span></div>
            <div className="qy-kpi"><span>用不了</span><b>{items.filter((t) => t.status !== 'ready').length}</b><span>套（看原因）</span></div>
          </div>
        )}
        {d && d.builtInMissing > 0 && (
          <div className="pt-imp">
            <span>还有 {d.builtInMissing} 套系统自带的模板没导入（启动后会自动导入；沙箱、office 容器没起来时会失败）。</span>
            <button type="button" className="gbtn" disabled={busy === 'imp'} onClick={() => void act('imp', () => post('/api/ppt-templates/admin/import', {}))}>
              {busy === 'imp' ? '开始导入…' : '现在导入'}
            </button>
          </div>
        )}
        {msg && <div className="hint" style={{ color: 'var(--cls-conf)' }}>{msg}</div>}
        <div className="pt-wrap">
          <div className="pt-main">
            {q.isLoading && <div className="hint">取数据中…</div>}
            <table className="hzd-tbl pt-tbl">
              <thead><tr><th style={{ width: 120 }} /><th>模板</th><th>来源 · 许可</th><th>认出的页面（灰的是这套没有）</th><th>启用</th><th /></tr></thead>
              <tbody>
                {items.map((t) => (
                  <tr key={t.id} className={t.enabled ? undefined : 'off'}>
                    <td>{t.previews > 0 ? <Thumb path={`/api/ppt-templates/${t.id}/preview/1`} alt={`${t.name} 封面`} /> : <span className="ppt-th" />}</td>
                    <td><b>{t.name}</b><div className="hint">{t.tags.join(' · ')}{t.slides ? ` · ${t.slides} 页` : ''}{t.builtIn ? ' · 系统自带' : ''}</div>
                      {t.status !== 'ready' && <div className="pt-bad">用不了：{t.problem}</div>}</td>
                    <td className="pt-src">{/^https?:\/\//.test(t.source) ? <a href={t.source} target="_blank" rel="noopener noreferrer">{new URL(t.source).hostname}</a> : t.source}
                      <div><span className="pt-lic">{t.license}</span></div></td>
                    <td>{d?.allTypes.map((x) => <span key={x.code} className={'pt-ty' + (t.types.includes(x.code) ? '' : ' no')}>{x.label}</span>)}</td>
                    <td><button type="button" role="switch" aria-checked={t.enabled} aria-label={`${t.name}开关`} className={'tl-sw' + (t.enabled ? ' on' : '')}
                      disabled={busy === t.id} onClick={() => void act(t.id, () => patchReq(`/api/ppt-templates/admin/${t.id}`, { enabled: !t.enabled }))}><i /></button></td>
                    <td className="pt-ops">
                      <button type="button" className="hm-link" disabled={busy === 're' + t.id} onClick={() => void act('re' + t.id, () => post(`/api/ppt-templates/admin/${t.id}/reanalyze`, {}))}>
                        {busy === 're' + t.id ? '分析中…' : '重新分析'}</button>
                      {!t.builtIn && <button type="button" className="hm-link" onClick={() => { if (confirm(`删掉模板「${t.name}」？`)) void act('del' + t.id, () => del(`/api/ppt-templates/admin/${t.id}`)); }}>删除</button>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {d && <UploadForm licenses={d.licenses} onDone={reload} />}
        </div>
      </div>
    </>
  );
}
