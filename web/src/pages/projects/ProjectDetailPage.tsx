// A3 项目详情：左侧台账字段（合同金额按角色整行消失，不打码不置灰），
// 右侧关联文档（机密文档对无权角色整条不出现、不报数量——计数会泄露存在性）。
import React from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { get, download, ApiError } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { CLS_LABEL, CLS_PILL, DELIVERY_LABEL } from '../../lib/types';
import type { ProjectDetail } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';

export default function ProjectDetailPage() {
  const { projectNo = '' } = useParams();
  const nav = useNavigate();
  // 售后没有独立的「项目查询」导航，经问答台账点行到达——面包屑如实反映来路
  const fromQa = (useLocation().state as { from?: string } | null)?.from === 'qa';
  const { profile } = useAuth();
  const confidentialOk = profile?.classifications.includes('Confidential') ?? false;

  const q = useQuery({
    queryKey: ['project', projectNo],
    queryFn: () => get<ProjectDetail>(`/api/projects/${encodeURIComponent(projectNo)}`),
    retry: (n, err) => !(err instanceof ApiError && err.status === 404) && n < 2,
  });

  if (q.isLoading) return <div style={{ padding: 40 }}><Spinner text="正在载入项目…" /></div>;
  if (q.isError || !q.data) {
    const notFound = q.error instanceof ApiError && q.error.status === 404;
    return (
      <div style={{ padding: 40, maxWidth: 560 }}>
        <ErrorBox message={notFound ? `台账里没有编号为 ${projectNo} 的项目（或不在你的可见范围内）。` : (q.error instanceof ApiError ? q.error.message : '载入失败，请稍后重试')} />
        <div style={{ marginTop: 12 }}><Link to="/projects" style={{ fontSize: 13 }}>← 返回项目查询</Link></div>
      </div>
    );
  }

  const { row, documents } = q.data;
  const status = row.deliveryStatus ? DELIVERY_LABEL[row.deliveryStatus] ?? row.deliveryStatus : null;
  const statusPill: React.CSSProperties = row.deliveryStatus === 'InProgress'
    ? { background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' }
    : { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' };

  return (
    <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minHeight: 0, minWidth: 0 }}>
      {/* 项目头 */}
      <div style={{ height: 60, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 12, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <button className="gbtn" style={{ height: 28, padding: '0 9px' }} onClick={() => nav(-1)} title="返回">←</button>
        <div>
          <div style={{ fontSize: 11.5, color: 'var(--ink-3)', marginBottom: 2 }}>
            {fromQa ? '问答 › 台账查询结果 › 项目详情' : '项目查询 › 项目详情'}
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 9 }}>
            <span className="m" style={{ fontSize: 15, fontWeight: 600 }}>{row.projectNo}</span>
            <span style={{ fontSize: 14, fontWeight: 500 }}>{row.customerName}</span>
            {status && <span className="pill" style={statusPill}>{status}</span>}
          </div>
        </div>
        <div style={{ flexGrow: 1 }} />
        <button
          className="gbtn"
          onClick={() => nav('/qa', { state: { presetCustomer: row.customerName, presetDeviceType: row.deviceType, presetYear: String(row.year) } })}
        >
          就本项目提问
        </button>
      </div>

      <div style={{ flexGrow: 1, display: 'flex', minHeight: 0 }}>
        {/* 台账字段 */}
        <div style={{ width: 520, flexShrink: 0, borderRight: '1px solid var(--line)', background: 'var(--panel)', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <div style={{ height: 38, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '0 18px', borderBottom: '1px solid var(--line)' }}>
            <span style={{ fontSize: 12, fontWeight: 600 }}>项目台账</span>
            <span className="hint">结构化字段，不经模型</span>
          </div>
          <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '4px 18px 16px' }}>
            <Kv k="项目编号"><span className="m">{row.projectNo}</span></Kv>
            <Kv k="客户名称">{row.customerName}</Kv>
            <Kv k="年份"><span className="m">{row.year}</span></Kv>
            <Kv k="设备类型">{row.deviceType}　<span style={{ fontSize: 11, color: 'var(--ink-3)' }}>受控词表</span></Kv>
            <Kv k="设备型号">{row.deviceModel ? <span className="m">{row.deviceModel}</span> : '—'}</Kv>
            <Kv k="规模参数">{row.specParams ?? '—'}</Kv>
            {row.contractAmount != null && (
              <Kv k="合同金额">
                <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <span className="m" style={{ fontWeight: 500 }}>¥ {row.contractAmount.toLocaleString()}</span>
                  <span className={CLS_PILL.Confidential}>机密</span>
                </span>
              </Kv>
            )}
            <Kv k="交付状态">{status ?? '—'}</Kv>
            <Kv k="负责人">{row.ownerName ?? '—'}</Kv>
            <Kv k="资料路径" last>关联文档 <span className="m">{documents.length}</span> 份，见右侧</Kv>

            <div style={{
              marginTop: 14, padding: '11px 12px', borderRadius: 5, fontSize: 11.5, lineHeight: 1.7,
              ...(confidentialOk
                ? { background: 'var(--bg-soft)', border: '1px solid var(--line-soft)', color: 'var(--ink-2)' }
                : { background: 'var(--cls-int-bg)', border: '1px solid var(--cls-int-line)', color: 'var(--cls-int-fg)' }),
            }}>
              {confidentialOk
                ? '合同金额为机密字段，按角色裁剪返回列。字段级密级仅适用于结构化表，文档仍以整份为单位定级。'
                : '本角色不可见合同金额，该列由服务端裁剪后根本不返回，界面上不出现占位或遮罩。'}
            </div>
          </div>
        </div>

        {/* 关联文档 */}
        <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minWidth: 0, background: 'var(--bg)' }}>
          <div style={{ height: 38, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
            <span style={{ fontSize: 12, fontWeight: 600 }}>关联文档</span>
            <span className="hint">经项目编号关联</span>
            <div style={{ flexGrow: 1 }} />
            <span className="hint">{documents.length} 份</span>
          </div>
          <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '12px 18px 16px' }}>
            {documents.map((d) => (
              <div
                key={d.docId}
                className="item"
                style={{ display: 'flex', alignItems: 'center', gap: 11, padding: '11px 13px', marginBottom: 7, background: 'var(--panel)', border: '1px solid var(--line)', borderRadius: 5, cursor: 'pointer' }}
                onClick={() => void download(`/api/files/${d.docId}`, d.title).catch((err) => alert(err instanceof ApiError ? err.message : '下载失败'))}
                title="下载原件"
              >
                <div style={{ flexGrow: 1, minWidth: 0 }}>
                  <div style={{ fontSize: 12.5, fontWeight: 500, lineHeight: 1.45 }}>{d.title}</div>
                  <div style={{ fontSize: 11, color: 'var(--ink-3)', marginTop: 3 }}>{d.uploadedAt.slice(0, 10)}</div>
                </div>
                <span className="pill pill-neutral">{d.docCategory}</span>
                <span className={CLS_PILL[d.classification]}>{CLS_LABEL[d.classification]}</span>
              </div>
            ))}
            {documents.length === 0 && (
              <div className="hint" style={{ padding: '20px 4px' }}>该项目在你的可见范围内没有关联文档。</div>
            )}
            <div style={{
              marginTop: 6, padding: '11px 13px', borderRadius: 5, fontSize: 11.5, lineHeight: 1.7,
              ...(confidentialOk
                ? { background: 'var(--bg-soft)', border: '1px solid var(--line-soft)', color: 'var(--ink-2)' }
                : { background: 'var(--cls-int-bg)', border: '1px solid var(--cls-int-line)', color: 'var(--cls-int-fg)' }),
            }}>
              {confidentialOk
                ? '文档清单按你的可访问密级过滤后返回。机密文档对无机密权限的角色整条不出现。'
                : '清单按你的可访问密级过滤后返回。超出密级的文档整条不渲染，也不显示「另有几份不可见」。'}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function Kv({ k, last, children }: { k: string; last?: boolean; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'flex-start', gap: 12, padding: '10px 0', borderBottom: last ? 'none' : '1px solid var(--line-soft)' }}>
      <div style={{ width: 84, flexShrink: 0, fontSize: 11.5, color: 'var(--ink-3)', lineHeight: 1.6 }}>{k}</div>
      <div style={{ flexGrow: 1, minWidth: 0, fontSize: 12.5, lineHeight: 1.6 }}>{children}</div>
    </div>
  );
}
