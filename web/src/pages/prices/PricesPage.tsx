// 价格库：采购价格表导入后，助手估算材料成本时按这里查价。
// 看要「查看采购价格」权限；上传、上传新版、删除要「管理价格库」权限（采购、系统管理员）。
import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, del, get, post, upload } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { Perm } from '../../lib/types';
import type { ColumnMap, PriceHit, PriceOverview, PricePreview, PriceTableRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';

const FIELDS: [string, string][] = [
  ['name', '名称'], ['spec', '规格'], ['group', '分组'], ['unit', '数量单位'], ['price', '含税单价'], ['price_net', '未税单价'],
  ['price_unit', '计价单位'], ['supplier', '供应商 / 品牌'], ['model', '型号'], ['code', '物料代码'], ['lead', '货期'],
  ['freq', '采购频率'], ['note', '备注'], ['ignore', '不导入'],
];
const fmt = (v: number) => v.toLocaleString('zh-CN', { maximumFractionDigits: v >= 100 ? 0 : 2 });
const price = (lo: number | null, hi: number | null) => (lo == null ? '—' : hi == null || hi === lo ? fmt(lo) : `${fmt(lo)}–${fmt(hi)}`);

export default function PricesPage() {
  const { profile } = useAuth();
  const canManage = !!profile?.permissions.includes(Perm.PriceManage);
  const qc = useQueryClient();
  const [params] = useSearchParams();
  const overview = useQuery({ queryKey: ['prices'], queryFn: () => get<PriceOverview>('/api/prices') });
  const [q, setQ] = useState(params.get('q') ?? '');
  const [table, setTable] = useState('');
  const [material, setMaterial] = useState('');
  const [supplier, setSupplier] = useState('');
  const [debounced, setDebounced] = useState(q);
  useEffect(() => { const t = setTimeout(() => setDebounced(q), 300); return () => clearTimeout(t); }, [q]);
  const search = useQuery({
    queryKey: ['price-search', debounced, table, material, supplier],
    queryFn: () => get<PriceHit[]>(`/api/prices/search?${new URLSearchParams({ q: debounced, table, material, supplier, take: '60' })}`),
    enabled: !!overview.data,
  });
  const [drawer, setDrawer] = useState<{ table: PriceTableRow | null } | null>(null);
  const [error, setError] = useState<string | null>(null);

  if (overview.isLoading) return <div style={{ padding: 40 }}><Spinner text="载入…" /></div>;
  if (overview.error) return <div style={{ padding: 40 }}><ErrorBox message={overview.error instanceof ApiError ? overview.error.message : '载入失败'} /></div>;
  const o = overview.data!;
  const stale = o.tables.filter((t) => t.stale).length;

  const remove = async (t: PriceTableRow) => {
    if (!window.confirm(`删除价格表「${t.name}」和它的全部 ${t.versions} 个版本？删除后助手就查不到这些价格了，不能恢复。`)) return;
    setError(null);
    try { await del(`/api/prices/${t.id}`); void qc.invalidateQueries({ queryKey: ['prices'] }); void qc.invalidateQueries({ queryKey: ['price-search'] }); }
    catch (err) { setError(err instanceof ApiError ? err.message : '删除失败'); }
  };

  return (
    <div className="pl-page">
      <div className="pl-main sc">
        <div className="pl-head">
          <div>
            <h1 className="dash-title" style={{ fontSize: 24 }}>价格库</h1>
            <p className="dash-sub">采购价格表导入后，助手估算材料成本时按这里查价。只有有「查看采购价格」权限的人看得到；采购负责上传和更新。</p>
          </div>
          {canManage && <button type="button" className="pbtn" onClick={() => setDrawer({ table: null })}>上传价格表</button>}
        </div>
        {error && <ErrorBox message={error} />}

        <section className="us-sum" aria-label="概况">
          <div className="us-sum-c"><span className="dash-kpi-l">价格条数</span><span className="us-sum-v">{o.totalItems.toLocaleString()}</span></div>
          <div className="us-sum-c"><span className="dash-kpi-l">价格表</span><span className="us-sum-v">{o.tables.length}<small>张</small></span></div>
          <div className="us-sum-c"><span className="dash-kpi-l">近 30 天被助手查询</span><span className="us-sum-v">{o.queries30d}<small>次</small></span></div>
          <div className="us-sum-c"><span className="dash-kpi-l">超过 {o.staleMonths} 个月没更新</span><span className="us-sum-v" style={{ color: stale ? 'var(--dc-warn)' : undefined }}>{stale}<small>张</small></span></div>
        </section>

        <section className="dc-card">
          <div className="dc-scroll">
            <table className="dc-table pl-tables">
              <thead><tr><th>价格表</th><th className="num">价格条数</th><th>当前版本 · 新鲜度</th><th>数据问题</th><th>更新人</th><th /></tr></thead>
              <tbody>
                {o.tables.length === 0 && <tr><td colSpan={6} className="muted" style={{ padding: 24 }}>还没有价格表。{canManage ? '点右上角「上传价格表」。' : '请采购上传。'}</td></tr>}
                {o.tables.map((t) => {
                  const fresh = Math.max(4, 100 - (t.ageMonths / Math.max(1, o.staleMonths * 1.2)) * 100);
                  return (
                    <tr key={t.id}>
                      <td>
                        <span className="pl-file">
                          <span className="pl-ext" aria-hidden="true">XLSX</span>
                          <span style={{ minWidth: 0 }}><span className="cell-main">{t.name}</span><span className="cell-sub">{t.description ?? `${t.versions} 个版本`}</span></span>
                        </span>
                      </td>
                      <td className="num strong">{t.itemCount.toLocaleString()}</td>
                      <td>
                        <span className="pl-fresh">
                          <span className="pl-fresh-t"><span className="tnum">{t.versionDate ?? '—'}</span>
                            <b className={t.stale ? 'warn' : 'ok'}>{t.ageMonths === 0 ? '本月' : `${t.ageMonths} 个月`}{t.stale ? ' · 可能过时' : ''}</b></span>
                          <span className="bar"><i className={t.stale ? 'warn' : 'ok'} style={{ width: `${fresh}%` }} /></span>
                        </span>
                      </td>
                      <td>
                        <span className="pl-issues">
                          {t.issues.length === 0 ? <span className="st-pill ok">无问题</span>
                            : t.issues.slice(0, 3).map((i) => <span key={i.kind} className={'st-pill ' + (i.kind === 'unit' || i.kind === 'missing' ? 'mid' : 'gray')} title={i.message}>{i.label} {i.count}</span>)}
                        </span>
                      </td>
                      <td className="muted">{t.uploadedBy ?? '—'}</td>
                      <td className="pl-acts">
                        <button type="button" className="gbtn sm" onClick={() => { setTable(t.id); setQ(''); }}>查看</button>
                        {canManage && <button type="button" className="pbtn sm" onClick={() => setDrawer({ table: t })}>上传新版</button>}
                        {canManage && <button type="button" className="fm-act" aria-label={`删除 ${t.name}`} onClick={() => void remove(t)}>删除</button>}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </section>

        <section className="dc-card">
          <div className="pl-search">
            <b>查价格</b>
            <input className="inp" placeholder="材质、品名、规格，如「C276 管」「1/4 卡套」" value={q} onChange={(e) => setQ(e.target.value)} aria-label="查价格" />
            <select className="inp" value={table} onChange={(e) => setTable(e.target.value)} aria-label="价格表">
              <option value="">全部价格表</option>{o.tables.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
            </select>
            <select className="inp" value={material} onChange={(e) => setMaterial(e.target.value)} aria-label="材质">
              <option value="">全部材质</option>{o.materials.map((m) => <option key={m}>{m}</option>)}
            </select>
            <select className="inp" value={supplier} onChange={(e) => setSupplier(e.target.value)} aria-label="供应商">
              <option value="">全部供应商</option>{o.suppliers.map((m) => <option key={m}>{m}</option>)}
            </select>
            <span className="muted">{search.data ? `${search.data.length} 条${search.data.length >= 60 ? '（只显示前 60 条）' : ''}` : ''}</span>
          </div>
          <div className="dc-scroll">
            <table className="dc-table pl-hits">
              <thead><tr><th>名称</th><th>规格</th><th>供应商 · 型号</th><th>计价单位</th><th className="num">含税单价（元）</th><th>历次版本</th><th className="num">来源</th></tr></thead>
              <tbody>
                {search.isLoading && <tr><td colSpan={7}><Spinner text="查找…" /></td></tr>}
                {search.data?.length === 0 && <tr><td colSpan={7} className="muted" style={{ padding: 20 }}>没有查到。换个说法试试（材质 + 品名）。</td></tr>}
                {search.data?.map((h) => <HitRow key={h.id} h={h} />)}
              </tbody>
            </table>
          </div>
        </section>
      </div>
      {drawer && <ImportDrawer table={drawer.table} onClose={() => setDrawer(null)}
        onDone={() => { setDrawer(null); void qc.invalidateQueries({ queryKey: ['prices'] }); void qc.invalidateQueries({ queryKey: ['price-search'] }); }} />}
    </div>
  );
}

function HitRow({ h }: { h: PriceHit }) {
  const [open, setOpen] = useState(false);
  const hist = useQuery({
    queryKey: ['price-history', h.id], enabled: open,
    queryFn: () => get<{ versionDate: string; taxLow: number | null; taxHigh: number | null; fileName: string }[]>(`/api/prices/items/${h.id}/history`),
  });
  const up = (h.changePct ?? 0) > 0, down = (h.changePct ?? 0) < 0;
  return (
    <>
      <tr className="clickable" onClick={() => setOpen(!open)} aria-expanded={open}>
        <td><span className="cell-main">{h.name}</span>{h.group && <span className="cell-sub">{h.group}</span>}</td>
        <td className="tnum">{h.spec ?? '—'}</td>
        <td><span className="cell-main" style={{ fontWeight: 400 }}>{h.supplier ?? '—'}</span>{h.model && <span className="cell-sub mono">{h.model}</span>}</td>
        <td>{h.priceUnit ? <span className="unit-chip">{h.priceUnit}</span> : <span className="muted">—</span>}</td>
        <td className="num">
          <b className="tnum">{h.taxLow == null ? <span className="muted">没有价格</span> : price(h.taxLow, h.taxHigh)}</b>
          <span className={'cell-sub ' + (up ? 'd-up' : down ? 'd-down' : '')}>
            {h.changePct != null ? `${up ? '▲' : down ? '▼' : ''} ${Math.abs(h.changePct)}%` : h.taxLow != null && h.taxHigh !== h.taxLow ? '区间价' : h.taxDerived ? '按未税推算' : ''}
          </span>
        </td>
        <td className="muted">{h.versions} 个版本 {open ? '▴' : '▾'}</td>
        <td className="num mono muted">{h.tableName} #{h.rowNo}{h.stale && <span className="cell-sub warn">可能过时</span>}</td>
      </tr>
      {open && (
        <tr className="hist-row">
          <td colSpan={7}>
            {hist.isLoading ? <Spinner text="载入…" /> : (
              <div className="hist">
                {(hist.data ?? []).map((p, i) => (
                  <span key={i} className="hist-pt"><span className="tnum">{p.versionDate}</span><b className="tnum">{price(p.taxLow, p.taxHigh)}</b><span className="muted">{p.fileName}</span></span>
                ))}
                {h.leadTime && <span className="hist-note">货期：{h.leadTime}</span>}
                {h.note && <span className="hist-note">备注：{h.note}</span>}
              </div>
            )}
          </td>
        </tr>
      )}
    </>
  );
}

function ImportDrawer({ table, onClose, onDone }: { table: PriceTableRow | null; onClose: () => void; onDone: () => void }) {
  const [file, setFile] = useState<{ fileKey: string; fileName: string } | null>(null);
  const [pv, setPv] = useState<PricePreview | null>(null);
  const [map, setMap] = useState<ColumnMap[]>([]);
  const [name, setName] = useState(table?.name ?? '');
  const [date, setDate] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const preview = async (f: { fileKey: string; fileName: string }, m: ColumnMap[] | null, sheet?: string) => {
    setBusy(true); setError(null);
    try {
      const r = await post<PricePreview>('/api/prices/preview', { fileKey: f.fileKey, fileName: f.fileName, tableId: table?.id ?? null, sheet: sheet ?? pv?.sheet ?? null, map: m, tableName: table ? null : name || null });
      setPv(r); setMap(r.map);
      if (!table && !name) setName(r.tableName);
      if (!date) setDate(r.suggestedDate);
    } catch (err) { setError(err instanceof ApiError ? err.message : '读取失败'); }
    finally { setBusy(false); }
  };
  const pick = async (f: File) => {
    setBusy(true); setError(null); setPv(null);
    try {
      const fd = new FormData(); fd.append('file', f);
      const r = await upload<{ fileKey: string; fileName: string; sheets: string[] }>('/api/prices/upload', fd);
      setFile(r);
      await preview(r, null);
    } catch (err) { setError(err instanceof ApiError ? err.message : '上传失败'); setBusy(false); }
  };
  const commit = async () => {
    if (!file || !pv) return;
    setBusy(true); setError(null);
    try {
      await post('/api/prices/commit', { fileKey: file.fileKey, fileName: file.fileName, tableId: table?.id ?? pv.tableId ?? null,
        tableName: table || pv.tableId ? null : name.trim(), sheet: pv.sheet, map, versionDate: date });
      onDone();
    } catch (err) { setError(err instanceof ApiError ? err.message : '导入失败'); setBusy(false); }
  };
  const changed = useMemo(() => pv != null && JSON.stringify(pv.map) !== JSON.stringify(map), [pv, map]);
  const replacing = table ?? (pv?.tableId ? { name: pv.tableName } : null);

  return (
    <aside className="acc-drawer pl-drawer" aria-label="导入价格表">
      <div className="acc-drawer-hd"><b>{table ? `上传新版 · ${table.name}` : '上传价格表'}</b><button type="button" className="refs-x" aria-label="关闭" onClick={onClose}>✕</button></div>
      <div className="acc-drawer-body sc">
        {error && <ErrorBox message={error} />}
        <label className="pl-drop">
          <input type="file" accept=".xlsx" onChange={(e) => { const f = e.target.files?.[0]; if (f) void pick(f); }} />
          <b>{file ? file.fileName : '选择 Excel 文件（.xlsx）'}</b>
          <span className="muted">{pv ? `${pv.includedSheets.join('、')} · ${pv.itemCount} 条价格` : '老的 .xls 请先用 Excel 另存为 .xlsx'}</span>
        </label>
        {busy && <Spinner text="读取中…" />}
        {pv && (
          <>
            <div className="pl-row2">
              {replacing
                ? <div className="acc-field"><span>更新哪张表</span><b>{replacing.name}</b><small className="muted">旧版本会保留，用来看价格历史</small></div>
                : <label className="acc-field"><span>价格表名称</span><input className="inp" value={name} maxLength={60} onChange={(e) => setName(e.target.value)} /></label>}
              <label className="acc-field"><span>这版价格的日期</span><input className="inp" type="date" value={date} onChange={(e) => setDate(e.target.value)} /></label>
            </div>
            {pv.sheets.length > 1 && (
              <label className="acc-field"><span>工作表</span>
                <select className="inp" value={pv.sheet} onChange={(e) => void preview(file!, null, e.target.value)}>
                  {pv.sheets.map((s) => <option key={s}>{s}</option>)}
                </select>
                {pv.includedSheets.length > 1 && <small className="muted">表头一样的工作表会一起导入：{pv.includedSheets.join('、')}</small>}
              </label>
            )}

            <div className="pl-step"><b>① 确认每一列是什么</b><span className="muted">按表头猜的；同一张表以后上传新版会记住</span></div>
            <div className="pl-map">
              {map.filter((m) => m.header || m.field !== 'ignore').map((m) => (
                <div key={m.col} className="pl-map-row">
                  <span className="pl-map-from" title={m.header}>{m.header || `第 ${m.col + 1} 列（没有表头）`}</span>
                  <span className="muted" aria-hidden="true">→</span>
                  <select className="inp" aria-label={`${m.header || `第 ${m.col + 1} 列`} 对应`} value={m.field}
                    onChange={(e) => setMap(map.map((x) => (x.col === m.col ? { ...x, field: e.target.value } : x)))}>
                    {FIELDS.map(([k, l]) => <option key={k} value={k}>{l}{m.supplier && ['price', 'price_net', 'model', 'lead'].includes(k) ? ` · ${m.supplier}` : ''}</option>)}
                  </select>
                </div>
              ))}
            </div>
            {changed && <button type="button" className="gbtn" onClick={() => void preview(file!, map)}>按新的对应关系重新读取</button>}

            {pv.diff && (
              <>
                <div className="pl-step"><b>② 和当前版本比</b></div>
                <div className="pl-diff">
                  <div><span>涨价</span><b className="d-up">{pv.diff.up}</b></div>
                  <div><span>降价</span><b className="d-down">{pv.diff.down}</b></div>
                  <div><span>新增</span><b>{pv.diff.added}</b></div>
                  <div><span>删除</span><b className="muted">{pv.diff.removed}</b></div>
                </div>
                {pv.diff.top.slice(0, 5).map((c, i) => (
                  <div key={i} className="pl-change">
                    <span>{c.name} {c.spec}{c.supplier ? ` · ${c.supplier}` : ''}</span>
                    <s className="muted tnum">{c.old != null ? fmt(c.old) : '—'}</s>
                    <b className={'tnum ' + (c.pct > 0 ? 'd-up' : 'd-down')}>{c.new != null ? fmt(c.new) : '—'}（{c.pct > 0 ? '+' : ''}{c.pct}%）</b>
                  </div>
                ))}
              </>
            )}

            <div className="pl-step"><b>{pv.diff ? '③' : '②'} 需要你看一下的{pv.issues.length ? `（${pv.issues.length} 类）` : ''}</b></div>
            {pv.issues.length === 0 && <div className="st-pill ok" style={{ alignSelf: 'flex-start' }}>没有发现问题</div>}
            {pv.issues.map((i) => (
              <div key={i.kind} className={'pl-issue ' + (i.kind === 'unit' || i.kind === 'missing' || i.kind === 'noheader' ? 'warn' : '')}>
                <b>{i.label}{i.count ? ` ${i.count} 行` : ''}</b>
                <span>{i.message}{i.rows.length ? `（第 ${i.rows.slice(0, 8).join('、')}${i.rows.length > 8 ? ' 等' : ''} 行）` : ''}</span>
              </div>
            ))}

            <div className="pl-step"><b>前几条长这样</b></div>
            <div className="dc-scroll">
              <table className="dc-table compact">
                <thead><tr><th>行</th><th>名称 · 规格</th><th>供应商</th><th className="num">含税</th><th>计价单位</th></tr></thead>
                <tbody>
                  {pv.sample.map((s, i) => (
                    <tr key={i}><td className="tnum muted">{s.rowNo}</td><td><span className="cell-main">{s.name}</span><span className="cell-sub">{s.spec}</span></td>
                      <td>{s.supplier ?? '—'}</td><td className="num tnum">{price(s.taxLow, s.taxHigh)}</td><td>{s.priceUnit ?? '—'}</td></tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </div>
      <div className="acc-drawer-f">
        <button type="button" className="gbtn" onClick={onClose}>取消</button>
        <button type="button" className="pbtn" disabled={!pv || busy || changed || !date || (!replacing && !name.trim())} onClick={() => void commit()}>
          {replacing ? '确认导入，替换为新版' : '确认导入'}
        </button>
      </div>
    </aside>
  );
}
