// 行业专用组件：设备数据表、技术偏离表、操作窗口、故障诊断树、风险矩阵、BOM 结构、物性数据、规范条款、选型匹配、跟进事项、交付计划。
import { useState } from 'react';
import type { ReactNode } from 'react';
import type { D } from './util';
import { list, n, s, strs } from './util';
import { Refs } from './Refs';
import { Icon } from '../../../components/Icons';

// ── 设备数据表 ───────────────────────────────────
export function Datasheet({ d }: { d: D }) {
  const head = list(d.header);
  return (
    <div className="blk-ds">
      {head.length > 0 && (
        <div className="ds-head">
          {head.map((h, i) => <div key={i}><span>{s(h.label)}</span><b>{s(h.value)}</b></div>)}
        </div>
      )}
      <div className="ds-secs">
        {list(d.sections).map((sec, i) => (
          <table key={i} className="dc-table compact ds-sec">
            <thead><tr><th>{s(sec.title)}</th><th className="num">{s(sec.col_a) || '设计'}</th><th className="num">{s(sec.col_b) || '操作'}</th><th>单位</th><th /></tr></thead>
            <tbody>
              {list(sec.rows).map((r, j) => (
                <tr key={j}>
                  <td>{s(r.name)}</td>
                  <td className="num strong">{s(r.a) || <span className="muted">—</span>}</td>
                  <td className="num">{s(r.b) || <span className="muted">—</span>}</td>
                  <td className="muted">{s(r.unit)}</td>
                  <td className="c-ref"><Refs v={r.ref} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        ))}
      </div>
    </div>
  );
}

// ── 技术偏离表 ───────────────────────────────────
const DEV: Record<string, string> = { 无偏离: 'ok', 正偏离: 'pos', 负偏离: 'neg' };
export function Deviation({ d }: { d: D }) {
  const items = list(d.items);
  const count = (t: string) => items.filter((x) => s(x.type) === t).length;
  return (
    <div>
      <div className="blk-chips">
        {Object.keys(DEV).map((t) => <span key={t} className={'dev-pill ' + DEV[t]}>{t} {count(t)}</span>)}
      </div>
      <div className="dc-scroll">
        <table className="dc-table">
          <thead><tr><th>条款</th><th>客户要求</th><th>我方响应</th><th>偏离</th></tr></thead>
          <tbody>
            {items.map((x, i) => (
              <tr key={i} className={s(x.type) === '负偏离' ? 'neg-row' : ''}>
                <td className="mono">{s(x.clause)}</td>
                <td>{s(x.requirement)}</td>
                <td><span className="cell-main" style={{ fontWeight: 400 }}>{s(x.response)}</span>{s(x.note) && <span className="cell-sub">{s(x.note)}</span>}</td>
                <td><span className={'dev-pill ' + (DEV[s(x.type)] ?? 'ok')}>{s(x.type) || '—'}</span></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {count('负偏离') > 0 && <div className="blk-foot-warn">有 {count('负偏离')} 项负偏离，投标前要和客户澄清。</div>}
    </div>
  );
}

// ── 操作窗口 ─────────────────────────────────────
export function OpWindow({ d }: { d: D }) {
  return (
    <div className="blk-window">
      {list(d.items).map((w, i) => {
        const normal = Array.isArray(w.normal) ? (w.normal as unknown[]).map(n) : [];
        const alarm = Array.isArray(w.alarm) ? (w.alarm as unknown[]).map(n) : [];
        const design = n(w.design), ask = n(w.ask);
        const max = n(w.max) ?? Math.max(design ?? 0, ask ?? 0, ...(alarm.filter((x): x is number => x != null)), ...(normal.filter((x): x is number => x != null))) * 1.15;
        const pct = (v: number | null) => `${Math.max(0, Math.min(100, ((v ?? 0) / (max || 1)) * 100))}%`;
        const width = (a: number | null, b: number | null) => `${Math.max(0, Math.min(100, (((b ?? 0) - (a ?? 0)) / (max || 1)) * 100))}%`;
        // 客户要求落在哪个区：系统自己判断，不看模型怎么说
        const zone = ask == null ? null : design != null && ask > design ? 'bad' : normal[1] != null && ask > (normal[1] ?? 0) ? 'warn' : 'ok';
        const unit = s(w.unit);
        return (
          <div key={i} className="win">
            <span className="win-hd"><b>{s(w.name)}{unit ? `（${unit}）` : ''}</b>
              {ask != null && <span className={'win-v ' + zone}>要求 {s(w.ask)} {unit} · {s(w.verdict) || (zone === 'ok' ? '在正常范围内' : zone === 'warn' ? '超出正常范围' : '超过设计值')}</span>}</span>
            <div className="win-bar">
              {normal.length === 2 && <i className="z-ok" style={{ left: pct(normal[0]), width: width(normal[0], normal[1]) }} />}
              {alarm.length === 2 && <i className="z-warn" style={{ left: pct(alarm[0]), width: width(alarm[0], alarm[1]) }} />}
              {design != null && <i className="z-design" style={{ left: pct(design) }} />}
              {ask != null && <i className={'z-ask ' + zone} style={{ left: pct(ask) }} />}
            </div>
            <div className="win-lab tnum">
              {normal.length === 2 && <span style={{ left: pct(normal[0]) }}>正常 {normal[0]}–{normal[1]}</span>}
              {alarm.length === 2 && <span className="warn" style={{ left: pct(alarm[0]) }}>报警 {alarm[0]}</span>}
              {design != null && <span className="bad" style={{ left: pct(design), transform: 'translateX(-50%)' }}>设计 {design}</span>}
            </div>
          </div>
        );
      })}
      <div className="blk-legend">
        <span><i style={{ background: '#cfe6d9' }} />正常操作</span><span><i style={{ background: '#f5e2be' }} />报警区</span>
        <span><i style={{ background: '#a00d25', width: 2 }} />设计值</span><span>▼ 要求值</span>
      </div>
      {s(d.conclusion) && <div className="blk-callout warn compact"><span className="sym" aria-hidden="true">!</span><span className="body"><b>结论</b><span>{s(d.conclusion)}</span></span></div>}
    </div>
  );
}

// ── 故障诊断树 ───────────────────────────────────
const LIKE: Record<string, [string, string]> = { 高: ['85%', '#a00d25'], 中: ['55%', '#b4690e'], 低: ['25%', '#6b6b6b'] };
export function FaultTree({ d }: { d: D }) {
  return (
    <div className="blk-ftree">
      <div className="root">现象：{s(d.symptom)}</div>
      {list(d.causes).map((c, i) => {
        const [w, col] = LIKE[s(c.likelihood)] ?? LIKE.中;
        return (
          <div key={i} className="branch">
            <div className="node">
              <span className="hd"><b>{s(c.cause)}</b>
                <span className="like">可能性 <span className="bar"><i style={{ width: w, background: col }} /></span>{s(c.likelihood)}</span></span>
              {s(c.check) && <span><b className="k">查：</b>{s(c.check)}</span>}
              {s(c.fix) && <span><b className="k">处理：</b>{s(c.fix)}</span>}
              {s(c.cases) && <span className="cases">{s(c.cases)}</span>}
            </div>
          </div>
        );
      })}
    </div>
  );
}

// ── 风险矩阵 ─────────────────────────────────────
const riskColor = (sc: number) => (sc >= 15 ? '#f2b8bf' : sc >= 8 ? '#f5d9a8' : sc >= 4 ? '#f3ebc6' : '#ddefe4');
export function RiskMatrix({ d }: { d: D }) {
  const items = list(d.items).map((x, i) => ({ id: s(x.id) || `R${i + 1}`, l: Math.min(5, Math.max(1, n(x.likelihood) ?? 1)), sv: Math.min(5, Math.max(1, n(x.severity) ?? 1)), t: s(x.title), m: s(x.measure) }));
  const cells = [];
  for (let l = 5; l >= 1; l--) for (let sv = 1; sv <= 5; sv++) cells.push({ l, sv, ids: items.filter((x) => x.l === l && x.sv === sv).map((x) => x.id) });
  return (
    <div className="blk-risk">
      <div className="grid" role="img" aria-label="风险矩阵：纵轴可能性，横轴严重程度">
        <span className="ylab">可能性 →</span>
        {cells.map((c, i) => <span key={i} className="cell" style={{ background: riskColor(c.l * c.sv) }}>{c.ids.join(' ')}</span>)}
        <span />
        <span className="xlab">严重程度 →</span>
      </div>
      <div className="list">
        {[...items].sort((a, b) => b.l * b.sv - a.l * a.sv).map((x) => (
          <div key={x.id} className="row">
            <span className="id" style={{ background: riskColor(x.l * x.sv) }}>{x.id}</span>
            <span><b>{x.t}</b><span className="muted tnum">　可能性 {x.l} × 严重 {x.sv} = {x.l * x.sv}</span>{x.m && <><br /><span className="m">措施：{x.m}</span></>}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

// ── BOM 结构（可展开收起）────────────────────────
export function Bom({ d }: { d: D }) {
  const items = list(d.items).map((x) => ({ level: Math.max(0, n(x.level) ?? 0), name: s(x.name), qty: s(x.qty), material: s(x.material), drawing: s(x.drawing) }));
  const [closed, setClosed] = useState<number[]>([]);
  const hasKids = (i: number) => i + 1 < items.length && items[i + 1].level > items[i].level;
  const hidden = (i: number) => {
    // 往上找祖先：任何一个收起了就隐藏
    let lvl = items[i].level;
    for (let j = i - 1; j >= 0 && lvl > 0; j--) if (items[j].level < lvl) { if (closed.includes(j)) return true; lvl = items[j].level; }
    return false;
  };
  return (
    <div className="dc-scroll">
      <table className="dc-table compact blk-bom">
        <thead><tr><th>名称</th><th className="num">数量</th><th>材质</th><th>图号</th></tr></thead>
        <tbody>
          {items.map((x, i) => hidden(i) ? null : (
            <tr key={i} className={x.level === 0 ? 'top' : ''}>
              <td style={{ paddingLeft: 10 + x.level * 18 }}>
                {hasKids(i)
                  ? <button type="button" className="tw" aria-expanded={!closed.includes(i)} aria-label={(closed.includes(i) ? '展开 ' : '收起 ') + x.name}
                      onClick={() => setClosed(closed.includes(i) ? closed.filter((c) => c !== i) : [...closed, i])}>{closed.includes(i) ? '▸' : '▾'}</button>
                  : <span className="tw-sp" />}
                <span style={{ fontWeight: x.level === 0 ? 600 : x.level === 1 ? 500 : 400 }}>{x.name}</span>
              </td>
              <td className="num">{x.qty}</td>
              <td>{x.material}</td>
              <td className="mono muted">{x.drawing}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ── 物性数据 ─────────────────────────────────────
export function Properties({ d }: { d: D }) {
  return (
    <div>
      <div className="blk-prop-hd">
        <b>{s(d.substance)}</b>
        {s(d.cas) && <span className="mono muted">CAS {s(d.cas)}</span>}
        {s(d.condition) && <span className="unit-chip">{s(d.condition)}</span>}
        {s(d.phase) && <span className="dc-badge b-blue">{s(d.phase)}</span>}
      </div>
      <dl className="spec-grid">
        {list(d.items).map((p, i) => (
          <div key={i} className="spec-item"><dt>{s(p.name)}</dt><dd>{s(p.value)} <span className="unit">{s(p.unit)}</span></dd></div>
        ))}
      </dl>
      {(s(d.source) || s(d.note)) && <div className="blk-sub-note">{[s(d.source) && `来源：${s(d.source)}`, s(d.note)].filter(Boolean).join('　')}</div>}
    </div>
  );
}

// ── 规范条款 ─────────────────────────────────────
const STATUS: Record<string, string> = { 现行: 'ok', 废止: 'bad', 即将实施: 'mid' };
export function Clause({ d }: { d: D }) {
  return (
    <div className="blk-clause">
      <div className="hd">
        <b className="mono std">{s(d.standard)}</b>
        {s(d.status) && <span className={'st-pill ' + (STATUS[s(d.status)] ?? 'gray')}>{s(d.status)}</span>}
        {s(d.title) && <span className="muted">{s(d.title)}</span>}
      </div>
      <div className="txt">
        <b className="mono">{s(d.clause)}</b>
        <span>「{s(d.text)}」<Refs v={d.ref} />
          {d.verified === true && <span className="chk ok"><Icon name="check" size={13} stroke={2.2} style={{ verticalAlign: '-2px', marginRight: 3 }} />已核对原文</span>}
          {d.verified === false && <span className="chk bad">原文里没找到这段，请核对</span>}</span>
      </div>
      <dl className="kv">
        {s(d.applies) && <><dt>适用</dt><dd>{s(d.applies)}</dd></>}
        {s(d.notes) && <><dt>注意</dt><dd>{s(d.notes)}</dd></>}
        {strs(d.related).length > 0 && <><dt>相关</dt><dd className="rel">{strs(d.related).join(' · ')}</dd></>}
      </dl>
    </div>
  );
}

// ── 选型匹配 ─────────────────────────────────────
const OK: Record<string, [ReactNode, string]> = {
  yes: [<Icon key="ok" name="check" size={13} stroke={2.4} style={{ verticalAlign: '-2px' }} />, 'ok'],
  no: [<Icon key="bad" name="x" size={13} stroke={2.4} style={{ verticalAlign: '-2px' }} />, 'bad'],
  partial: ['!', 'mid'],
};
export function Match({ d }: { d: D }) {
  const items = list(d.items);
  const met = items.filter((x) => s(x.ok) === 'yes').length;
  return (
    <div>
      <div className="blk-match-hd">
        <span><span className="muted">推荐</span><b>{s(d.model)}</b>{s(d.basis) && <span className="muted">{s(d.basis)}</span>}</span>
        <span className="score"><b className="tnum">{met} / {items.length}</b><span className="muted">项需求满足</span></span>
      </div>
      <table className="dc-table compact">
        <thead><tr><th>需求</th><th className="num">客户要求</th><th className="num">本方案</th><th /></tr></thead>
        <tbody>
          {items.map((x, i) => {
            const [mk, cls] = OK[s(x.ok)] ?? OK.partial;
            return (
              <tr key={i} className={cls === 'bad' ? 'neg-row' : ''}>
                <td>{s(x.need)}</td><td className="num muted">{s(x.required)}</td><td className="num strong">{s(x.offered)}</td>
                <td className={'mark ' + cls} style={{ textAlign: 'center' }}><b>{mk}</b></td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

// ── 跟进事项 ─────────────────────────────────────
const FU: Record<string, string> = { 待办: 'gray', 进行中: 'mid', 已完成: 'ok', 客户方: 'info' };
export function Followups({ d }: { d: D }) {
  const today = new Date().toISOString().slice(0, 10);
  const year = today.slice(0, 4);
  const late = (due: string, st: string) => {
    if (st === '已完成' || !due) return false;
    const m = /(\d{4})?[-/.年]?(\d{1,2})[-/.月](\d{1,2})/.exec(due);
    if (!m) return false;
    const iso = `${m[1] ?? year}-${m[2].padStart(2, '0')}-${m[3].padStart(2, '0')}`;
    return iso < today;
  };
  return (
    <table className="dc-table compact">
      <thead><tr><th>事项</th><th>负责</th><th>截止</th><th>状态</th></tr></thead>
      <tbody>
        {list(d.items).map((x, i) => (
          <tr key={i}>
            <td>{s(x.task)}</td><td className="muted">{s(x.owner)}</td>
            <td className={'tnum' + (late(s(x.due), s(x.status)) ? ' bad-t' : '')}>{s(x.due)}{late(s(x.due), s(x.status)) ? ' · 已过期' : ''}</td>
            <td><span className={'st-pill ' + (FU[s(x.status)] ?? 'gray')}>{s(x.status) || '待办'}</span></td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

// ── 交付计划（甘特图）────────────────────────────
const GS: Record<string, string> = { done: '已完成', doing: '进行中', plan: '计划', late: '延期' };
export function Gantt({ d }: { d: D }) {
  const [now] = useState(() => Date.now());
  const parse = (v: unknown) => { const t = Date.parse(s(v)); return Number.isNaN(t) ? null : t; };
  const tasks = list(d.tasks).map((t) => ({ name: s(t.name), a: parse(t.start), b: parse(t.end) ?? parse(t.start), st: GS[s(t.status)] ? s(t.status) : 'plan', ms: t.milestone === true }))
    .filter((t) => t.a != null || t.b != null).map((t) => ({ ...t, a: t.a ?? t.b!, b: t.b ?? t.a! }));
  if (tasks.length === 0) return <div className="muted" style={{ padding: 16 }}>没有可排的日期</div>;
  const DAY = 86400000;
  const min = Math.min(...tasks.map((t) => t.a)), max = Math.max(...tasks.map((t) => t.b));
  const byWeek = (max - min) / DAY > 35;
  const unit = byWeek ? 7 * DAY : DAY;
  const start = byWeek ? min - ((new Date(min).getDay() + 6) % 7) * DAY : min;
  const count = Math.max(1, Math.ceil((max + DAY - start) / unit));
  const pct = (t: number) => ((t - start) / (count * unit)) * 100;
  const todayT = parse(d.today) ?? now;
  const showToday = todayT >= start && todayT <= start + count * unit;
  const label = (i: number) => { const dt = new Date(start + i * unit); return byWeek ? `${dt.getMonth() + 1}/${dt.getDate()}` : `${dt.getDate()}`; };
  return (
    <div className="blk-gantt">
      <div className="dc-scroll">
        <div className="g" style={{ minWidth: 200 + count * (byWeek ? 44 : 26) }}>
          <div className="g-row g-head">
            <span className="g-name">工序</span>
            <span className="g-track">{Array.from({ length: count }, (_, i) => <span key={i} className="g-tick" style={{ left: `${(i / count) * 100}%`, width: `${100 / count}%` }}>{label(i)}</span>)}</span>
          </div>
          {tasks.map((t, i) => (
            <div key={i} className="g-row">
              <span className="g-name"><span>{t.name}</span><span className={'g-st ' + t.st}>{GS[t.st]}</span></span>
              <span className="g-track">
                {t.ms
                  ? <i className={'g-ms ' + t.st} style={{ left: `${pct(t.b + DAY / 2)}%` }} title={new Date(t.b).toLocaleDateString('zh-CN')} />
                  : <i className={'g-bar ' + t.st} style={{ left: `${pct(t.a)}%`, width: `${Math.max(0.8, pct(t.b + DAY) - pct(t.a))}%` }}
                      title={`${new Date(t.a).toLocaleDateString('zh-CN')} – ${new Date(t.b).toLocaleDateString('zh-CN')}`} />}
                {showToday && <i className="g-today" style={{ left: `${pct(todayT)}%` }} />}
              </span>
            </div>
          ))}
        </div>
      </div>
      <div className="blk-legend">
        <span><i style={{ background: '#1c6b45' }} />已完成</span><span><i style={{ background: '#2a4a85' }} />进行中 / 计划</span>
        <span><i style={{ background: '#b4690e' }} />延期</span><span>◆ 里程碑</span>{showToday && <span><i style={{ background: '#a00d25', width: 2 }} />今天</span>}
        {s(d.conclusion) && <span className="src warn" style={{ fontWeight: 600 }}>{s(d.conclusion)}</span>}
      </div>
    </div>
  );
}
