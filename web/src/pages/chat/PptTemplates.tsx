// PPT 模板库在对话里的三处：输入框「＋ → PPT 模板」的选择窗、助手做的 PPT 文件卡上的每页预览、「换个风格」。
// 图片都要带登录凭证取（fetchBlobUrl），取过的记在这里，同一张不取第二次。
import { useEffect, useMemo, useState } from 'react';
import { fetchBlobUrl } from '../../lib/api';
import { coverOf, usePptTemplates } from './pptUtil';
import type { PptTemplateRow } from '../../lib/types';
import { Icon } from '../../components/Icons';

const cache = new Map<string, Promise<string>>();

function useImg(path: string | null): string | null | false {
  const [src, setSrc] = useState<{ path: string; url: string | false } | null>(null);
  useEffect(() => {
    if (!path) return;
    let alive = true;
    let p = cache.get(path);
    if (!p) { p = fetchBlobUrl(path); cache.set(path, p); p.catch(() => cache.delete(path)); }
    p.then((u) => { if (alive) setSrc({ path, url: u }); }).catch(() => { if (alive) setSrc({ path, url: false }); });
    return () => { alive = false; };
  }, [path]);
  if (!path) return null;
  return src && src.path === path ? src.url : null;
}

export function Thumb({ path, alt, className }: { path: string; alt: string; className?: string }) {
  const src = useImg(path);
  return (
    <span className={'ppt-th' + (className ? ' ' + className : '')}>
      {src ? <img src={src} alt={alt} loading="lazy" /> : <span className="hint">{src === false ? '图取不到' : ''}</span>}
    </span>
  );
}

/** 「＋ → PPT 模板」或在插件里选了做 PPT 的技能时弹出：看着封面挑；点小眼睛看这套的全部页面；也可以不选，让助手按场景挑。 */
export function PptPicker({ value, onPick, onClose, skillName }: {
  value: PptTemplateRow | null; onPick: (t: PptTemplateRow | null) => void; onClose: () => void;
  /** 从技能进来的：标题下说一句「已选技能，模板可选可不选」 */
  skillName?: string;
}) {
  const q = usePptTemplates();
  const [tag, setTag] = useState('全部');
  const [peek, setPeek] = useState<PptTemplateRow | null>(null);
  useEffect(() => {
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', esc);
    return () => window.removeEventListener('keydown', esc);
  }, [onClose]);
  const list = useMemo(() => q.data ?? [], [q.data]);
  const tags = useMemo(() => ['全部', ...Array.from(new Set(list.flatMap((t) => t.tags)))], [list]);
  const shown = tag === '全部' ? list : list.filter((t) => t.tags.includes(tag));
  return (
    <div className="modal-mask" onClick={onClose}>
      <div className="modal-box ppt-pick" role="dialog" aria-label="选一套 PPT 模板" onClick={(e) => e.stopPropagation()}>
        <div className="ppt-pick-hd">
          <b>选一套 PPT 模板<span className="beta-tag" title="试用版：还在完善，做出来的 PPT 请检查后再用">Beta</span></b>
          <span className="hint">{skillName ? `已选「${skillName}」· 模板可以不选，助手会按内容挑` : `${list.length} 套 · 都是能商用的开源模板`}</span>
          <button type="button" className="gbtn" style={{ marginLeft: 'auto' }} onClick={() => { onPick(null); onClose(); }}>不选，让助手挑</button>
          <button type="button" className="ppt-x" aria-label="关闭" onClick={onClose}><Icon name="x" size={16} /></button>
        </div>
        <div className="ppt-flt" role="tablist">
          {tags.map((t) => <button key={t} type="button" role="tab" aria-selected={tag === t} className={'q-chip' + (tag === t ? ' on' : '')} onClick={() => setTag(t)}>{t}</button>)}
        </div>
        {q.isLoading && <div className="hint" style={{ padding: 20 }}>取模板中…</div>}
        {q.isError && <div className="hint" style={{ padding: 20 }}>模板库取不到，稍后再试</div>}
        {!q.isLoading && list.length === 0 && !q.isError && <div className="hint" style={{ padding: 20 }}>模板库还是空的：管理员在后台「PPT 模板」里导入后就能选</div>}
        {peek ? (
          <div className="ppt-peek">
            <div className="ppt-peek-hd">
              <button type="button" className="pm-back" onClick={() => setPeek(null)}><Icon name="left" size={14} />全部模板</button>
              <b>{peek.name}</b><span className="hint">{peek.tags.join(' · ')} · {peek.slides} 页 · 认出的页面：{peek.types.join('、')}</span>
              <button type="button" className="pbtn" style={{ marginLeft: 'auto' }} onClick={() => { onPick(peek); onClose(); }}>用这套</button>
            </div>
            <div className="ppt-peek-g">
              {Array.from({ length: peek.previews }, (_, i) => <Thumb key={i} path={`/api/ppt-templates/${peek.id}/preview/${i + 1}`} alt={`第 ${i + 1} 页`} />)}
            </div>
          </div>
        ) : (
          <div className="ppt-grid">
            {shown.map((t) => (
              <div key={t.id} className={'ppt-tile' + (value?.id === t.id ? ' on' : '')}>
                <button type="button" className="ppt-tile-b" onClick={() => { onPick(t); onClose(); }} aria-pressed={value?.id === t.id}>
                  <Thumb path={coverOf(t)} alt={`${t.name} 封面`} />
                  <span className="ppt-tile-n">{t.name}{value?.id === t.id && <Icon name="check" size={13} stroke={2.4} />}</span>
                  <span className="hint">{t.tags.join(' · ')} · {t.slides} 页</span>
                </button>
                <button type="button" className="ppt-eye" aria-label={`看「${t.name}」的全部页面`} title="看全部页面" onClick={() => setPeek(t)}><Icon name="eye" size={14} /></button>
              </div>
            ))}
          </div>
        )}
        <div className="hint ppt-pick-ft">选了以后这次对话做 PPT 都用它；做完不满意可以在文件卡上点「换个风格」。模板来自 SlidesCarnival 等开源模板站（CC BY 4.0），做出来的 PPT 最后一页带一行署名。</div>
      </div>
    </div>
  );
}

/** 助手做的 PPT：文件卡上一排每页的图，点开看大图。 */
export function PptPages({ pages, title }: { pages: string[]; title: string }) {
  const [big, setBig] = useState<number | null>(null);
  const shown = pages.slice(0, 8);
  useEffect(() => {
    if (big === null) return;
    const key = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setBig(null);
      if (e.key === 'ArrowRight') setBig((b) => (b === null ? b : Math.min(pages.length - 1, b + 1)));
      if (e.key === 'ArrowLeft') setBig((b) => (b === null ? b : Math.max(0, b - 1)));
    };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [big, pages.length]);
  return (
    <>
      <div className="ppt-strip">
        {shown.map((p, i) => {
          const rest = i === 7 && pages.length > 8 ? pages.length - 7 : 0;   // 第 8 格盖一层「还有几页」
          return (
            <button key={p} type="button" className="ppt-pg" onClick={() => setBig(i)} aria-label={rest ? `看第 8 页及后面 ${rest - 1} 页` : `看第 ${i + 1} 页`}>
              <span className="ppt-pg-i"><Thumb path={p} alt={`第 ${i + 1} 页`} />{rest > 0 && <span className="ppt-more">+{rest} 页</span>}</span>
              <span>{String(i + 1).padStart(2, '0')}</span>
            </button>
          );
        })}
      </div>
      {big !== null && (
        <div className="modal-mask" onClick={() => setBig(null)}>
          <div className="ppt-big" role="dialog" aria-label={`${title} 第 ${big + 1} 页`} onClick={(e) => e.stopPropagation()}>
            <Thumb path={pages[big]} alt={`第 ${big + 1} 页`} />
            <div className="ppt-big-ft">
              <button type="button" className="gbtn" disabled={big === 0} onClick={() => setBig(big - 1)}><Icon name="left" size={13} />上一页</button>
              <span className="hint">{big + 1} / {pages.length}</span>
              <button type="button" className="gbtn" disabled={big === pages.length - 1} onClick={() => setBig(big + 1)}>下一页<Icon name="right" size={13} /></button>
              <button type="button" className="ppt-x" style={{ marginLeft: 'auto' }} aria-label="关闭" onClick={() => setBig(null)}><Icon name="x" size={16} /></button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

/** 「换个风格」：给 3 套和现在风格不一样的模板，点了就让助手用它重做（内容不变）。 */
export function Restyle({ current, fileName, onSend }: { current?: { id: string; name: string }; fileName: string; onSend: (text: string) => void }) {
  const [open, setOpen] = useState(false);
  const q = usePptTemplates(open);
  const now = q.data?.find((t) => t.id === current?.id);
  const picks = useMemo(() => {
    const rest = (q.data ?? []).filter((t) => t.id !== current?.id);
    const out: PptTemplateRow[] = [];
    const seen = new Set(now?.tags ?? []);
    for (const t of rest) if (out.length < 3 && !t.tags.some((x) => seen.has(x))) { out.push(t); t.tags.forEach((x) => seen.add(x)); }
    for (const t of rest) if (out.length < 3 && !out.includes(t)) out.push(t);
    return out;
  }, [q.data, current?.id, now?.tags]);
  const go = (t: PptTemplateRow) => { setOpen(false); onSend(`换个风格：用「${t.name}」模板重做《${fileName}》，内容不变`); };
  return (
    <span className="ppt-rs">
      <button type="button" className="gbtn" aria-expanded={open} onClick={() => setOpen(!open)}><Icon name="refresh" size={13} />换个风格</button>
      {open && (
        <div className="ppt-rs-pop" role="menu">
          <div className="hint">内容、页数、引用都不变，只换模板</div>
          {q.isLoading && <div className="hint">取模板中…</div>}
          <div className="ppt-rs-g">
            {picks.map((t) => (
              <button key={t.id} type="button" role="menuitem" className="ppt-rs-i" onClick={() => go(t)}>
                <Thumb path={coverOf(t)} alt={`${t.name} 封面`} />
                <span>{t.name}</span>
              </button>
            ))}
          </div>
        </div>
      )}
    </span>
  );
}
