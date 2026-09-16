// E12 模板管理 + E13 槽位定义 + E11 条款库（表 9-1 未给条款库位置——三者都是文档生成
// 的配置，合并到「模板管理」下三个 tab，左栏保持七项）。
// 三条要点：只有内容控件标定的位置才会被抽到（琥珀提示）；槽位定义不完整的模板不可启用；
// 条款审定后锁定正文，改动走「拟修改」生成待审新版本，旧版停用但保留。
import React, { useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put, upload, ApiError } from '../../lib/api';

import type { ClauseRow, SlotDef, TemplateRowT } from '../../lib/types';
import { ErrorBox, InfoBox, Spinner } from '../../components/Common';

interface TemplateDetail { id: string; name: string; docType: string; isEnabled: boolean; slots: SlotDef[]; }

export default function TemplatesAdminPage() {
  const [tab, setTab] = useState<'templates' | 'slots' | 'clauses'>('templates');
  const [slotTemplateId, setSlotTemplateId] = useState<string | null>(null);
  const tabStyle = (on: boolean): React.CSSProperties => ({
    height: 40, padding: '0 13px', display: 'inline-flex', alignItems: 'center', fontSize: 12.5, cursor: 'pointer',
    borderBottom: on ? '2px solid var(--accent)' : '2px solid transparent',
    color: on ? 'var(--accent)' : 'var(--ink-2)', fontWeight: on ? 600 : 400,
  });
  return (
    <>
      <div style={{ flexShrink: 0, display: 'flex', gap: 2, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <span style={tabStyle(tab === 'templates')} onClick={() => setTab('templates')}>模板</span>
        <span style={tabStyle(tab === 'slots')} onClick={() => setTab('slots')}>槽位定义</span>
        <span style={tabStyle(tab === 'clauses')} onClick={() => setTab('clauses')}>条款库</span>
      </div>
      {tab === 'templates' && <TemplatesTab onEditSlots={(id) => { setSlotTemplateId(id); setTab('slots'); }} />}
      {tab === 'slots' && <SlotsTab templateId={slotTemplateId} onPick={setSlotTemplateId} />}
      {tab === 'clauses' && <ClausesTab />}
    </>
  );
}

const fi: React.CSSProperties = { height: 28, padding: '0 9px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };

// ─── E12 ───────────────────────────────────────────────
function TemplatesTab({ onEditSlots }: { onEditSlots: (id: string) => void }) {
  const qc = useQueryClient();
  const rows = useQuery({ queryKey: ['tpl-admin'], queryFn: () => get<TemplateRowT[]>('/api/templates?includeDisabled=true') });
  const fileRef = useRef<HTMLInputElement>(null);
  const [name, setName] = useState('');
  const [docType, setDocType] = useState('方案');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [uploadResult, setUploadResult] = useState<{ slotsExtracted: number; warnings: string[] } | null>(null);

  const refresh = () => void qc.invalidateQueries({ queryKey: ['tpl-admin'] });
  const doUpload = async (f: File) => {
    setBusy(true);
    setError(null);
    setUploadResult(null);
    try {
      const form = new FormData();
      form.append('file', f);
      form.append('name', name.trim() || f.name.replace(/\.[^.]+$/, ''));
      form.append('docType', docType);
      setUploadResult(await upload('/api/templates', form));
      setName('');
      refresh();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '上传失败');
    } finally {
      setBusy(false);
    }
  };
  const act = async (fn: () => Promise<unknown>) => {
    setError(null);
    try {
      await fn();
      refresh();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '操作失败');
    }
  };

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
      <div style={{ maxWidth: 980, margin: '0 auto' }}>
        <div style={{ padding: '11px 13px', borderRadius: 5, background: 'var(--cls-int-bg)', border: '1px solid var(--cls-int-line)', marginBottom: 14 }}>
          <span style={{ fontSize: 11.5, lineHeight: 1.7, color: 'var(--cls-int-fg)' }}>
            只有被内容控件标定的位置才会被抽为槽位。模板里用下划线、方括号或黄色底纹表示的可变项抽不出来——那些位置生成时会原样输出。整理模板时先把它们改成内容控件，这是模板整理阶段的主要工作量。
            控件属性里除「标签」（槽位标识）外，把「标题」写成「章节/名称」（如 基本信息/合同编号），上传后显示名称与所属章节就会自动带出，省去逐项补录。
          </span>
        </div>

        <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginBottom: 14 }}>
          <input style={{ ...fi, width: 200 }} placeholder="模板名称（默认取文件名）" value={name} onChange={(e) => setName(e.target.value)} />
          <select style={fi} value={docType} onChange={(e) => setDocType(e.target.value)}>
            {['方案', '投标', '报价', '合同'].map((t) => <option key={t}>{t}</option>)}
          </select>
          <input ref={fileRef} type="file" accept=".docx" hidden onChange={(e) => { const f = e.target.files?.[0]; if (f) void doUpload(f); e.target.value = ''; }} />
          <button className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={busy} onClick={() => fileRef.current?.click()}>
            {busy ? '抽取中…' : '上传模板（docx）'}
          </button>
        </div>
        {error && <div style={{ marginBottom: 10 }}><ErrorBox message={error} /></div>}
        {uploadResult && (
          <div style={{ marginBottom: 12 }}>
            <InfoBox>
              抽取到 {uploadResult.slotsExtracted} 个内容控件槽位。
              {uploadResult.warnings.length > 0 && ` 提示：${uploadResult.warnings.join('；')}`}
              控件标题写了「章节/名称」的已自动带出；其余到「槽位定义」页补齐显示名称与章节后才能启用。
            </InfoBox>
          </div>
        )}

        {rows.isLoading && <Spinner text="载入…" />}
        {(rows.data ?? []).map((t) => (
          <div key={t.id} className="card" style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '11px 14px', marginBottom: 8 }}>
            <div style={{ flexGrow: 1, minWidth: 0 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <span style={{ fontSize: 12.5, fontWeight: 600 }}>{t.name}</span>
                <span className="pill pill-neutral">{t.docType}</span>
                <span className="pill" style={t.isEnabled
                  ? { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' }
                  : { background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' }}>
                  {t.isEnabled ? '已启用' : '未启用'}
                </span>
              </div>
              <div className="hint" style={{ marginTop: 3 }}>
                {t.slotCount} 个槽位
                {t.incompleteSlots > 0 && <span style={{ color: 'var(--cls-int-fg)' }}>　·　{t.incompleteSlots} 个定义不完整——补齐前不可启用</span>}
                {t.lastUsedAt && `　·　最近使用 ${t.lastUsedAt.slice(0, 10)}`}
              </div>
            </div>
            <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => onEditSlots(t.id)}>槽位定义</button>
            <button className="gbtn" style={{ height: 24, fontSize: 11.5 }}
              onClick={() => void act(() => post(`/api/templates/${t.id}/copy`, { newName: `${t.name}（副本）` }))}>复制</button>
            <button className={t.isEnabled ? 'gbtn' : 'pbtn'} style={{ height: 24, fontSize: 11.5, padding: '0 10px' }}
              onClick={() => void act(() => post(`/api/templates/${t.id}/enable`, { enable: !t.isEnabled }))}>
              {t.isEnabled ? '停用' : '启用'}
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}

// ─── E13 ───────────────────────────────────────────────
const DATA_TYPES: [string, string][] = [
  ['Text', '文本'], ['Number', '数值'], ['SingleChoice', '单选'], ['MultiChoice', '多选'],
  ['Date', '日期'], ['LongText', '长段落'], ['RepeatingRows', '重复行'],
];

function SlotsTab({ templateId, onPick }: { templateId: string | null; onPick: (id: string) => void }) {
  const templates = useQuery({ queryKey: ['tpl-admin'], queryFn: () => get<TemplateRowT[]>('/api/templates?includeDisabled=true') });
  const active = templateId ?? templates.data?.[0]?.id ?? null;
  const detail = useQuery({
    queryKey: ['tpl-detail', active],
    queryFn: () => get<TemplateDetail>(`/api/templates/${active}`),
    enabled: !!active,
  });
  const [editing, setEditing] = useState<SlotDef | null>(null);

  return (
    <div style={{ flexGrow: 1, display: 'flex', minHeight: 0 }}>
      <div className="sc" style={{ flexGrow: 1, minWidth: 0, padding: '16px 20px' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
          <select style={fi} value={active ?? ''} onChange={(e) => onPick(e.target.value)}>
            {(templates.data ?? []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
          <span className="hint">槽位标识与提问话术初值取自内容控件（自动），其余九项人工维护（表 5-2）。</span>
        </div>
        {detail.isLoading && <Spinner text="载入槽位…" />}
        {(detail.data?.slots ?? []).map((s) => {
          const incomplete = !s.name.trim() || !s.section.trim();
          return (
            <div key={s.id} style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '8px 0', borderBottom: '1px solid var(--line-soft)', fontSize: 12.5 }}>
              <span className="m" style={{ width: 150, color: 'var(--ink-2)' }}>{s.tag}<span className="hint">（自动）</span></span>
              <span style={{ width: 120, fontWeight: 500, color: incomplete ? 'var(--cls-int-fg)' : undefined }}>{s.name || '（未命名）'}</span>
              <span style={{ width: 90, color: 'var(--ink-2)' }}>{s.section || '（无章节）'}</span>
              <span className="pill pill-neutral">{DATA_TYPES.find(([k]) => k === s.dataType)?.[1] ?? s.dataType}</span>
              {s.required && <span className="pill" style={{ background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' }}>必填</span>}
              {s.stage === 'Later' && <span className="pill pill-neutral">后续阶段</span>}
              {s.forbidInherit && <span className="pill pill-neutral">禁止继承</span>}
              <div style={{ flexGrow: 1 }} />
              <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => setEditing(s)}>编辑</button>
            </div>
          );
        })}
      </div>
      {editing && active && (
        <SlotEditPanel key={editing.id} slot={editing} onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); void detail.refetch(); void templates.refetch(); }} />
      )}
    </div>
  );
}

function SlotEditPanel({ slot, onClose, onSaved }: { slot: SlotDef; onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState({ ...slot, unit: slot.unit ?? '', choices: slot.choices ?? '', prompt: slot.prompt ?? '', suggestSource: slot.suggestSource ?? '' });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await put(`/api/templates/slots/${slot.id}`, {
        name: f.name.trim(), section: f.section.trim(), dataType: f.dataType,
        unit: f.unit.trim() || null, choices: f.choices.trim() || null,
        required: f.required, stage: f.stage, forbidInherit: f.forbidInherit,
        prompt: f.prompt.trim() || null, suggestSource: f.suggestSource.trim() || null,
        subFields: slot.subFields, sortOrder: f.sortOrder,
      });
      onSaved();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '保存失败');
    } finally {
      setBusy(false);
    }
  };
  const fl: React.CSSProperties = { fontSize: 11.5, color: 'var(--ink-2)', marginBottom: 4 };

  return (
    <form onSubmit={save} className="sc" style={{ width: 340, flexShrink: 0, borderLeft: '1px solid var(--line)', background: 'var(--panel)', padding: '16px 16px 20px', minHeight: 0 }}>
      <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 4 }}>槽位定义</div>
      <div className="m hint" style={{ marginBottom: 12 }}>{slot.tag}（标识取自内容控件 Tag，不可改）</div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        <div><div style={fl}>显示名称 *</div><input style={{ ...fi, width: '100%' }} value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} /></div>
        <div><div style={fl}>所属章节 *</div><input style={{ ...fi, width: '100%' }} value={f.section} onChange={(e) => setF({ ...f, section: e.target.value })} /></div>
        <div style={{ display: 'flex', gap: 8 }}>
          <div style={{ flex: 1 }}><div style={fl}>数据类型</div>
            <select style={{ ...fi, width: '100%' }} value={f.dataType} onChange={(e) => setF({ ...f, dataType: e.target.value })}>
              {DATA_TYPES.map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </select></div>
          <div style={{ width: 90 }}><div style={fl}>单位</div><input style={{ ...fi, width: '100%' }} value={f.unit} onChange={(e) => setF({ ...f, unit: e.target.value })} /></div>
        </div>
        <div><div style={fl}>可选项（单选/多选，用 | 分隔）</div><input style={{ ...fi, width: '100%' }} value={f.choices} onChange={(e) => setF({ ...f, choices: e.target.value })} /></div>
        <div style={{ display: 'flex', gap: 14 }}>
          <label style={{ display: 'flex', alignItems: 'center', gap: 5, fontSize: 12.5, cursor: 'pointer' }}>
            <input type="checkbox" checked={f.required} onChange={(e) => setF({ ...f, required: e.target.checked })} />必填
          </label>
          <label style={{ display: 'flex', alignItems: 'center', gap: 5, fontSize: 12.5, cursor: 'pointer' }}>
            <input type="checkbox" checked={f.stage === 'Later'} onChange={(e) => setF({ ...f, stage: e.target.checked ? 'Later' : 'Current' })} />后续阶段
          </label>
          <label style={{ display: 'flex', alignItems: 'center', gap: 5, fontSize: 12.5, cursor: 'pointer' }}>
            <input type="checkbox" checked={f.forbidInherit} onChange={(e) => setF({ ...f, forbidInherit: e.target.checked })} />禁止继承
          </label>
        </div>
        <div><div style={fl}>提问话术（初值取自控件占位文字·自动）</div>
          <textarea style={{ width: '100%', minHeight: 54, padding: '7px 9px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, lineHeight: 1.6, fontFamily: 'var(--font)', resize: 'vertical' }}
            value={f.prompt} onChange={(e) => setF({ ...f, prompt: e.target.value })} /></div>
        <div><div style={fl}>建议来源规则（按序尝试，命中即停：精确标题 → 标题关键词 → 章节序号）</div>
          <input style={{ ...fi, width: '100%' }} className="m" placeholder="如：技术参数|参数表|3" value={f.suggestSource} onChange={(e) => setF({ ...f, suggestSource: e.target.value })} /></div>
      </div>
      {error && <div style={{ marginTop: 10 }}><ErrorBox message={error} /></div>}
      <div style={{ display: 'flex', gap: 8, marginTop: 14 }}>
        <button type="submit" className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={busy}>保存</button>
        <button type="button" className="gbtn" onClick={onClose}>取消</button>
      </div>
    </form>
  );
}

// ─── E11 ───────────────────────────────────────────────
function ClausesTab() {
  const qc = useQueryClient();
  const rows = useQuery({ queryKey: ['clauses-admin'], queryFn: () => get<ClauseRow[]>('/api/clauses') });
  const [editing, setEditing] = useState<'new' | ClauseRow | null>(null);
  const [error, setError] = useState<string | null>(null);
  const refresh = () => void qc.invalidateQueries({ queryKey: ['clauses-admin'] });
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
    Active: { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' },
    PendingReview: { background: 'var(--cls-int-bg)', color: 'var(--cls-int-fg)', border: '1px solid var(--cls-int-line)' },
    Draft: { background: 'var(--cls-int-bg)', color: 'var(--cls-int-fg)', border: '1px solid var(--cls-int-line)' },
    Retired: { background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' },
  };
  const STATUS_LABEL: Record<string, string> = { Active: '已审定', PendingReview: '待审定', Draft: '草稿', Retired: '已替代（保留）' };

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
      <div style={{ maxWidth: 980, margin: '0 auto' }}>
        <div className="hint" style={{ lineHeight: 1.7, marginBottom: 12 }}>
          条款一经审定即锁定正文，不能就地编辑——改动走「拟修改」生成待审新版本，通过后新版生效、旧版停用但保留（旧合同要能查到当时原文）。待审定条款不进入报价拼装的可选范围。
        </div>
        {error && <div style={{ marginBottom: 10 }}><ErrorBox message={error} /></div>}
        {!editing && <button className="pbtn" style={{ height: 28, padding: '0 14px', marginBottom: 12 }} onClick={() => setEditing('new')}>起草新条款</button>}
        {editing && (
          <ClauseEditPanel base={editing === 'new' ? null : editing}
            onDone={() => { setEditing(null); refresh(); }} onError={setError} />
        )}
        {rows.isLoading && <Spinner text="载入条款库…" />}
        {(rows.data ?? []).map((c) => (
          <div key={c.id} className="card" style={{ padding: '11px 14px', marginBottom: 8 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 4 }}>
              <span className="m pill pill-neutral">{c.code}</span>
              <span style={{ fontSize: 12.5, fontWeight: 600 }}>{c.title}</span>
              <span className="pill" style={{ background: 'var(--bg-soft)', color: 'var(--ink-3)', border: '1px solid var(--line-soft)' }}>{c.category}</span>
              <span className="pill" style={STATUS_PILL[c.status] ?? STATUS_PILL.Pending}>{STATUS_LABEL[c.status] ?? c.status}</span>
              <div style={{ flexGrow: 1 }} />
              {(c.status === 'PendingReview' || c.status === 'Draft') && (
                <>
                  <button className="pbtn" style={{ height: 24, fontSize: 11.5, padding: '0 10px' }} onClick={() => void act(() => post(`/api/clauses/${c.id}/approve`))}>审定通过</button>
                  <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => void act(() => post(`/api/clauses/${c.id}/reject`))}>驳回</button>
                </>
              )}
              {c.status === 'Active' && <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => setEditing(c)}>拟修改</button>}
            </div>
            <div style={{ fontSize: 12, lineHeight: 1.7, color: 'var(--ink-2)' }}>{c.text}</div>
            <div className="hint" style={{ marginTop: 4 }}>
              {c.approvedByName ? `审定 ${c.approvedByName}` : '未审定'}{c.effectiveDate ? ` · ${c.effectiveDate} 生效` : ''}{c.supersedesId ? ' · 替代旧版' : ''}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function ClauseEditPanel({ base, onDone, onError }: { base: ClauseRow | null; onDone: () => void; onError: (m: string | null) => void }) {
  const [category, setCategory] = useState(base?.category ?? '');
  const [code, setCode] = useState(base?.code ?? '');
  const [title, setTitle] = useState(base?.title ?? '');
  const [text, setText] = useState(base?.text ?? '');
  const [busy, setBusy] = useState(false);

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    onError(null);
    try {
      const body = { category: category.trim(), code: code.trim(), title: title.trim(), text: text.trim() };
      if (base) await post(`/api/clauses/${base.id}/revise`, body);
      else await post('/api/clauses', body);
      onDone();
    } catch (err) {
      onError(err instanceof ApiError ? err.message : '保存失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={save} className="card" style={{ padding: '12px 14px', marginBottom: 14 }}>
      <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 8 }}>
        {base ? `拟修改 ${base.code}（生成待审新版本，原文不动）` : '起草新条款（须审定通过才进入可选范围）'}
      </div>
      <div style={{ display: 'flex', gap: 8, marginBottom: 8 }}>
        <input style={{ ...fi, width: 100 }} placeholder="类目" value={category} onChange={(e) => setCategory(e.target.value)} />
        <input style={{ ...fi, width: 100 }} className="m" placeholder="编号" value={code} onChange={(e) => setCode(e.target.value)} />
        <input style={{ ...fi, flexGrow: 1 }} placeholder="标题" value={title} onChange={(e) => setTitle(e.target.value)} />
      </div>
      <textarea style={{ width: '100%', minHeight: 76, padding: '8px 10px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, lineHeight: 1.75, fontFamily: 'var(--font)', resize: 'vertical', marginBottom: 8 }}
        placeholder="条款正文" value={text} onChange={(e) => setText(e.target.value)} />
      <div style={{ display: 'flex', gap: 8 }}>
        <button type="submit" className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={busy || !category.trim() || !code.trim() || !title.trim() || !text.trim()}>
          {base ? '提交待审新版本' : '提交待审'}
        </button>
        <button type="button" className="gbtn" onClick={onDone}>取消</button>
      </div>
    </form>
  );
}
