// E8 受控词表 + E10 术语表 + E9 项目台账（含批量修正）。
// 受控字段只能选不能写是入库三道闸的第 3 条；E9 批量修正是在给它的历史欠账擦屁股
// （FR-3.6：历史资料归集阶段的集中整理）。术语表有两个下游：翻译强制对照 + 检索改写扩召回。
import React, { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put, del, ApiError } from '../../lib/api';
import { DELIVERY_LABEL } from '../../lib/types';
import type { ProjectSearchResult, TermRow, VocabRow } from '../../lib/types';
import { ErrorBox, InfoBox, Spinner } from '../../components/Common';

const VOCAB_KEYS: [string, string][] = [
  ['device_type', '设备类型'], ['doc_category', '文档类别'],
  ['customer_name', '客户名称'], ['term_domain', '术语领域'],
];

export default function MetaPage() {
  const [tab, setTab] = useState<'vocab' | 'terms' | 'ledger'>('vocab');
  const tabStyle = (on: boolean): React.CSSProperties => ({
    height: 40, padding: '0 13px', display: 'inline-flex', alignItems: 'center', fontSize: 12.5, cursor: 'pointer',
    borderBottom: on ? '2px solid var(--accent)' : '2px solid transparent',
    color: on ? 'var(--accent)' : 'var(--ink-2)', fontWeight: on ? 600 : 400,
  });
  return (
    <>
      <div style={{ flexShrink: 0, display: 'flex', gap: 2, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <span style={tabStyle(tab === 'vocab')} onClick={() => setTab('vocab')}>受控词表</span>
        <span style={tabStyle(tab === 'terms')} onClick={() => setTab('terms')}>术语表</span>
        <span style={tabStyle(tab === 'ledger')} onClick={() => setTab('ledger')}>项目台账</span>
      </div>
      {tab === 'vocab' && <VocabTab />}
      {tab === 'terms' && <TermsTab />}
      {tab === 'ledger' && <LedgerTab />}
    </>
  );
}

const fi: React.CSSProperties = { height: 28, padding: '0 9px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };

// ─── E8 ────────────────────────────────────────────────
function VocabTab() {
  const qc = useQueryClient();
  const [key, setKey] = useState('device_type');
  const [value, setValue] = useState('');
  const [aliases, setAliases] = useState('');
  const [error, setError] = useState<string | null>(null);
  const rows = useQuery({ queryKey: ['vocab', key], queryFn: () => get<VocabRow[]>(`/api/vocab/${key}`) });

  const add = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await post(`/api/vocab/${key}`, { value: value.trim(), aliases: aliases.trim() || null });
      setValue('');
      setAliases('');
      void qc.invalidateQueries({ queryKey: ['vocab', key] });
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '新增失败');
    }
  };
  const remove = async (id: string) => {
    setError(null);
    try {
      await del(`/api/vocab/items/${id}`);
      void qc.invalidateQueries({ queryKey: ['vocab', key] });
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '删除失败');
    }
  };

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
      <div style={{ maxWidth: 680 }}>
        <div className="hint" style={{ lineHeight: 1.7, marginBottom: 12 }}>
          受控字段在上传登记时只能选不能写（入库三道闸第 3 条）——不设这道闸，半年后同一种设备五六种写法，筛选与元数据对齐全失效。词表外取值不参与筛选。
        </div>
        <div style={{ display: 'flex', gap: 6, marginBottom: 14 }}>
          {VOCAB_KEYS.map(([k, label]) => (
            <button key={k} className={key === k ? 'pbtn' : 'gbtn'} style={{ height: 26, fontSize: 12, padding: '0 11px' }} onClick={() => setKey(k)}>{label}</button>
          ))}
        </div>
        {error && <div style={{ marginBottom: 10 }}><ErrorBox message={error} /></div>}
        {rows.isLoading && <Spinner text="载入…" />}
        {(rows.data ?? []).map((v) => (
          <div key={v.id} style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '7px 0', borderBottom: '1px solid var(--line-soft)' }}>
            <span style={{ fontSize: 12.5, fontWeight: 500, width: 180 }}>{v.value}</span>
            <span className="hint" style={{ flexGrow: 1 }}>{v.aliases ? `别名：${v.aliases}` : ''}</span>
            <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => void remove(v.id)}>删除</button>
          </div>
        ))}
        <form onSubmit={add} style={{ display: 'flex', gap: 8, marginTop: 14 }}>
          <input style={{ ...fi, width: 180 }} placeholder="新增取值" value={value} onChange={(e) => setValue(e.target.value)} />
          <input style={{ ...fi, flexGrow: 1 }} placeholder="别名（可选，检索改写时扩召回；多个用、分隔）" value={aliases} onChange={(e) => setAliases(e.target.value)} />
          <button type="submit" className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={!value.trim()}>新增</button>
        </form>
      </div>
    </div>
  );
}

// ─── E10 ───────────────────────────────────────────────
function TermsTab() {
  const qc = useQueryClient();
  const [status, setStatus] = useState('');
  const [zh, setZh] = useState('');
  const [en, setEn] = useState('');
  const [domain, setDomain] = useState('');
  const [error, setError] = useState<string | null>(null);
  const rows = useQuery({ queryKey: ['terms-admin', status], queryFn: () => get<TermRow[]>(`/api/terms${status ? `?status=${status}` : ''}`) });

  const refresh = () => void qc.invalidateQueries({ queryKey: ['terms-admin'] });
  const act = async (fn: () => Promise<unknown>) => {
    setError(null);
    try {
      await fn();
      refresh();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '操作失败');
    }
  };

  const STATUS_PILL: Record<string, React.CSSProperties> = {
    Approved: { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' },
    Pending: { background: 'var(--cls-int-bg)', color: 'var(--cls-int-fg)', border: '1px solid var(--cls-int-line)' },
    Rejected: { background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' },
  };
  const STATUS_LABEL: Record<string, string> = { Approved: '已审定', Pending: '待确认', Rejected: '已驳回' };

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
      <div style={{ maxWidth: 720 }}>
        <div className="hint" style={{ lineHeight: 1.7, marginBottom: 12 }}>
          改一条术语会同时影响两处：翻译以强制对照注入保证全文译名一致（FR-6.1）；问答把命中的同义表述注入查询改写扩充召回（FR-4.2）。删同义表述前想想检索那头。
        </div>
        <div style={{ display: 'flex', gap: 6, marginBottom: 12 }}>
          {[['', '全部'], ['Pending', '待确认'], ['Approved', '已审定'], ['Rejected', '已驳回']].map(([v, label]) => (
            <button key={v} className={status === v ? 'pbtn' : 'gbtn'} style={{ height: 26, fontSize: 12, padding: '0 11px' }} onClick={() => setStatus(v)}>{label}</button>
          ))}
        </div>
        {error && <div style={{ marginBottom: 10 }}><ErrorBox message={error} /></div>}
        {rows.isLoading && <Spinner text="载入…" />}
        {(rows.data ?? []).map((t) => (
          <div key={t.id} style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '8px 0', borderBottom: '1px solid var(--line-soft)' }}>
            <span style={{ fontSize: 12.5, fontWeight: 500, width: 140 }}>{t.zh}</span>
            <span className="m" style={{ fontSize: 12, color: 'var(--ink-2)', width: 200 }}>{t.en}</span>
            <span className="pill" style={{ background: 'var(--bg-soft)', color: 'var(--ink-3)', border: '1px solid var(--line-soft)' }}>{t.domain}</span>
            <span className="pill" style={STATUS_PILL[t.status] ?? STATUS_PILL.Pending}>{STATUS_LABEL[t.status] ?? t.status}</span>
            <span className="hint" style={{ flexGrow: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{t.note ?? ''}</span>
            {t.status === 'Pending' && (
              <>
                <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => void act(() => post(`/api/terms/${t.id}/approve`))}>确认</button>
                <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => void act(() => post(`/api/terms/${t.id}/reject`))}>驳回</button>
              </>
            )}
          </div>
        ))}
        <form onSubmit={(e) => { e.preventDefault(); void act(() => post('/api/terms', { domain: domain.trim() || '通用', zh: zh.trim(), en: en.trim(), note: null })).then(() => { setZh(''); setEn(''); }); }}
          style={{ display: 'flex', gap: 8, marginTop: 14 }}>
          <input style={{ ...fi, width: 140 }} placeholder="中文" value={zh} onChange={(e) => setZh(e.target.value)} />
          <input style={{ ...fi, width: 200 }} className="m" placeholder="English" value={en} onChange={(e) => setEn(e.target.value)} />
          <input style={{ ...fi, width: 120 }} placeholder="领域" value={domain} onChange={(e) => setDomain(e.target.value)} />
          <button type="submit" className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={!zh.trim() || !en.trim()}>直接新增（即时生效）</button>
        </form>
        <div className="hint" style={{ marginTop: 8 }}>翻译中提交的修正建议出现在「待确认」里；确认后从下一次翻译起生效。</div>
      </div>
    </div>
  );
}

// ─── E9 ────────────────────────────────────────────────
function LedgerTab() {
  const qc = useQueryClient();
  const [showBatch, setShowBatch] = useState(false);
  const [editNo, setEditNo] = useState<string | null>(null); // null=不编辑, ''=新建
  const [error, setError] = useState<string | null>(null);
  const rows = useQuery({
    queryKey: ['admin-projects'],
    queryFn: () => post<ProjectSearchResult>('/api/projects/search', { limit: 500 }),
  });
  const devices = useQuery({ queryKey: ['vocab', 'device_type'], queryFn: () => get<VocabRow[]>('/api/vocab/device_type'), staleTime: 60_000 });
  const refresh = () => void qc.invalidateQueries({ queryKey: ['admin-projects'] });

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
      <div style={{ maxWidth: 860 }}>
        <div className="hint" style={{ lineHeight: 1.7, marginBottom: 12 }}>
          台账与文档经项目编号关联（FR-3.3）；上传时项目编号必须在台账已登记。批量修正用于历史资料归集阶段的集中整理（FR-3.6）——早期导入的自由文本设备类型不在受控词表内，既筛不出也对不齐，是归集阶段的主要工作量。
        </div>
        <div style={{ display: 'flex', gap: 8, marginBottom: 12 }}>
          <button className="pbtn" style={{ height: 28, padding: '0 13px' }} onClick={() => setEditNo('')}>登记新项目</button>
          <button className="gbtn" style={{ height: 28 }} onClick={() => setShowBatch(!showBatch)}>批量修正元数据</button>
        </div>
        {error && <div style={{ marginBottom: 10 }}><ErrorBox message={error} /></div>}

        {showBatch && <BatchFixPanel deviceOptions={(devices.data ?? []).map((d) => d.value)} onDone={() => { setShowBatch(false); refresh(); }} />}
        {editNo !== null && (
          <ProjectEditPanel
            projectNo={editNo}
            existing={editNo ? rows.data?.rows.find((r) => r.projectNo === editNo) : undefined}
            deviceOptions={(devices.data ?? []).map((d) => d.value)}
            onDone={() => { setEditNo(null); refresh(); }}
            onError={setError}
          />
        )}

        {rows.isLoading && <Spinner text="载入台账…" />}
        {(rows.data?.rows ?? []).map((r) => (
          <div key={r.projectNo} style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '7px 0', borderBottom: '1px solid var(--line-soft)', fontSize: 12.5 }}>
            <span className="m" style={{ width: 110, fontWeight: 500 }}>{r.projectNo}</span>
            <span style={{ width: 130 }}>{r.customerName}</span>
            <span className="m" style={{ width: 48 }}>{r.year}</span>
            <span style={{ width: 110 }}>{r.deviceType}</span>
            <span className="m hint" style={{ width: 90 }}>{r.deviceModel ?? '—'}</span>
            <span className="hint" style={{ flexGrow: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.specParams ?? ''}</span>
            <span style={{ width: 62 }}>{r.deliveryStatus ? DELIVERY_LABEL[r.deliveryStatus] : '—'}</span>
            <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => setEditNo(r.projectNo)}>编辑</button>
          </div>
        ))}
      </div>
    </div>
  );
}

function ProjectEditPanel({ projectNo, existing, deviceOptions, onDone, onError }: {
  projectNo: string;
  existing?: { customerName: string; year: number; deviceType: string; deviceModel: string | null; specParams: string | null; contractAmount: number | null; deliveryStatus: string | null };
  deviceOptions: string[];
  onDone: () => void;
  onError: (m: string | null) => void;
}) {
  const isNew = projectNo === '';
  const [no, setNo] = useState(projectNo);
  const [customer, setCustomer] = useState(existing?.customerName ?? '');
  const [year, setYear] = useState(String(existing?.year ?? new Date().getFullYear()));
  const [deviceType, setDeviceType] = useState(existing?.deviceType ?? '');
  const [model, setModel] = useState(existing?.deviceModel ?? '');
  const [spec, setSpec] = useState(existing?.specParams ?? '');
  const [amount, setAmount] = useState(existing?.contractAmount != null ? String(existing.contractAmount) : '');
  const [status, setStatus] = useState(existing?.deliveryStatus ?? '');
  const [busy, setBusy] = useState(false);

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    onError(null);
    try {
      const body = {
        projectNo: no.trim(), customerName: customer.trim(), year: Number(year), deviceType,
        deviceModel: model.trim() || null, specParams: spec.trim() || null,
        contractAmount: amount.trim() ? Number(amount) : null,
        deliveryStatus: status || null, ownerId: null, docPath: null,
      };
      if (isNew) await post('/api/projects', body);
      else await put(`/api/projects/${encodeURIComponent(projectNo)}`, body);
      onDone();
    } catch (err) {
      onError(err instanceof ApiError ? err.message : '保存失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={save} className="card" style={{ padding: '12px 14px', marginBottom: 14 }}>
      <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 10 }}>{isNew ? '登记新项目' : `编辑 ${projectNo}`}</div>
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginBottom: 10 }}>
        <input style={{ ...fi, width: 120 }} className="m" placeholder="项目编号" value={no} disabled={!isNew} onChange={(e) => setNo(e.target.value)} />
        <input style={{ ...fi, width: 130 }} placeholder="客户名称" value={customer} onChange={(e) => setCustomer(e.target.value)} />
        <input style={{ ...fi, width: 70 }} className="m" placeholder="年份" value={year} onChange={(e) => setYear(e.target.value.replace(/\D/g, '').slice(0, 4))} />
        <select style={fi} value={deviceType} onChange={(e) => setDeviceType(e.target.value)}>
          <option value="">设备类型（受控）</option>
          {deviceOptions.map((d) => <option key={d}>{d}</option>)}
        </select>
        <input style={{ ...fi, width: 100 }} className="m" placeholder="型号" value={model} onChange={(e) => setModel(e.target.value)} />
        <input style={{ ...fi, width: 170 }} placeholder="规模参数" value={spec} onChange={(e) => setSpec(e.target.value)} />
        <input style={{ ...fi, width: 110 }} className="m" placeholder="合同金额（元）" value={amount} onChange={(e) => setAmount(e.target.value.replace(/[^\d.]/g, ''))} />
        <select style={fi} value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">交付状态</option>
          {Object.entries(DELIVERY_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </select>
      </div>
      <div style={{ display: 'flex', gap: 8 }}>
        <button type="submit" className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={busy || !no.trim() || !customer.trim() || !deviceType}>保存</button>
        <button type="button" className="gbtn" onClick={onDone}>取消</button>
      </div>
    </form>
  );
}

function BatchFixPanel({ deviceOptions, onDone }: { deviceOptions: string[]; onDone: () => void }) {
  const [fCustomer, setFCustomer] = useState('');
  const [fCategory, setFCategory] = useState('');
  const [fYear, setFYear] = useState('');
  const [sDevice, setSDevice] = useState('');
  const [sCategory, setSCategory] = useState('');
  const [sProject, setSProject] = useState('');
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await post<{ affected: number }>('/api/documents/metadata/batch', {
        filter: { customerName: fCustomer.trim() || null, docCategory: fCategory.trim() || null, year: fYear ? Number(fYear) : null },
        set: { deviceType: sDevice || null, docCategory: sCategory.trim() || null, projectNo: sProject.trim() || null },
      });
      setResult(r.affected);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '批量修正失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card" style={{ padding: '12px 14px', marginBottom: 14, background: 'var(--bg-soft)' }}>
      <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 8 }}>批量修正文档元数据（新值限受控词表取值）</div>
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center', marginBottom: 8 }}>
        <span className="hint">筛选：</span>
        <input style={{ ...fi, width: 120 }} placeholder="客户" value={fCustomer} onChange={(e) => setFCustomer(e.target.value)} />
        <input style={{ ...fi, width: 100 }} placeholder="文档类别" value={fCategory} onChange={(e) => setFCategory(e.target.value)} />
        <input style={{ ...fi, width: 70 }} className="m" placeholder="年份" value={fYear} onChange={(e) => setFYear(e.target.value.replace(/\D/g, '').slice(0, 4))} />
        <span className="hint">改为：</span>
        <select style={fi} value={sDevice} onChange={(e) => setSDevice(e.target.value)}>
          <option value="">设备类型不改</option>
          {deviceOptions.map((d) => <option key={d}>{d}</option>)}
        </select>
        <input style={{ ...fi, width: 100 }} placeholder="文档类别" value={sCategory} onChange={(e) => setSCategory(e.target.value)} />
        <input style={{ ...fi, width: 110 }} className="m" placeholder="项目编号" value={sProject} onChange={(e) => setSProject(e.target.value)} />
      </div>
      {error && <div style={{ marginBottom: 8 }}><ErrorBox message={error} /></div>}
      {result != null && <div style={{ marginBottom: 8 }}><InfoBox>已修正 {result} 份文档的元数据。</InfoBox></div>}
      <div style={{ display: 'flex', gap: 8 }}>
        <button className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={busy || (!sDevice && !sCategory.trim() && !sProject.trim())} onClick={() => void run()}>执行修正</button>
        <button className="gbtn" onClick={onDone}>收起</button>
      </div>
    </div>
  );
}
