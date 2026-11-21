// 方案生成对话里的输出件：两张建议卡与一张汇总卡。
// 共同的口径是信息分层——先给能一眼扫完的概览，细节点开再看，
// 而不是把几十条内容一次性铺满屏幕。
import React, { useMemo, useState } from 'react';
import type { GenAdvice, GenChatPayload, GenEquipmentCard, GenEquipmentItem, GenSuggestion } from '../../lib/types';
import { Icon } from '../../components/Icons';

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
          <span className="advc-tri"><Icon name="play" size={9} style={{ display: 'block' }} /></span>
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

/** 汇总卡：几十项平铺过去没人看得下来。先给「已填 / 未填」的数，
    再按章节分组，未填的标出来并且可以一键只看未填——汇总是用来查漏的。 */
export function SummaryCard({ summary }: { summary: NonNullable<GenChatPayload['summary']> }) {
  const [onlyBlank, setOnlyBlank] = useState(false);
  const { filled, blank } = useMemo(() => {
    const all = summary.flatMap((s) => s.items);
    return { filled: all.filter((i) => i.value).length, blank: all.filter((i) => !i.value).length };
  }, [summary]);
  const sections = summary
    .map((sec) => ({ ...sec, items: onlyBlank ? sec.items.filter((i) => !i.value) : sec.items }))
    .filter((sec) => sec.items.length > 0);

  return (
    <div className="advc" style={{ borderColor: 'var(--line)' }}>
      <div className="advc-head" style={{ background: 'var(--bg-soft)' }}>
        <span className="advc-cap" style={{ color: 'var(--ink-2)' }}>汇总</span>
        <span className="pill pill-ok">已填 {filled}</span>
        {blank > 0 && <span className="pill pill-int">未填 {blank}</span>}
        {blank > 0 && (
          <button type="button" className="gbtn" style={{ height: 21, fontSize: 11, padding: '0 8px', marginLeft: 'auto' }}
            onClick={() => setOnlyBlank(!onlyBlank)}>{onlyBlank ? '看全部' : '只看未填'}</button>
        )}
      </div>
      {sections.map((sec) => (
        <div key={sec.section}>
          <div className="sum-sec">
            {sec.section}
            <span className="sum-cnt">{sec.items.filter((i) => i.value).length}/{sec.items.length}</span>
          </div>
          {sec.items.map((it) => (
            <div key={it.name} className={`sum-row${it.value ? '' : ' blank'}`}>
              <span className="sum-name">{it.name}</span>
              <span className="sum-val">{it.value ?? '未填'}</span>
            </div>
          ))}
        </div>
      ))}
      {sections.length === 0 && <div className="sum-row"><span className="hint">没有未填的项。</span></div>}
    </div>
  );
}

/** 设备卡（技术协议这类按设备展开的模板）：定装置里有哪些设备。
 *  第三章每台设备一张参数表、第四章每类部件一行，都按这张清单展开；
 *  参数表配套的第四章行由系统自动配，这里只列出来给人看，不让单独改。 */
export function EquipmentCard({ data, active, onTurn }: {
  data: GenEquipmentCard; active: boolean; onTurn: (b: Record<string, unknown>) => Promise<void>;
}) {
  const [items, setItems] = useState<GenEquipmentItem[]>(data.items);
  const [pick, setPick] = useState('');
  const [busy, setBusy] = useState(false);
  const byBlock = useMemo(() => new Map(data.catalog.map((c) => [c.block, c])), [data.catalog]);
  const main = items.filter((i) => !i.linkedTo);
  const linked = items.filter((i) => i.linkedTo);
  const tables = data.catalog.filter((c) => c.kind === 'table');
  const parts = data.catalog.filter((c) => c.kind === 'part');

  const update = (idx: number, patch: Partial<GenEquipmentItem>) =>
    setItems((list) => list.map((it) => (it === main[idx] ? { ...it, ...patch } : it)));
  const remove = (idx: number) => {
    const target = main[idx];
    // 删参数表连带删它配套的第四章行
    setItems((list) => list.filter((it) => it !== target && !(target.id && it.linkedTo === target.id)));
  };
  const add = () => {
    const c = byBlock.get(pick);
    if (!c) return;
    setItems((list) => [...list, { id: null, block: c.block, tagNo: c.tagPrefix, name: c.name, qty: 1, linkedTo: null }]);
    setPick('');
  };
  const submit = async (list: GenEquipmentItem[]) => {
    setBusy(true);
    try { await onTurn({ equipment: list }); } finally { setBusy(false); }
  };
  const fi: React.CSSProperties = { height: 24, padding: '0 6px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12, fontFamily: 'var(--font)' };

  return (
    <div className="advc" style={{ borderColor: 'var(--line-strong)' }}>
      <div className="advc-head">
        <span className="advc-cap">设备清单</span>
        <span className="pill pill-neutral">{main.length} 项</span>
        {!data.decided && <span className="pill pill-int">待确定</span>}
        <span className="advc-note">第三章参数表、第四章部件清单按此逐台展开</span>
      </div>
      <div style={{ padding: '8px 12px', display: 'flex', flexDirection: 'column', gap: 6 }}>
        {main.length === 0 && <div className="hint">还没有设备。从下面的目录里添加，或在对话里直接说。</div>}
        {main.map((it, idx) => (
          <div key={it.id ?? `new-${idx}`} style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
            <span className={`pill ${byBlock.get(it.block)?.kind === 'table' ? 'pill-int' : 'pill-neutral'}`}
              style={{ minWidth: 92, justifyContent: 'center' }}>{byBlock.get(it.block)?.name ?? it.block}</span>
            {active ? (
              <>
                <input style={{ ...fi, width: 96 }} placeholder="位号" value={it.tagNo ?? ''}
                  onChange={(e) => update(idx, { tagNo: e.target.value || null })} />
                <input style={{ ...fi, width: 150 }} placeholder="名称" value={it.name ?? ''}
                  onChange={(e) => update(idx, { name: e.target.value || null })} />
                <input style={{ ...fi, width: 52 }} type="number" min={1} value={it.qty}
                  onChange={(e) => update(idx, { qty: Math.max(1, Number(e.target.value) || 1) })} />
                <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} onClick={() => remove(idx)}>删除</button>
              </>
            ) : (
              <span style={{ fontSize: 12.5 }}>{[it.tagNo, it.name].filter(Boolean).join(' ')}{it.qty > 1 ? ` ×${it.qty}` : ''}</span>
            )}
          </div>
        ))}
        {linked.length > 0 && (
          <div className="hint">
            第四章已自动配：{linked.map((l) => `${byBlock.get(l.block)?.title ?? l.block}${l.tagNo ? `（${l.tagNo}）` : ''}`).join('、')}
          </div>
        )}
        {active && (
          <div style={{ display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap', marginTop: 2 }}>
            <select style={{ ...fi, minWidth: 220 }} value={pick} onChange={(e) => setPick(e.target.value)}>
              <option value="">添加设备…</option>
              <optgroup label="第三章 设备参数表">
                {tables.map((c) => <option key={c.block} value={c.block}>{c.title}</option>)}
              </optgroup>
              <optgroup label="第四章 部件清单">
                {parts.map((c) => <option key={c.block} value={c.block}>{c.title}</option>)}
              </optgroup>
            </select>
            <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} disabled={!pick} onClick={add}>添加</button>
          </div>
        )}
      </div>
      {active && (
        <div className="advc-foot">
          <button className="pbtn" style={{ height: 24, fontSize: 11.5 }} disabled={busy}
            onClick={() => void submit(items)}>确认设备清单</button>
          {!data.decided && main.length === 0 && (
            <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} disabled={busy}
              onClick={() => void submit([])}>不选设备</button>
          )}
        </div>
      )}
    </div>
  );
}
