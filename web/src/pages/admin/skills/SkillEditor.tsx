// 编辑一个技能：代号、名称、什么时候用、正文（SKILL.md），给谁用、附带资料；保存留版本，送审、启用、退回、导出、试一试。
import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { get, post, put, del, download, ApiError } from '../../../lib/api';
import type { DocRow, RoleRow } from '../../../lib/types';
import { Icon } from '../../../components/Icons';
import { Spinner, ErrorBox } from '../../../components/Common';
import { SOURCE, STATUS, bodyLength, shortDate, unknownTools } from './types';
import type { SkillDetail, SkillMeta } from './types';
import { TryBox } from './TryBox';

const TEMPLATE = `# 技能名称

## 先确认（缺了先按常见情况答，并写明假设）
-

## 怎么判断
1. 先用 search_documents、query_projects 查公司以往的资料和项目
2.

## 常见的坑（必须提醒）
-

## 该查什么
search_documents · query_projects

## 回答怎么写
结论 → 依据（带出处）→ 风险提醒 → 要确认的参数
`;

interface Form { code: string; name: string; category: string; description: string; body: string; roles: string[]; refDocs: { id: string; title: string }[] }

export function SkillEditor({ id, meta, onSaved, onDeleted }: { id: string | null; meta: SkillMeta; onSaved: (id: string) => void; onDeleted?: () => void }) {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['skill', id], enabled: !!id, queryFn: () => get<SkillDetail>(`/api/skills/${id}`) });
  if (id && q.isLoading) return <div className="qy-detail"><Spinner text="正在载入…" /></div>;
  if (id && !q.data) return <div className="qy-detail"><ErrorBox message="这个技能没载入" /></div>;
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ['skills'] });
    void qc.invalidateQueries({ queryKey: ['skill', id] });
  };
  return <EditorBody key={q.data?.version ?? 'new'} detail={q.data ?? null} meta={meta} refresh={refresh} onSaved={onSaved} onDeleted={onDeleted} />;
}

function EditorBody({ detail, meta, refresh, onSaved, onDeleted }: {
  detail: SkillDetail | null; meta: SkillMeta; refresh: () => void; onSaved: (id: string) => void; onDeleted?: () => void;
}) {
  const init: Form = detail
    ? { code: detail.code, name: detail.name, category: detail.category, description: detail.description, body: detail.body, roles: detail.roles, refDocs: detail.refDocs }
    : { code: '', name: '', category: meta.categories[0] ?? '其他', description: '', body: TEMPLATE, roles: [], refDocs: [] };
  const [f, setF] = useState<Form>(init);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const [trying, setTrying] = useState(false);
  const [showVersions, setShowVersions] = useState(false);
  const [viewVersion, setViewVersion] = useState<number | null>(null);
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => get<RoleRow[]>('/api/roles'), staleTime: 5 * 60_000 });
  const set = (p: Partial<Form>) => setF((x) => ({ ...x, ...p }));
  const dirty = JSON.stringify(f) !== JSON.stringify(init);
  const len = bodyLength(f.body);
  const unknown = useMemo(() => unknownTools(f.body, meta.tools.map((t) => t.code)), [f.body, meta.tools]);
  const usedTools = meta.tools.filter((t) => f.body.includes(t.code) && t.code !== 'use_skill');
  const todo = (f.body.match(/\[待(写|填)/g) ?? []).length;

  const save = async (submit: boolean) => {
    setBusy(true); setMsg(null);
    const body = { code: f.code, name: f.name, category: f.category, description: f.description, body: f.body, roles: f.roles,
      refDocIds: f.refDocs.map((x) => x.id), submit, note: note.trim() || null };
    try {
      if (detail) { await put(`/api/skills/${detail.id}`, body); refresh(); setMsg(submit ? '已保存，等审核' : '已保存为草稿'); }
      else { const r = await post<{ id: string }>('/api/skills', body); refresh(); onSaved(r.id); }
    } catch (e) { setMsg(e instanceof ApiError ? e.message : '没保存上'); } finally { setBusy(false); }
  };
  const status = async (s: 'enabled' | 'draft' | 'review') => {
    if (!detail) return;
    setBusy(true); setMsg(null);
    try { await post(`/api/skills/${detail.id}/status`, { status: s }); refresh(); }
    catch (e) { setMsg(e instanceof ApiError ? e.message : '没改成'); } finally { setBusy(false); }
  };
  const remove = async () => {
    if (!detail || !confirm(`删掉技能「${detail.name}」？历史版本一起删，不能恢复。`)) return;
    await del(`/api/skills/${detail.id}`); refresh(); onDeleted?.();
  };

  return (
    <div className="sk-ed">
      <div className="sk-ed-hd">
        <div className="sk-ed-t">
          <div className="sk-ed-name">
            <b>{detail ? detail.name : '新建技能'}</b>
            {detail && <><span className={'sk-st ' + STATUS[detail.status].cls}>{STATUS[detail.status].label}</span><span className="sk-tag">v{detail.version}</span><span className="sk-tag">{SOURCE[detail.source] ?? detail.source}</span></>}
            {detail?.license && <span className="sk-tag">{detail.license}</span>}
          </div>
          {detail && <span className="hint">改于 {shortDate(detail.updatedAt)}{detail.reviewer ? ` · ${detail.reviewer} 审核` : ''} · 这 7 天被翻看 {detail.usedWeek} 次</span>}
        </div>
        <span className="sk-ed-act">
          {detail && <button type="button" className="gbtn" onClick={() => setShowVersions(!showVersions)}>历史版本</button>}
          {detail && <button type="button" className="gbtn" onClick={() => void download(`/api/skills/${detail.id}/export`, `${detail.code}-SKILL.md`)}>导出</button>}
          {detail && <button type="button" className="gbtn" disabled={dirty} title={dirty ? '先保存再试' : ''} onClick={() => setTrying(true)}>
            <Icon name="play" size={11} style={{ verticalAlign: '-1px', marginRight: 4 }} />试一试</button>}
        </span>
      </div>

      <div className="sk-ed-body">
        <div className="sk-ed-main">
          <div className="sk-row2">
            <label className="q-field" style={{ flex: '0 0 230px' }}><span>代号 <i>· 英文，导出时做文件夹名</i></span>
              <input className="sk-in mono" value={f.code} disabled={!!detail} onChange={(e) => set({ code: e.target.value })} placeholder="material-selection" /></label>
            <label className="q-field" style={{ flex: 1 }}><span>名称</span>
              <input className="sk-in" value={f.name} onChange={(e) => set({ name: e.target.value })} placeholder="选材与耐腐蚀" /></label>
            <label className="q-field" style={{ flex: '0 0 150px' }}><span>分类</span>
              <select className="sk-in" value={f.category} onChange={(e) => set({ category: e.target.value })}>
                {meta.categories.map((c) => <option key={c}>{c}</option>)}
              </select></label>
          </div>
          <label className="q-field"><span>什么时候用 <i>· 助手平时只看这一句来决定要不要翻出来，把场景写清楚</i></span>
            <input className="sk-in" value={f.description} onChange={(e) => set({ description: e.target.value })} placeholder="客户问某种材料能不能用在某个介质工况、要推荐材质时用。" /></label>
          <label className="q-field sk-body"><span>正文 <i>· Markdown；</i>
            <i className={len > meta.maxBody ? 'bad' : ''}>约 {len} 字（上限 {meta.maxBody}，本地模型看得完）</i>
            {todo > 0 && <i className="bad">· 还有 {todo} 处待写 / 待填</i>}</span>
            <textarea className="sk-md" value={f.body} onChange={(e) => set({ body: e.target.value })} spellCheck={false} /></label>
          {unknown.length > 0 && (
            <div className="sk-warn"><Icon name="x" size={13} stroke={2.2} />正文提到了助手没有的工具：{unknown.join('、')}。改成右边列出的工具名，否则助手照着做会找不到。</div>
          )}
        </div>

        <div className="sk-ed-side">
          <div className="sk-card">
            <span className="sk-cap">给谁用 <i>· 不选 = 所有内部员工</i></span>
            <div className="q-chips">
              {(roles.data ?? []).filter((r) => r.code !== 'customer').map((r) => (
                <button key={r.id} type="button" className={'q-chip' + (f.roles.includes(r.code) ? ' on' : '')}
                  onClick={() => set({ roles: f.roles.includes(r.code) ? f.roles.filter((x) => x !== r.code) : [...f.roles, r.code] })}>{r.name}</button>
              ))}
            </div>
          </div>
          {detail && (
            <div className="sk-card">
              <span className="sk-cap">聊天「用技能」清单 <i>· 不放进去照样启用，助手碰到相关问题自己会用</i></span>
              <div className="q-chips">
                {[true, false].map((v) => (
                  <button key={String(v)} type="button" className={'q-chip' + (detail.inMenu === v ? ' on' : '')} disabled={busy}
                    onClick={() => {
                      if (detail.inMenu === v) return;
                      setBusy(true);
                      post(`/api/skills/${detail.id}/menu`, { inMenu: v }).then(refresh, () => setMsg('没改成'))
                        .finally(() => setBusy(false));
                    }}>{v ? '放进清单' : '不放（自动用）'}</button>
                ))}
              </div>
            </div>
          )}
          <RefDocs value={f.refDocs} onChange={(refDocs) => set({ refDocs })} />
          <div className="sk-card">
            <span className="sk-cap">正文里用到的工具</span>
            <div className="q-chips">
              {usedTools.length === 0 && <span className="hint">还没提到工具。写明「用 search_documents 查……」助手才知道去哪查。</span>}
              {usedTools.map((t) => <span key={t.code} className="sk-tool" title={t.code}>{t.label}</span>)}
            </div>
            <details className="sk-tools-all"><summary>全部工具名</summary>
              {meta.tools.map((t) => <div key={t.code}><code>{t.code}</code> {t.label}</div>)}
            </details>
          </div>
          {detail && (
            <div className="sk-card">
              <span className="sk-cap">效果</span>
              <div className="sk-kv"><span>这 7 天被翻看</span><b>{detail.usedWeek} 次</b></div>
              <div className="sk-kv"><span>用了它的回答被点「没帮上」</span><b className={detail.notHelpfulWeek ? 'bad' : ''}>{detail.notHelpfulWeek} 条</b></div>
              {detail.notHelpfulWeek > 0 && <Link to="/admin/quality" className="hint">去「回答质量」看看 ›</Link>}
              <span className="hint">改之前用「试一试」对比，有代表性的问题存成测试题。</span>
            </div>
          )}
        </div>
      </div>

      <div className="sk-ed-ft">
        <input className="sk-in sk-note" value={note} onChange={(e) => setNote(e.target.value)} placeholder="这次改了什么（记在版本里，可不填）" />
        {msg && <span className="hint">{msg}</span>}
        <span className="sk-ed-act">
          {detail && <button type="button" className="gbtn sk-del" onClick={() => void remove()}>删除</button>}
          {detail?.status === 'Enabled' && !dirty && <button type="button" className="gbtn" disabled={busy} onClick={() => void status('draft')}>停用</button>}
          <button type="button" className="gbtn" disabled={busy || (!!detail && !dirty)} onClick={() => void save(false)}>保存草稿</button>
          {(!detail || dirty || detail.status === 'Draft') && (
            <button type="button" className="pbtn" disabled={busy || (!!detail && !dirty && detail.status !== 'Draft')} onClick={() => void save(true)}>
              {detail ? (detail.status === 'Enabled' ? `保存为 v${detail.version + 1}（送审）` : '保存并送审') : '保存并送审'}</button>
          )}
          {detail && !dirty && detail.status === 'Review' && (
            <button type="button" className="pbtn" disabled={busy} onClick={() => void status('enabled')}>审核通过，启用</button>
          )}
        </span>
      </div>
      {detail?.status === 'Enabled' && dirty && <div className="sk-tip">改了内容要重新审核：保存后先停用，审核通过再生效。</div>}

      {showVersions && detail && (
        <div className="sk-versions">
          <div className="sk-cap">历史版本</div>
          {detail.versions.map((v) => (
            <button key={v.version} type="button" className="sk-ver" onClick={() => setViewVersion(v.version)}>
              <b>v{v.version}</b><span>{shortDate(v.editedAt)}</span><span>{v.editor ?? ''}</span><span className="hint">{v.note ?? ''}</span>
            </button>
          ))}
        </div>
      )}
      {viewVersion != null && detail && <VersionView id={detail.id} v={viewVersion} current={detail.version} onClose={() => setViewVersion(null)} onRestored={() => { setViewVersion(null); refresh(); }} />}
      {trying && detail && <TryBox code={detail.code} name={detail.name} onClose={() => setTrying(false)} />}
    </div>
  );
}

function RefDocs({ value, onChange }: { value: { id: string; title: string }[]; onChange: (v: { id: string; title: string }[]) => void }) {
  const [kw, setKw] = useState('');
  const docs = useQuery({ queryKey: ['skill-docs', kw], enabled: kw.trim().length >= 2, queryFn: () => get<DocRow[]>(`/api/documents?search=${encodeURIComponent(kw.trim())}`) });
  return (
    <div className="sk-card">
      <span className="sk-cap">附带的参考资料 <i>· 助手翻看技能时一起告诉它</i></span>
      {value.map((d) => (
        <div key={d.id} className="sk-doc"><Icon name="file" size={13} /><span>{d.title}</span>
          <button type="button" aria-label="去掉" onClick={() => onChange(value.filter((x) => x.id !== d.id))}><Icon name="x" size={12} /></button></div>
      ))}
      <input className="sk-in" value={kw} onChange={(e) => setKw(e.target.value)} placeholder="搜知识库里的文件名，挑一份加上" />
      {(docs.data ?? []).filter((d) => !value.some((x) => x.id === d.id)).slice(0, 6).map((d) => (
        <button key={d.id} type="button" className="sk-doc-add" onClick={() => { onChange([...value, { id: d.id, title: d.title }]); setKw(''); }}>＋ {d.title}</button>
      ))}
    </div>
  );
}

function VersionView({ id, v, current, onClose, onRestored }: { id: string; v: number; current: number; onClose: () => void; onRestored: () => void }) {
  const q = useQuery({ queryKey: ['skill-ver', id, v], queryFn: () => get<{ version: number; name: string; description: string; body: string; note: string | null; editedAt: string }>(`/api/skills/${id}/versions/${v}`) });
  const [err, setErr] = useState<string | null>(null);
  const restore = async () => {
    try { await post(`/api/skills/${id}/restore/${v}`); onRestored(); } catch (e) { setErr(e instanceof ApiError ? e.message : '没退回成'); }
  };
  return (
    <div className="q-mask" role="dialog" aria-modal="true" aria-label={`v${v}`} onClick={onClose}>
      <div className="q-dlg sk-ver-dlg" onClick={(e) => e.stopPropagation()}>
        <div className="q-dlg-hd"><b>v{v}{q.data ? ` · ${q.data.name}` : ''}</b>{q.data?.note && <span className="hint">{q.data.note}</span>}
          <button type="button" className="q-x" aria-label="关闭" onClick={onClose}><Icon name="x" size={14} /></button></div>
        {q.data && <><div className="hint">什么时候用：{q.data.description}</div><pre className="sk-md sk-md-ro">{q.data.body}</pre></>}
        {err && <span className="q-err">{err}</span>}
        <div className="q-dlg-ft">
          <span className="hint" style={{ marginRight: 'auto' }}>退回会把这一版存成 v{current + 1}，要重新审核</span>
          <button type="button" className="gbtn" onClick={onClose}>关闭</button>
          {v !== current && <button type="button" className="pbtn" onClick={() => void restore()}>退回到这一版</button>}
        </div>
      </div>
    </div>
  );
}
