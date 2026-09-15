// B4 案例录入 / 编辑：七个结构化字段（FR-8.1）。保存即在本公司生效，不等审核（FR-8.2）——
// 右栏解释「一条案例同时是两样东西」：结构化行供精确查询，渲染分块供语义检索。
import React, { useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { get, post, put, ApiError } from '../../lib/api';
import type { CaseDetailData, CaseRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';

interface Form {
  deviceModel: string; alarmCode: string; phenomenon: string; causeAnalysis: string;
  steps: string; spareParts: string; result: string; note: string;
}
const EMPTY: Form = { deviceModel: '', alarmCode: '', phenomenon: '', causeAnalysis: '', steps: '', spareParts: '', result: '已解决', note: '' };

export default function CaseEntryPage() {
  const { caseId } = useParams(); // 有 caseId = 编辑态
  const nav = useNavigate();
  const [form, setForm] = useState<Form>(EMPTY);
  const [loaded, setLoaded] = useState(!caseId);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!caseId) return;
    void get<CaseDetailData>(`/api/cases/${caseId}`).then((c) => {
      setForm({
        deviceModel: c.deviceModel, alarmCode: c.alarmCode ?? '', phenomenon: c.phenomenon,
        causeAnalysis: c.causeAnalysis, steps: c.steps, spareParts: c.spareParts ?? '',
        result: c.result, note: c.extra?.['补充说明'] ?? '',
      });
      setLoaded(true);
    }).catch(() => setError('载入案例失败'));
  }, [caseId]);

  // 同型号已有案例：同代码不同成因，录入时把成因写清楚，检索归并时才分得开
  const similar = useQuery({
    queryKey: ['similar-cases', form.deviceModel],
    queryFn: () => post<CaseRow[]>('/api/cases/search', { deviceModel: form.deviceModel.trim(), limit: 5 }),
    enabled: form.deviceModel.trim().length >= 3,
    staleTime: 30_000,
  });

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const body = {
        deviceModel: form.deviceModel.trim(),
        alarmCode: form.alarmCode.trim() || null,
        phenomenon: form.phenomenon.trim(),
        causeAnalysis: form.causeAnalysis.trim(),
        steps: form.steps.trim(),
        spareParts: form.spareParts.trim() || null,
        result: form.result,
        extra: form.note.trim() ? { 补充说明: form.note.trim() } : null,
      };
      if (caseId) {
        await put(`/api/cases/${caseId}`, body);
        nav(`/cases/${caseId}`);
      } else {
        const { id } = await post<{ id: string }>('/api/cases', body);
        nav(`/cases/${id}`);
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '保存失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  };

  if (!loaded && !error) return <div style={{ padding: 40 }}><Spinner text="正在载入…" /></div>;

  const fl: React.CSSProperties = { fontSize: 11.5, color: 'var(--ink-2)', marginBottom: 5 };
  const req = <span style={{ color: 'var(--cls-conf-fg)' }}> *</span>;
  const fi: React.CSSProperties = { height: 28, width: '100%', padding: '0 9px', background: 'var(--panel)', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };
  const ta: React.CSSProperties = { width: '100%', padding: '8px 10px', background: 'var(--panel)', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, lineHeight: 1.8, fontFamily: 'var(--font)', resize: 'vertical' };

  return (
    <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minHeight: 0, minWidth: 0 }}>
      <div style={{ height: 54, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 12, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <button className="gbtn" style={{ height: 28, padding: '0 9px' }} onClick={() => nav(-1)} title="返回">←</button>
        <div>
          <div style={{ fontSize: 14, fontWeight: 600, lineHeight: 1.4 }}>{caseId ? '编辑故障案例' : '新建故障案例'}</div>
          <div className="hint" style={{ marginTop: 2 }}>带 * 为必填 · 客户名称与项目编号不要写进任何字段——提交总部前会检测</div>
        </div>
        <div style={{ flexGrow: 1 }} />
        <span className="pill" style={{ height: 22, padding: '0 9px', fontSize: 11.5, background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' }}>
          {caseId ? '编辑中' : '草稿'}
        </span>
      </div>

      <div style={{ flexGrow: 1, display: 'flex', minHeight: 0 }}>
        {/* 表单 */}
        <form onSubmit={save} className="sc" style={{ flexGrow: 1, minWidth: 0, padding: '16px 20px 20px' }}>
          <div style={{ maxWidth: 820 }}>
            <div style={{ display: 'flex', gap: 14, marginBottom: 14 }}>
              <div style={{ width: 220 }}>
                <div style={fl}>设备型号{req}</div>
                <input style={fi} className="m" value={form.deviceModel} placeholder="如 CJF-5L"
                  onChange={(e) => setForm({ ...form, deviceModel: e.target.value })} />
              </div>
              <div style={{ width: 160 }}>
                <div style={fl}>报警代码</div>
                <input style={fi} className="m" value={form.alarmCode} placeholder="如 E-17"
                  onChange={(e) => setForm({ ...form, alarmCode: e.target.value })} />
              </div>
              <div style={{ width: 220 }}>
                <div style={fl}>处理结果{req}</div>
                <select style={fi} value={form.result} onChange={(e) => setForm({ ...form, result: e.target.value })}>
                  <option>已解决</option>
                  <option>临时处理，待跟进</option>
                  <option>未解决</option>
                </select>
              </div>
            </div>
            <div style={{ marginBottom: 14 }}>
              <div style={fl}>故障现象{req}</div>
              <textarea style={{ ...ta, minHeight: 74 }} value={form.phenomenon} placeholder="现场表现、报警与复现条件。写清楚现象，别人才能按描述语义检索到这条案例。"
                onChange={(e) => setForm({ ...form, phenomenon: e.target.value })} />
            </div>
            <div style={{ marginBottom: 14 }}>
              <div style={fl}>原因判断{req}</div>
              <textarea style={{ ...ta, minHeight: 74 }} value={form.causeAnalysis} placeholder="同一报警代码可能有不同成因——成因写清楚，检索归并时才分得开。"
                onChange={(e) => setForm({ ...form, causeAnalysis: e.target.value })} />
            </div>
            <div style={{ marginBottom: 14 }}>
              <div style={fl}>处理步骤{req}</div>
              <textarea style={{ ...ta, minHeight: 110 }} value={form.steps} placeholder={'1. …\n2. …\n按序写明操作与关键参数（力矩、温度、时长）。'}
                onChange={(e) => setForm({ ...form, steps: e.target.value })} />
            </div>
            <div style={{ display: 'flex', gap: 14, marginBottom: 14 }}>
              <div style={{ flexGrow: 1 }}>
                <div style={fl}>所用备件</div>
                <input style={fi} value={form.spareParts} placeholder="名称、规格/编号、数量，如：磁力耦合轴承 MC-5L-B × 1 套"
                  onChange={(e) => setForm({ ...form, spareParts: e.target.value })} />
              </div>
            </div>
            <div style={{ marginBottom: 18 }}>
              <div style={fl}>补充说明</div>
              <input style={fi} value={form.note} placeholder="预防建议、保养提示等"
                onChange={(e) => setForm({ ...form, note: e.target.value })} />
            </div>
            {error && <div style={{ marginBottom: 12 }}><ErrorBox message={error} /></div>}
            <div style={{ display: 'flex', gap: 8 }}>
              <button type="submit" className="pbtn" style={{ height: 30, padding: '0 16px' }} disabled={busy}>
                {busy ? '保存中…' : '保存并生效'}
              </button>
              <button type="button" className="gbtn" style={{ height: 30 }} onClick={() => nav(-1)}>取消</button>
            </div>
          </div>
        </form>

        {/* 右栏：保存后会发生什么 + 同型号已有案例 */}
        <div style={{ width: 460, flexShrink: 0, borderLeft: '1px solid var(--line)', background: 'var(--panel)', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '16px 18px' }}>
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 10 }}>保存后会发生什么</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10, marginBottom: 18 }}>
              {[
                <>立即写入本公司私有库，<strong style={{ fontWeight: 600 }}>不等审核</strong>。保存后本公司同事即可检索到。</>,
                <>按固定模板渲染为一段文本作为分块入库，上面这些结构化字段同时作为该分块的元数据。所以它既能按型号、代码精确查，也能按现象描述语义检索。</>,
                <>提交总部是可选的另一步，在案例页发起。提交前会先做敏感信息检测。</>,
              ].map((t, i) => (
                <div key={i} style={{ display: 'flex', gap: 10, padding: '11px 12px', background: 'var(--bg-soft)', border: '1px solid var(--line-soft)', borderRadius: 5 }}>
                  <span className="m" style={{ flexShrink: 0, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', width: 18, height: 18, borderRadius: 3, fontSize: 11, fontWeight: 500, background: '#eef1f5', color: 'var(--ink-2)' }}>{i + 1}</span>
                  <div style={{ fontSize: 12, lineHeight: 1.7 }}>{t}</div>
                </div>
              ))}
            </div>

            {form.deviceModel.trim().length >= 3 && (
              <>
                <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 9 }}>同型号已有案例</div>
                {similar.isLoading && <Spinner text="查询中…" />}
                {similar.data && similar.data.filter((r) => r.id !== caseId).length === 0 && (
                  <div className="hint">「{form.deviceModel}」还没有已录入的案例，这将是第一条。</div>
                )}
                {similar.data && similar.data.filter((r) => r.id !== caseId).length > 0 && (
                  <>
                    <div style={{ border: '1px solid var(--line)', borderRadius: 5, overflow: 'hidden' }}>
                      {similar.data.filter((r) => r.id !== caseId).slice(0, 4).map((r) => (
                        <div key={r.id} style={{ padding: '10px 12px', borderBottom: '1px solid var(--line-soft)', cursor: 'pointer' }} onClick={() => nav(`/cases/${r.id}`)}>
                          <div style={{ display: 'flex', alignItems: 'center', gap: 7, marginBottom: 3 }}>
                            {r.alarmCode && <span className="m pill" style={{ background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' }}>{r.alarmCode}</span>}
                            <span style={{ fontSize: 11, color: 'var(--ink-3)' }}>
                              {r.sourceCompany ? `共享库 · ${r.sourceCompany}` : '本公司'} · {r.updatedAt.slice(0, 10)}
                            </span>
                          </div>
                          <div style={{ fontSize: 12, lineHeight: 1.6 }}>{r.phenomenon}</div>
                        </div>
                      ))}
                    </div>
                    <div style={{ fontSize: 11, color: 'var(--ink-3)', lineHeight: 1.65, marginTop: 8 }}>
                      同代码可能不同成因。录入时把成因写清楚，检索归并时才分得开。
                    </div>
                  </>
                )}
              </>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
