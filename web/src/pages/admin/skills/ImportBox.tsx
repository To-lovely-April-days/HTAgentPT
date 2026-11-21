// 导入开源技能：选 SKILL.md 或技能文件夹的 zip。先检查许可证、格式、字数、工具名，再存成草稿（可选让模型初译成中文、压短）。
import { useState } from 'react';
import type { ReactNode } from 'react';
import { upload, ApiError } from '../../../lib/api';
import { Icon } from '../../../components/Icons';
import { useEscape } from '../quality/useEscape';
import type { ImportChecks } from './types';

function Row({ ok, warn, children }: { ok: boolean; warn?: boolean; children: ReactNode }) {
  return <div className="sk-chk"><span className={'sk-chk-i ' + (ok ? 'ok' : warn ? 'warn' : 'bad')}><Icon name={ok ? 'check' : 'x'} size={12} stroke={2.4} /></span><span>{children}</span></div>;
}

export function ImportBox({ categories, onClose, onDone }: { categories: string[]; onClose: () => void; onDone: (id: string) => void }) {
  const [file, setFile] = useState<File | null>(null);
  const [checks, setChecks] = useState<ImportChecks | null>(null);
  const [translate, setTranslate] = useState(true);
  const [category, setCategory] = useState(categories[0] ?? '其他');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  useEscape(onClose);

  const send = async (f: File, dryRun: boolean) => {
    const form = new FormData();
    form.append('file', f);
    form.append('dryRun', String(dryRun));
    form.append('translate', String(translate));
    form.append('category', category);
    return upload<{ checks: ImportChecks; saved: string | null }>('/api/skills/import', form);
  };
  const pick = async (f: File | undefined) => {
    if (!f) return;
    setFile(f); setChecks(null); setErr(null); setBusy(true);
    try { setChecks((await send(f, true)).checks); } catch (e) { setErr(e instanceof ApiError ? e.message : '读不了这个文件'); } finally { setBusy(false); }
  };
  const go = async () => {
    if (!file) return;
    setBusy(true); setErr(null);
    try { const r = await send(file, false); if (r.saved) onDone(r.saved); } catch (e) { setErr(e instanceof ApiError ? e.message : '没导入成'); } finally { setBusy(false); }
  };
  const c = checks;

  return (
    <div className="q-mask" role="dialog" aria-modal="true" aria-label="导入技能" onClick={onClose}>
      <div className="q-dlg" onClick={(e) => e.stopPropagation()}>
        <div className="q-dlg-hd"><b>导入开源技能</b><span className="hint">Agent Skills 标准的 SKILL.md，或整个技能文件夹打成的 zip</span>
          <button type="button" className="q-x" aria-label="关闭" onClick={onClose}><Icon name="x" size={14} /></button></div>
        <label className="sk-drop">
          <Icon name="upload" size={16} />
          <span>{file ? file.name : '选择 SKILL.md 或 .zip'}</span>
          <input type="file" accept=".md,.zip" onChange={(e) => void pick(e.target.files?.[0])} />
        </label>
        {busy && !c && <span className="hint">正在检查…</span>}
        {c && (
          <div className="sk-chks">
            <div className="sk-chk-h"><b>{c.name || '（没有 name）'}</b><span className="hint">{c.description}</span></div>
            <Row ok={c.licenseOk}>{c.licenseOk ? `许可证 ${c.license}：可以商用、可以改` : `许可证：${c.license ?? '没有声明'}——不能导入`}</Row>
            <Row ok={!!c.code && !c.exists}>{!c.code ? 'SKILL.md 里没有 name' : c.exists ? `已经有代号为 ${c.code} 的技能` : `代号 ${c.code}`}</Row>
            <Row ok={c.length <= c.maxBody} warn>{`正文约 ${c.length} 字${c.length > c.maxBody ? `，超过 ${c.maxBody}，需要压短（勾选下面的初译会一起压）` : ''}`}</Row>
            <Row ok={c.unknownTools.length === 0} warn>{c.unknownTools.length ? `提到了我们没有的工具：${c.unknownTools.join('、')}（初译时会换成我们的，换不了的删掉）` : '提到的工具我们都有'}</Row>
            {c.scripts.length > 0 && <Row ok={false} warn>{`附带 ${c.scripts.length} 个脚本（${c.scripts.slice(0, 3).join('、')}）：暂时不导入，需要的话装进代码沙箱`}</Row>}
            {c.references.length > 0 && <Row ok warn>{`附带 ${c.references.length} 份参考文档：没有一起导入，需要的话上传到知识库再挂上`}</Row>}
          </div>
        )}
        {c && (
          <div className="sk-imp-opt">
            <label className="q-check"><input type="checkbox" checked={translate} onChange={(e) => setTranslate(e.target.checked)} />
              <Icon name={translate ? 'boxOn' : 'boxOff'} size={15} />让模型初译成中文、压短、换成我们的工具名（存成草稿后人工校对）</label>
            <label className="q-dept">分类 <select value={category} onChange={(e) => setCategory(e.target.value)}>{categories.map((x) => <option key={x}>{x}</option>)}</select></label>
          </div>
        )}
        {err && <span className="q-err">{err}</span>}
        <div className="q-dlg-ft">
          <span className="hint" style={{ marginRight: 'auto' }}>没声明许可证的不让导入；导入后是草稿，审过才启用</span>
          <button type="button" className="gbtn" onClick={onClose}>取消</button>
          <button type="button" className="pbtn" disabled={busy || !c || !c.licenseOk || !c.code || c.exists} onClick={() => void go()}>{busy && c ? '正在导入…' : '导入为草稿'}</button>
        </div>
      </div>
    </div>
  );
}
