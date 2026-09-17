// 历史项目的档案预览：左边是台账字段，右边是这个项目的原件本身。
// 拿它当基准之前总得先看一眼「这单当时是怎么做的」——光有编号和型号决定不了。
import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { get } from '../../lib/api';
import { DELIVERY_LABEL } from '../../lib/types';
import type { ProjectDetail } from '../../lib/types';
import { ClsBadge, ErrorBox, Spinner } from '../../components/Common';
import { FileView } from '../../components/FileView';

export function ProjectPreviewModal({ projectNo, onClose, onPick }: {
  projectNo: string;
  onClose: () => void;
  /** 看完觉得合适，直接拿它当基准 */
  onPick?: (projectNo: string) => void;
}) {
  const nav = useNavigate();
  const q = useQuery({
    queryKey: ['project-detail', projectNo],
    queryFn: () => get<ProjectDetail>(`/api/projects/${encodeURIComponent(projectNo)}`),
  });
  const [docId, setDocId] = useState<string | null>(null);
  // 默认打开第一份资料，省一次点击
  useEffect(() => {
    if (!docId && q.data?.documents.length) setDocId(q.data.documents[0].docId);
  }, [q.data, docId]);

  useEffect(() => {
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', esc);
    return () => window.removeEventListener('keydown', esc);
  }, [onClose]);

  const r = q.data?.row;
  const open = q.data?.documents.find((d) => d.docId === docId);

  return (
    <div className="modal-mask" onClick={onClose}>
      <div className="modal-box pv" onClick={(e) => e.stopPropagation()}>
        <div className="pv-hd">
          <span className="m" style={{ fontSize: 13, fontWeight: 600, color: 'var(--accent)' }}>{projectNo}</span>
          {r && <span style={{ fontSize: 13 }}>{r.customerName}</span>}
          <div style={{ flexGrow: 1 }} />
          {onPick && <button className="pbtn" style={{ height: 26, fontSize: 12, padding: '0 12px' }}
            onClick={() => { onPick(projectNo); onClose(); }}>用这个做基准</button>}
          <button className="gbtn" style={{ height: 26, fontSize: 12 }}
            onClick={() => nav(`/projects/${encodeURIComponent(projectNo)}`, { state: { from: 'chat' } })}>
            打开完整档案
          </button>
          <button className="gbtn" style={{ height: 26, fontSize: 12 }} onClick={onClose}>关闭</button>
        </div>

        <div className="pv-body">
          <div className="pv-side sc">
            {q.isPending && <div style={{ padding: 12 }}><Spinner text="取项目档案…" /></div>}
            {q.isError && <div style={{ padding: 12 }}><ErrorBox message="这个项目的档案取不到" /></div>}
            {r && (
              <>
                <div className="sum-sec">台账</div>
                {([['年份', String(r.year)], ['设备类型', r.deviceType], ['设备型号', r.deviceModel ?? '—'],
                   ['规格参数', r.specParams ?? '—'],
                   ['交付状态', r.deliveryStatus ? (DELIVERY_LABEL[r.deliveryStatus] ?? r.deliveryStatus) : '—'],
                   ['负责人', r.ownerName ?? '—']] as [string, string][]).map(([k, v]) => (
                  <div key={k} className="sum-row"><span className="sum-name">{k}</span><span className="sum-val">{v}</span></div>
                ))}
                <div className="sum-sec">
                  项目资料<span className="sum-cnt">{q.data!.documents.length}</span>
                </div>
                {q.data!.documents.length === 0 && (
                  <div className="hint" style={{ padding: '8px 11px' }}>这个项目下还没有归档资料。</div>
                )}
                {q.data!.documents.map((d) => (
                  <button key={d.docId} type="button"
                    className={`pv-doc${d.docId === docId ? ' on' : ''}`}
                    onClick={() => setDocId(d.docId)}>
                    <span className="pv-doc-t">{d.title}</span>
                    <span className="pv-doc-m"><ClsBadge cls={d.classification} /> {d.docCategory}</span>
                  </button>
                ))}
              </>
            )}
          </div>
          <div className="pv-main sc">
            {open ? <FileView docId={open.docId} fileName={open.title} />
              : <div className="hint" style={{ padding: 16 }}>左边选一份资料就能看原件。</div>}
          </div>
        </div>
      </div>
    </div>
  );
}
