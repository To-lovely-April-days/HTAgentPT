// A2 项目查询（台账）：结构化查询，不经模型。合同金额为字段级机密——
// 列与筛选项都跟着权限走：无机密权限时两者都不渲染（只藏列不藏筛选等于留了反推通道）。
import React, { useCallback, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { get, post, ApiError } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { DELIVERY_LABEL } from '../../lib/types';
import type { ProjectSearchResult, VocabRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';

interface Filters {
  customerName: string; year: string; deviceType: string; deliveryStatus: string;
  amountMinWan: string; amountMaxWan: string;
}
const EMPTY: Filters = { customerName: '', year: '', deviceType: '', deliveryStatus: '', amountMinWan: '', amountMaxWan: '' };

const STATUS_PILL: Record<string, React.CSSProperties> = {
  Delivered: { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' },
  Closed: { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' },
  InProgress: { background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' },
};

export default function ProjectsPage() {
  const nav = useNavigate();
  const { profile } = useAuth();
  // 与服务端同一条判定：金额可见 = 可访问机密密级。前端只决定要不要画筛选项，裁剪本身在服务层。
  const amountAllowed = profile?.classifications.includes('Confidential') ?? false;

  const [filters, setFilters] = useState<Filters>(EMPTY);
  const [applied, setApplied] = useState<Filters | null>(EMPTY); // null = 尚未查询
  const [result, setResult] = useState<ProjectSearchResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const devices = useQuery({ queryKey: ['vocab', 'device_type'], queryFn: () => get<VocabRow[]>('/api/vocab/device_type'), staleTime: 60_000 });

  const run = useCallback(async (f: Filters) => {
    setBusy(true);
    setError(null);
    try {
      const body = {
        customerName: f.customerName.trim() || null,
        yearFrom: f.year ? Number(f.year) : null,
        yearTo: f.year ? Number(f.year) : null,
        deviceType: f.deviceType || null,
        deliveryStatus: f.deliveryStatus || null,
        amountMin: amountAllowed && f.amountMinWan ? Number(f.amountMinWan) * 10000 : null,
        amountMax: amountAllowed && f.amountMaxWan ? Number(f.amountMaxWan) * 10000 : null,
      };
      setResult(await post<ProjectSearchResult>('/api/projects/search', body));
      setApplied(f);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '查询失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  }, [amountAllowed]);

  useEffect(() => { void run(EMPTY); /* 初始进入即列最近记录 */ }, [run]);

  const removeCondition = (key: keyof Filters, extra?: keyof Filters) => {
    const next = { ...(applied ?? filters), [key]: '' } as Filters;
    if (extra) next[extra] = '';
    setFilters(next);
    void run(next);
  };

  const appliedChips = applied
    ? ([
        applied.customerName && { label: `客户 = ${applied.customerName}`, key: 'customerName' as const },
        applied.year && { label: `年份 = ${applied.year}`, key: 'year' as const },
        applied.deviceType && { label: `设备类型 = ${applied.deviceType}`, key: 'deviceType' as const },
        applied.deliveryStatus && { label: `交付状态 = ${DELIVERY_LABEL[applied.deliveryStatus]}`, key: 'deliveryStatus' as const },
        (applied.amountMinWan || applied.amountMaxWan) && {
          label: `金额 ${applied.amountMinWan || '不限'}—${applied.amountMaxWan || '不限'} 万`,
          key: 'amountMinWan' as const, extra: 'amountMaxWan' as const,
        },
      ].filter(Boolean) as { label: string; key: keyof Filters; extra?: keyof Filters }[])
    : [];

  const total = result && result.amountVisible
    ? result.rows.reduce((s, r) => s + (r.contractAmount ?? 0), 0)
    : null;

  const exportCsv = () => {
    if (!result) return;
    const head = ['项目编号', '客户名称', '年份', '设备类型', '设备型号', '规模参数', '交付状态',
      ...(result.amountVisible ? ['合同金额'] : []), '负责人'];
    // 防公式注入：以 =+-@ 开头的单元格前置单引号
    const esc = (v: string | number | null | undefined) => {
      let s = v == null ? '' : String(v);
      if (/^[=+\-@]/.test(s)) s = `'${s}`;
      return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
    };
    const lines = [head.map(esc).join(',')];
    for (const r of result.rows) {
      lines.push([r.projectNo, r.customerName, r.year, r.deviceType, r.deviceModel ?? '', r.specParams ?? '',
        r.deliveryStatus ? DELIVERY_LABEL[r.deliveryStatus] ?? r.deliveryStatus : '',
        ...(result.amountVisible ? [r.contractAmount ?? ''] : []), r.ownerName ?? ''].map(esc).join(','));
    }
    const url = URL.createObjectURL(new Blob(['﻿' + lines.join('\n')], { type: 'text/csv;charset=utf-8' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = `项目台账-${new Date().toISOString().slice(0, 10)}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const fl: React.CSSProperties = { fontSize: 11.5, color: 'var(--ink-3)', marginBottom: 5 };
  const fi: React.CSSProperties = { height: 28, width: '100%', padding: '0 9px', background: 'var(--panel)', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };

  return (
    <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minHeight: 0, minWidth: 0 }}>
      {/* 筛选 */}
      <div style={{ flexShrink: 0, background: 'var(--panel)', borderBottom: '1px solid var(--line)', padding: '14px 18px 12px' }}>
        <form
          onSubmit={(e) => { e.preventDefault(); void run(filters); }}
          style={{ display: 'flex', gap: 12, marginBottom: 11, alignItems: 'flex-end', flexWrap: 'wrap' }}
        >
          <div style={{ width: 200 }}>
            <div style={fl}>客户名称</div>
            <input style={fi} value={filters.customerName} onChange={(e) => setFilters({ ...filters, customerName: e.target.value })} placeholder="模糊匹配" />
          </div>
          <div style={{ width: 100 }}>
            <div style={fl}>年份</div>
            <input style={fi} className="m" value={filters.year} inputMode="numeric" onChange={(e) => setFilters({ ...filters, year: e.target.value.replace(/\D/g, '').slice(0, 4) })} placeholder="全部" />
          </div>
          <div style={{ width: 150 }}>
            <div style={fl}>设备类型　<span style={{ color: 'var(--accent)' }}>受控词表</span></div>
            <select style={fi} value={filters.deviceType} onChange={(e) => setFilters({ ...filters, deviceType: e.target.value })}>
              <option value="">全部</option>
              {(devices.data ?? []).map((v) => <option key={v.id} value={v.value}>{v.value}</option>)}
            </select>
          </div>
          <div style={{ width: 120 }}>
            <div style={fl}>交付状态</div>
            <select style={fi} value={filters.deliveryStatus} onChange={(e) => setFilters({ ...filters, deliveryStatus: e.target.value })}>
              <option value="">全部</option>
              {Object.entries(DELIVERY_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </select>
          </div>
          {amountAllowed && (
            <div style={{ width: 220 }}>
              <div style={fl}>合同金额区间　<span className="pill pill-conf" style={{ height: 16, padding: '0 5px', fontSize: 10 }}>机密</span></div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                <input style={{ ...fi, width: 0, flexGrow: 1 }} className="m" inputMode="numeric" placeholder="不限"
                  value={filters.amountMinWan} onChange={(e) => setFilters({ ...filters, amountMinWan: e.target.value.replace(/[^\d.]/g, '') })} />
                <span style={{ fontSize: 12, color: 'var(--ink-3)' }}>—</span>
                <input style={{ ...fi, width: 0, flexGrow: 1 }} className="m" inputMode="numeric" placeholder="不限"
                  value={filters.amountMaxWan} onChange={(e) => setFilters({ ...filters, amountMaxWan: e.target.value.replace(/[^\d.]/g, '') })} />
                <span style={{ fontSize: 11.5, color: 'var(--ink-3)' }}>万</span>
              </div>
            </div>
          )}
          <div style={{ flexGrow: 1 }} />
          <div style={{ display: 'flex', gap: 8 }}>
            <button type="button" className="gbtn" style={{ height: 28 }} onClick={() => { setFilters(EMPTY); void run(EMPTY); }}>重置</button>
            <button type="submit" className="pbtn" style={{ height: 28, padding: '0 15px' }} disabled={busy}>查询</button>
          </div>
        </form>
        <div className="hint">
          结构化查询，条件为「与」关系，结果直接来自台账，不经模型生成
          {amountAllowed && '　·　合同金额为机密字段，无机密权限的账号查询同一台账时该列与该筛选项均不出现'}
        </div>
      </div>

      {/* 结果 */}
      {error && <div style={{ padding: 16 }}><ErrorBox message={error} /></div>}
      {!error && !result && <div style={{ padding: 40 }}><Spinner text="正在查询…" /></div>}
      {!error && result && result.rows.length > 0 && (
        <>
          <div style={{ height: 40, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 12, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
            <span style={{ fontSize: 12.5 }}>命中 <span className="m" style={{ fontWeight: 500 }}>{result.rows.length}</span> 个项目</span>
            <span className="hint">按年份倒序{result.rows.length >= 200 ? '，仅显示前 200 条，请加筛选条件收窄' : ''}</span>
            <div style={{ flexGrow: 1 }} />
            {total != null && <span className="hint">合计 <span className="m">¥ {total.toLocaleString()}</span></span>}
            <button className="gbtn" style={{ height: 26 }} onClick={exportCsv}>导出</button>
          </div>
          <div className="sc" style={{ flexGrow: 1, minHeight: 0, background: 'var(--panel)' }}>
            <table style={{ width: '100%', borderCollapse: 'collapse' }}>
              <thead>
                <tr>
                  {['项目编号', '客户名称', '年份', '设备类型', '设备型号', '规模参数', '交付状态',
                    ...(result.amountVisible ? ['合同金额'] : []), '负责人'].map((h) => (
                    <th key={h} style={{ ...th, textAlign: h === '合同金额' ? 'right' : 'left' }}>{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {result.rows.map((r) => (
                  <tr key={r.projectNo} className="row-click" style={{ cursor: 'pointer' }} onClick={() => nav(`/projects/${encodeURIComponent(r.projectNo)}`)}>
                    <td className="m" style={{ ...td, color: 'var(--accent)', fontWeight: 500 }}>{r.projectNo}</td>
                    <td style={td}>{r.customerName}</td>
                    <td className="m" style={td}>{r.year}</td>
                    <td style={td}>{r.deviceType}</td>
                    <td className="m" style={td}>{r.deviceModel ?? '—'}</td>
                    <td style={{ ...td, color: 'var(--ink-2)' }}>{r.specParams ?? '—'}</td>
                    <td style={td}>
                      {r.deliveryStatus
                        ? <span className="pill" style={STATUS_PILL[r.deliveryStatus]}>{DELIVERY_LABEL[r.deliveryStatus] ?? r.deliveryStatus}</span>
                        : '—'}
                    </td>
                    {result.amountVisible && (
                      <td className="m" style={{ ...td, textAlign: 'right' }}>{r.contractAmount != null ? `¥ ${r.contractAmount.toLocaleString()}` : '—'}</td>
                    )}
                    <td style={td}>{r.ownerName ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div style={{ height: 44, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 10, padding: '0 18px', background: 'var(--panel)', borderTop: '1px solid var(--line)' }}>
            {result.amountFilterIgnored && <span style={{ fontSize: 11.5, color: 'var(--cls-int-fg)' }}>金额筛选条件已被忽略（当前角色不可按金额筛选）</span>}
            <div style={{ flexGrow: 1 }} />
            <span className="hint">点击任一行查看项目详情与关联文档</span>
          </div>
        </>
      )}
      {/* 无结果：字段精确匹配没命中——与问答的阈值拦截不是一回事，没有「可能相关」 */}
      {!error && result && result.rows.length === 0 && (
        <div style={{ flexGrow: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 40 }}>
          <div className="card" style={{ width: 540, padding: '24px 26px 20px' }}>
            <div style={{ fontSize: 15, fontWeight: 600, lineHeight: 1.45, marginBottom: 7 }}>没有符合条件的项目</div>
            <div style={{ fontSize: 13, lineHeight: 1.75, color: 'var(--ink-2)', marginBottom: 16 }}>
              台账查询是按字段精确匹配，不存在相关度与阈值——没有命中就是台账里确实没有这样的记录，不是「可能相关但分数不够」。
            </div>
            {appliedChips.length > 0 && (
              <div style={{ padding: '12px 13px', background: 'var(--bg-soft)', border: '1px solid var(--line-soft)', borderRadius: 5, marginBottom: 14 }}>
                <div style={{ fontSize: 11.5, color: 'var(--ink-3)', marginBottom: 8 }}>当前条件（点 ✕ 去掉后重查）</div>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                  {appliedChips.map((c) => (
                    <button key={c.key} className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => removeCondition(c.key, c.extra)}>
                      {c.label}　✕
                    </button>
                  ))}
                </div>
              </div>
            )}
            <div style={{ fontSize: 12, lineHeight: 1.7, color: 'var(--ink-2)' }}>
              {appliedChips.length > 0 ? '可以去掉某个条件重试。' : ''}若确认该项目存在但查不到，多半是台账尚未登记——联系管理员补录。
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

const th: React.CSSProperties = {
  fontSize: 11.5, fontWeight: 500, color: 'var(--ink-2)', padding: '0 12px', height: 36,
  background: 'var(--bg-soft)', borderBottom: '1px solid var(--line)', whiteSpace: 'nowrap',
  position: 'sticky', top: 0,
};
const td: React.CSSProperties = { fontSize: 12.5, padding: '0 12px', height: 40, borderBottom: '1px solid var(--line-soft)', whiteSpace: 'nowrap' };
