// 输入框里的「思考」：自动 / 快速 / 标准 / 深度。平时是「自动」不用管；选了别的变黑底，一眼看出这次改过。
// 只管这一个对话，开新对话回到「自动」。深度档要模型服务的上下文到 32K，没到之前是灰的。
import { useEffect, useRef, useState } from 'react';
import { Icon } from '../../components/Icons';

export type Level = 'auto' | 'fast' | 'standard' | 'deep';

export interface LevelsInfo { default: string; userChoice: boolean; deepAvailable: boolean; contextTokens: number }

const LEVEL_LABEL: Record<Level, string> = { auto: '自动', fast: '快速', standard: '标准', deep: '深度' };

const OPTIONS: { code: Level; icon: string; time: string; desc: string }[] = [
  { code: 'auto', icon: 'auto', time: '默认', desc: '简单问题直接答，选型、计算、对比这类先想一想再答。不知道选哪个就用它。' },
  { code: 'fast', icon: 'bolt', time: '约 3–8 秒', desc: '不思考，直接答。查参数、问联系方式、翻译一句话。' },
  { code: 'standard', icon: 'bulb', time: '约 10–25 秒', desc: '先想一想再答。大部分售前、技术问题。' },
  { code: 'deep', icon: 'layers', time: '约 30–90 秒', desc: '多想一会，多查几轮资料、多算几步。选型、计算、标准对比、写方案。' },
];

export function ThinkPicker({ value, onChange, deepOk }: { value: Level; onChange: (l: Level) => void; deepOk: boolean }) {
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const down = (e: MouseEvent) => { if (!box.current?.contains(e.target as Node)) setOpen(false); };
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', down);
    document.addEventListener('keydown', key);
    return () => { document.removeEventListener('mousedown', down); document.removeEventListener('keydown', key); };
  }, [open]);

  return (
    <div className="think-pick" ref={box}>
      <button type="button" className={'scope think-btn' + (value !== 'auto' ? ' on' : '')} aria-haspopup="listbox" aria-expanded={open}
        title="思考档位：答得快一点，还是想得细一点" onClick={() => setOpen(!open)}>
        <span className="scope-cap">思考</span><b>{LEVEL_LABEL[value]}</b><Icon name="down" size={13} />
      </button>
      {open && (
        <div className="think-pop" role="listbox" aria-label="思考档位">
          {OPTIONS.map((o) => {
            const off = o.code === 'deep' && !deepOk;
            return (
              <button key={o.code} type="button" role="option" aria-selected={value === o.code} disabled={off}
                className={'think-opt' + (value === o.code ? ' sel' : '')} onClick={() => { onChange(o.code); setOpen(false); }}>
                <span className="think-ic"><Icon name={o.icon} size={15} /></span>
                <span className="think-tx">
                  <span className="think-t">{LEVEL_LABEL[o.code]}<i>{o.time}</i>{value === o.code && <Icon name="check" size={14} stroke={2.4} style={{ marginLeft: 'auto' }} />}</span>
                  <span className="think-d">{off ? '要先把模型服务的上下文调到 32K，现在还不能选。' : o.desc}</span>
                </span>
              </button>
            );
          })}
          <div className="think-foot">只管这一个对话；开新对话回到「自动」。</div>
        </div>
      )}
    </div>
  );
}
