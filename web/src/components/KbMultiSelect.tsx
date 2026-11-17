// 资料库多选：下拉里每个库前面一个复选框，选中的以小标签排在框里。
// 不能选的库（公开库要走审定发布、共享库总部下发）灰着显示并写明原因——不藏起来，免得人以为没这个库。
import { useEffect, useRef, useState } from 'react';

export interface KbChoice { id: string; name: string; note?: string | null; count?: string; disabled?: string | null }

export function KbMultiSelect({ options, value, onChange, placeholder = '选择资料库' }: {
  options: KbChoice[]; value: string[]; onChange: (ids: string[]) => void; placeholder?: string;
}) {
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (!box.current?.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', esc);
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', esc); };
  }, [open]);

  const toggle = (id: string) => onChange(value.includes(id) ? value.filter((x) => x !== id) : [...value, id]);
  const picked = value.map((id) => options.find((o) => o.id === id)).filter(Boolean) as KbChoice[];

  return (
    <div className="kbms" ref={box}>
      <div className={'kbms-box' + (open ? ' open' : '')} role="combobox" aria-expanded={open} aria-haspopup="listbox" tabIndex={0}
        onClick={() => setOpen(!open)} onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setOpen(!open); } }}>
        {picked.length === 0 && <span className="kbms-ph">{placeholder}</span>}
        {picked.map((o) => (
          <span key={o.id} className="kbms-tag">
            {o.name}
            <button type="button" aria-label={`去掉 ${o.name}`} onClick={(e) => { e.stopPropagation(); toggle(o.id); }}>✕</button>
          </span>
        ))}
        <span className="kbms-caret" aria-hidden="true">{open ? '▴' : '▾'}</span>
      </div>
      {open && (
        <div className="kbms-pop" role="listbox" aria-multiselectable="true">
          {options.map((o) => {
            const on = value.includes(o.id);
            return (
              <button key={o.id} type="button" role="option" aria-selected={on} disabled={!!o.disabled}
                className={'kbms-opt' + (on ? ' on' : '')} onClick={() => toggle(o.id)}>
                <span className="kbms-cb" aria-hidden="true">{on ? '✓' : ''}</span>
                <span className="kbms-t">
                  <b>{o.name}</b>
                  {(o.disabled || o.note) && <span>{o.disabled ?? o.note}</span>}
                </span>
                {o.count && <span className="kbms-n">{o.count}</span>}
              </button>
            );
          })}
          {options.length === 0 && <div className="kbms-empty">还没有可选的资料库</div>}
        </div>
      )}
    </div>
  );
}
