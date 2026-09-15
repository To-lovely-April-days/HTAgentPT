// E2 上传与元数据登记。入库链路上的三道闸（都在入库时把关，事后补难得多）：
// ① 必须选目标知识库与密级（FR-1.1）② 必填元数据未填齐不允许提交（FR-3.1）
// ③ 受控字段只能选不能写（FR-3.2）。项目编号按表 4-5 条件必填。
import React, { useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get, upload, ApiError } from '../../lib/api';
import { KB_TIER_LABEL, PROJECT_LINKED_CATEGORIES, STRATEGY_LABEL } from '../../lib/types';
import type { KbRow, VocabRow } from '../../lib/types';
import { ErrorBox } from '../../components/Common';

export default function UploadModal({ kbs, onClose, onUploaded }: {
  kbs: KbRow[];
  onClose: () => void;
  onUploaded: () => void;
}) {
  const [file, setFile] = useState<File | null>(null);
  const [kbId, setKbId] = useState('');
  const [classification, setClassification] = useState('');
  const [title, setTitle] = useState('');
  const [customerName, setCustomerName] = useState('');
  const [year, setYear] = useState(String(new Date().getFullYear()));
  const [deviceType, setDeviceType] = useState('');
  const [docCategory, setDocCategory] = useState('');
  const [projectNo, setProjectNo] = useState('');
  const [strategy, setStrategy] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  const customers = useQuery({ queryKey: ['vocab', 'customer_name'], queryFn: () => get<VocabRow[]>('/api/vocab/customer_name'), staleTime: 60_000 });
  const devices = useQuery({ queryKey: ['vocab', 'device_type'], queryFn: () => get<VocabRow[]>('/api/vocab/device_type'), staleTime: 60_000 });
  const categories = useQuery({ queryKey: ['vocab', 'doc_category'], queryFn: () => get<VocabRow[]>('/api/vocab/doc_category'), staleTime: 60_000 });

  const kb = kbs.find((k) => k.id === kbId);
  const projectRequired = PROJECT_LINKED_CATEGORIES.includes(docCategory);
  const ready = !!file && !!kbId && !!classification && !!customerName && year.length === 4
    && !!deviceType && !!docCategory && (!projectRequired || projectNo.trim().length > 0);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!ready || !file) return;
    setBusy(true);
    setError(null);
    try {
      const form = new FormData();
      form.append('file', file);
      form.append('kbId', kbId);
      form.append('classification', classification);
      form.append('customerName', customerName);
      form.append('year', year);
      form.append('deviceType', deviceType);
      form.append('docCategory', docCategory);
      if (projectNo.trim()) form.append('projectNo', projectNo.trim());
      if (title.trim()) form.append('title', title.trim());
      if (strategy) form.append('chunkStrategy', strategy);
      await upload('/api/documents', form);
      onUploaded();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '上传失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  };

  const fl: React.CSSProperties = { fontSize: 11.5, color: 'var(--ink-2)', marginBottom: 5 };
  const req = <span style={{ color: 'var(--cls-conf-fg)' }}> *</span>;
  const fi: React.CSSProperties = { height: 28, width: '100%', padding: '0 9px', background: 'var(--panel)', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };

  return (
    <div style={{ position: 'fixed', inset: 0, background: 'rgba(28,37,48,.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 50 }} onClick={onClose}>
      <div style={{ width: 640, maxHeight: '88vh', display: 'flex', flexDirection: 'column', background: 'var(--panel)', borderRadius: 8, overflow: 'hidden', boxShadow: '0 18px 50px rgba(16,24,40,.22)' }}
        onClick={(e) => e.stopPropagation()}>
        <div style={{ flexShrink: 0, padding: '16px 20px 13px', borderBottom: '1px solid var(--line)' }}>
          <div style={{ fontSize: 15, fontWeight: 600 }}>上传文档并登记元数据</div>
          <div className="hint" style={{ marginTop: 3 }}>必填元数据未填齐不允许提交；受控字段只能选不能写。上传后不会自动解析。</div>
        </div>

        <form onSubmit={submit} className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '16px 20px 8px' }}>
          <div
            style={{ border: '1.5px dashed var(--line-strong)', borderRadius: 6, padding: file ? '12px 14px' : '22px 14px', textAlign: 'center', cursor: 'pointer', background: 'var(--bg-soft)', marginBottom: 16 }}
            onClick={() => fileRef.current?.click()}
          >
            <input ref={fileRef} type="file" hidden accept=".pdf,.docx,.xlsx,.pptx,.txt,.md"
              onChange={(e) => { const f = e.target.files?.[0]; if (f) { setFile(f); if (!title) setTitle(f.name.replace(/\.[^.]+$/, '')); } }} />
            {file
              ? <div style={{ fontSize: 12.5, fontWeight: 500 }}>{file.name}　<span className="hint">{(file.size / 1048576).toFixed(1)} MB · 点击更换</span></div>
              : <div style={{ fontSize: 12.5, color: 'var(--ink-2)' }}>点击选择文件（PDF / DOCX / XLSX / PPTX / TXT）</div>}
          </div>

          <div style={{ display: 'flex', gap: 12, marginBottom: 14 }}>
            <div style={{ flex: '1 1 0' }}>
              <div style={fl}>目标知识库{req}</div>
              <select style={fi} value={kbId} onChange={(e) => setKbId(e.target.value)}>
                <option value="">请选择</option>
                {kbs.filter((k) => k.tier !== 'Shared').map((k) => (
                  <option key={k.id} value={k.id}>{k.name}（{KB_TIER_LABEL[k.tier]}）</option>
                ))}
              </select>
              {kb && <div className="hint" style={{ marginTop: 4 }}>默认切分策略：{STRATEGY_LABEL[kb.defaultChunkStrategy]}</div>}
            </div>
            <div style={{ width: 150 }}>
              <div style={fl}>密级{req}</div>
              <select style={fi} value={classification} onChange={(e) => setClassification(e.target.value)}>
                <option value="">请选择</option>
                <option value="Confidential">机密</option>
                <option value="Internal">内部</option>
                <option value="Public">公开</option>
              </select>
            </div>
            <div style={{ width: 170 }}>
              <div style={fl}>切分策略（可选）</div>
              <select style={fi} value={strategy} onChange={(e) => setStrategy(e.target.value)}>
                <option value="">随知识库默认</option>
                {Object.entries(STRATEGY_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </select>
            </div>
          </div>

          <div style={{ marginBottom: 14 }}>
            <div style={fl}>文档标题</div>
            <input style={fi} value={title} onChange={(e) => setTitle(e.target.value)} placeholder="默认取文件名" />
          </div>

          <div style={{ display: 'flex', gap: 12, marginBottom: 14 }}>
            <div style={{ flex: '1 1 0' }}>
              <div style={fl}>客户名称{req}</div>
              <select style={fi} value={customerName} onChange={(e) => setCustomerName(e.target.value)}>
                <option value="">请选择</option>
                {(customers.data ?? []).map((v) => <option key={v.id} value={v.value}>{v.value}</option>)}
              </select>
            </div>
            <div style={{ width: 110 }}>
              <div style={fl}>年份{req}</div>
              <input style={fi} className="m" inputMode="numeric" value={year}
                onChange={(e) => setYear(e.target.value.replace(/\D/g, '').slice(0, 4))} />
            </div>
            <div style={{ flex: '1 1 0' }}>
              <div style={fl}>设备类型{req}　<span style={{ color: 'var(--accent)' }}>受控词表</span></div>
              <select style={fi} value={deviceType} onChange={(e) => setDeviceType(e.target.value)}>
                <option value="">请选择</option>
                {(devices.data ?? []).map((v) => <option key={v.id} value={v.value}>{v.value}</option>)}
              </select>
            </div>
          </div>

          <div style={{ display: 'flex', gap: 12, marginBottom: 12 }}>
            <div style={{ flex: '1 1 0' }}>
              <div style={fl}>文档类别{req}　<span style={{ color: 'var(--accent)' }}>受控词表</span></div>
              <select style={fi} value={docCategory} onChange={(e) => setDocCategory(e.target.value)}>
                <option value="">请选择</option>
                {(categories.data ?? []).map((v) => <option key={v.id} value={v.value}>{v.value}</option>)}
              </select>
            </div>
            <div style={{ flex: '1 1 0' }}>
              <div style={fl}>项目编号{projectRequired ? req : <span className="hint">（该类别不填）</span>}</div>
              <input style={{ ...fi, ...(projectRequired && !projectNo.trim() ? { borderColor: 'var(--cls-int-line)', background: 'var(--cls-int-bg)' } : {}) }}
                className="m" value={projectNo} onChange={(e) => setProjectNo(e.target.value)}
                placeholder={projectRequired ? '须在台账中已登记' : '手册与资料类不关联项目'} />
            </div>
          </div>

          {projectRequired && (
            <div className="hint" style={{ marginBottom: 12, lineHeight: 1.65 }}>
              类别为「{docCategory}」时项目编号必填并关联台账（表 4-5 条件必填）——编号不在台账时请先到台账登记。
            </div>
          )}

          {error && <div style={{ marginBottom: 12 }}><ErrorBox message={error} /></div>}
        </form>

        <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 8, padding: '13px 20px', borderTop: '1px solid var(--line)', background: 'var(--bg-soft)' }}>
          <span className="hint">{ready ? '登记完整，可以提交' : '必填项未填齐时不允许提交'}</span>
          <div style={{ flexGrow: 1 }} />
          <button type="button" className="gbtn" onClick={onClose}>取消</button>
          <button type="button" className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={!ready || busy} onClick={(e) => void submit(e as unknown as React.FormEvent)}>
            {busy ? '上传中…' : '上传并登记'}
          </button>
        </div>
      </div>
    </div>
  );
}
