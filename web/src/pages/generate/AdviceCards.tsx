// 方案生成对话里的两张建议卡。一条建议默认只占一行，点开才看理由/风险/出处——
// 信息分层而不是一次性全泼出来：工程师先扫一遍哪几项要动，再决定读哪条的为什么。
import React, { useState } from 'react';
import type { GenAdvice, GenSuggestion } from '../../lib/types';

/** 一条建议一行：名称 + 现值→建议值 + 状态，点开才展开理由/风险/出处。
    默认收起是有意的——工程师先扫一眼哪几项要动，再决定读哪条的为什么，
    而不是被整屏文字糊住。工况建议与资料依据共用这一行，差别在配色与展开区内容。 */
function AdviceRow({ name, current, value, tone, pill, high, detail, active, onAdopt }: {
  name: string; current?: string | null; value: string; tone: string;
  pill: { label: string; cls: string } | null; high?: boolean;
  detail: React.ReactNode; active: boolean; onAdopt: () => void;
}) {
  const [open, setOpen] = useState(false);
  const changed = !!current && current.trim() !== value.trim();
  return (
    <>
      <div className={`advc-row${open ? ' open' : ''}`}>
        <span className={`advc-bar${high ? ' on' : ''}`} />
        <button type="button" className="advc-tog" aria-expanded={open} onClick={() => setOpen(!open)}>
          <span className="advc-tri">▶</span>
          <span className="advc-name" style={high ? { color: tone, fontWeight: 600 } : undefined}>{name}</span>
          <span className="advc-val">
            {changed && <><span className="advc-old">{current}</span> → </>}
            <span style={{ fontWeight: changed ? 600 : 400 }}>{value}</span>
          </span>
        </button>
        <span className="advc-act">
          {pill && <span className={`pill ${pill.cls}`}>{pill.label}</span>}
          {active && <button className="gbtn" style={{ height: 22, fontSize: 11, padding: '0 9px' }}
            onClick={onAdopt}>采纳</button>}
        </span>
      </div>
      {open && <div className="advc-body">{detail}</div>}
    </>
  );
}

const ADVICE_KIND: Record<string, { label: string; cls: string }> = {
  change: { label: '要改', cls: 'pill-conf' },
  fill: { label: '待补', cls: 'pill-int' },
  keep: { label: '一致', cls: 'pill-ok' },
};

/** 工况建议卡：模型按工艺常识判断，没有文档依据。关系到安全的标红条并排在前面，
    与当前填写冲突的标「要改」——先让人看见哪几项动了，再看为什么。 */
export function AdviceCard({ advices, active, onTurn }: {
  advices: GenAdvice[]; active: boolean; onTurn: (b: Record<string, unknown>) => Promise<void>;
}) {
  const high = advices.filter((a) => a.level === 'high').length;
  const change = advices.filter((a) => a.kind === 'change').length;
  return (
    <div className="advc" style={{ borderColor: 'var(--cls-int-line)' }}>
      <div className="advc-head" style={{ background: 'var(--cls-int-bg)' }}>
        <span className="advc-cap" style={{ color: 'var(--cls-int)' }}>工况建议</span>
        <span className="pill pill-neutral">{advices.length} 项</span>
        {high > 0 && <span className="pill pill-conf">{high} 项关系到安全</span>}
        {change > 0 && <span className="pill pill-int">{change} 项要改</span>}
        <span className="advc-note">按工艺常识判断 · 无文档依据，请工程师确认</span>
      </div>
      {advices.map((a) => (
        <AdviceRow key={a.tag} name={a.name} current={a.current} value={a.value}
          tone="var(--cls-conf)" high={a.level === 'high'} pill={ADVICE_KIND[a.kind] ?? null}
          active={active} onAdopt={() => void onTurn({ adoptTags: [a.tag] })}
          detail={<>
            {a.reason}
            {a.risk && <div className="advc-risk">不这么做：{a.risk}</div>}
          </>} />
      ))}
      {active && advices.length > 1 && (
        <div className="advc-foot">
          <button className="pbtn" style={{ height: 24, fontSize: 11.5 }}
            onClick={() => void onTurn({ adoptTags: advices.map((a) => a.tag) })}>全部采纳</button>
          {change > 0 && <span className="hint" style={{ alignSelf: 'center' }}>会覆盖已填的 {change} 项</span>}
        </div>
      )}
    </div>
  );
}

/** 资料依据卡：检索命中知识库或基准文档，出处逐条可追溯（FR-5.9/5.10）。 */
export function EvidenceCard({ suggestions, active, onTurn }: {
  suggestions: GenSuggestion[]; active: boolean; onTurn: (b: Record<string, unknown>) => Promise<void>;
}) {
  return (
    <div className="advc" style={{ borderColor: 'var(--pending-line)' }}>
      <div className="advc-head" style={{ background: 'var(--pending-bg)' }}>
        <span className="advc-cap" style={{ color: 'var(--pending)' }}>资料依据</span>
        <span className="pill pill-neutral">{suggestions.length} 项</span>
        <span className="advc-note">检索命中，出处可追溯 · 采纳后才落表</span>
      </div>
      {suggestions.map((s) => (
        <AdviceRow key={s.tag} name={s.name} value={s.value ?? ''} tone="var(--pending)"
          pill={{ label: `${s.evidence.length} 条依据`, cls: 'pill-pending' }}
          active={active} onAdopt={() => void onTurn({ adoptTags: [s.tag] })}
          detail={<>
            {s.note}
            {s.evidence.map((e, i) => (
              <div key={i} className="advc-src">
                <span className="m" style={{ color: 'var(--pending)' }}>[{i + 1}]</span> {e.sourceTitle}
                {e.section ? ` › ${e.section}` : ''}{e.pageNo != null ? ` · 第 ${e.pageNo} 页` : ''}
              </div>
            ))}
          </>} />
      ))}
      {active && suggestions.length > 1 && (
        <div className="advc-foot">
          <button className="pbtn" style={{ height: 24, fontSize: 11.5 }}
            onClick={() => void onTurn({ adoptTags: suggestions.map((s) => s.tag) })}>全部采纳</button>
        </div>
      )}
    </div>
  );
}
