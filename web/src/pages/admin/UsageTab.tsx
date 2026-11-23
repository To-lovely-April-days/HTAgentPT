// 使用统计（GitHub / 用量面板风格）：时间范围切换 → 汇总条 → 每日调用堆叠柱（悬停看明细）+ 按功能构成
// → 一年活跃热力图 → 按人员 / 没答上来的问题。数据全部来自 /api/stats/daily（服务端按天汇总审计日志）。
// 配色：功能是分类，用固定的五色（问答黑、项目灰蓝、出文档品牌红、故障浅灰蓝、其他更浅）；热力图只表示多少，用单色绿阶。
import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get } from '../../lib/api';
import { Spinner } from '../../components/Common';
import { Icon } from '../../components/Icons';

interface DayRow { date: string; total: number; modules: Record<string, number> }
interface ModuleRow { name: string; n: number; prev: number }
interface PersonRow { userId: string; name: string; role: string; n: number; modules: Record<string, number>; lastAt: string }
interface Totals {
  calls: number; prevCalls: number; activeUsers: number; prevActiveUsers: number; totalUsers: number;
  hitRate: number | null; prevHitRate: number | null; noResult: number; prevNoResult: number;
  docs: number; prevDocs: number; docsByTemplate: Record<string, number>;
}
interface YearData { days: { date: string; n: number }[]; total: number; activeDays: number; longestStreak: number; busiestWeekday: number | null }
interface Daily { from: string; to: string; days: DayRow[]; totals: Totals; moduleRows: ModuleRow[]; people: PersonRow[]; year: YearData | null }
interface NoResultRow { question: string; count: number; lastAt: string }

/** 界面上的五类：翻译、报修工单量小，并成「其他」。 */
const GROUPS = [
  { name: '检索问答', c: '#141414', of: ['检索问答'] },
  { name: '项目查询', c: '#6E7B8B', of: ['项目查询'] },
  { name: '出文档', c: '#C8102E', of: ['出文档'] },
  { name: '故障案例', c: '#B9C1CA', of: ['故障案例'] },
  { name: '其他', c: '#E2E5E8', of: ['翻译', '报修工单'] },
];
const groupSum = (mods: Record<string, number>, g: (typeof GROUPS)[number]) => g.of.reduce((s, m) => s + (mods[m] ?? 0), 0);
const LEVELS = ['#EBEDF0', '#9BE9A8', '#40C463', '#30A14E', '#216E39'];
const WEEKDAY = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];
const RANGES = [{ d: 7, l: '7 天' }, { d: 30, l: '30 天' }, { d: 90, l: '90 天' }, { d: 365, l: '1 年' }];
const fmt = (n: number) => n.toLocaleString('zh-CN');
const md = (iso: string) => { const [, m, d] = iso.split('-'); return `${+m} 月 ${+d} 日`; };

function pct(cur: number, prev: number) {
  if (prev === 0) return cur === 0 ? null : Infinity;
  return ((cur - prev) / prev) * 100;
}

export default function UsageTab() {
  const [days, setDays] = useState(30);
  const [only, setOnly] = useState('');
  const tz = -new Date().getTimezoneOffset();
  const q = useQuery({
    queryKey: ['stats-daily', days, tz],
    queryFn: () => get<Daily>(`/api/stats/daily?days=${days}&tz=${tz}&year=true`),
  });
  const gaps = useQuery({
    queryKey: ['stats-noresult', Math.min(days, 90)],
    queryFn: () => get<NoResultRow[]>(`/api/stats/no-result?days=${Math.min(days, 90)}`),
  });
  const d = q.data;
  const rangeLabel = RANGES.find((r) => r.d === days)?.l ?? `${days} 天`;

  const exportCsv = () => {
    if (!d) return;
    const head = ['日期', '合计', ...GROUPS.map((g) => g.name)];
    const rows = d.days.map((r) => [r.date, r.total, ...GROUPS.map((g) => groupSum(r.modules, g))]);
    const csv = '﻿' + [head, ...rows].map((r) => r.join(',')).join('\r\n');
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
    a.download = `使用统计-${d.from}-${d.to}.csv`;
    a.click();
    URL.revokeObjectURL(a.href);
  };

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, background: '#FAFAF9' }}>
      <div className="dash-in">
        <div className="dash-head">
          <div>
            <h1 className="dash-title">使用统计</h1>
            <p className="dash-sub">{d ? `${md(d.from)} – ${md(d.to)} · 与前 ${rangeLabel}比较` : ' '}</p>
          </div>
          <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
            <div role="group" aria-label="时间范围" className="us-seg">
              {RANGES.map((r) => (
                <button key={r.d} type="button" aria-pressed={r.d === days} className={r.d === days ? 'on' : ''} onClick={() => setDays(r.d)}>{r.l}</button>
              ))}
            </div>
            <select className="us-sel" value={only} onChange={(e) => setOnly(e.target.value)} aria-label="按功能筛选">
              <option value="">全部功能</option>
              {GROUPS.map((g) => <option key={g.name} value={g.name}>{g.name}</option>)}
            </select>
            <button type="button" className="us-sel" onClick={exportCsv} disabled={!d}>导出 CSV</button>
          </div>
        </div>

        {q.isLoading && <Spinner text="统计中…" />}
        {q.isError && <div className="dash-card dash-empty">统计没取到：{String((q.error as Error)?.message ?? '')}</div>}
        {d && (
          <>
            <Summary t={d.totals} rangeLabel={rangeLabel} />
            <section className="dash-card us-main">
              <DailyChart rows={d.days} only={only} />
              <Breakdown rows={d.moduleRows} />
            </section>
            {d.year && <Heatmap y={d.year} />}
            <div className="dash-row" style={{ gap: 18 }}>
              <People rows={d.people} active={d.totals.activeUsers} total={d.totals.totalUsers} />
              <Gaps rows={gaps.data ?? []} loading={gaps.isLoading} hit={d.totals.hitRate} rangeLabel={rangeLabel} />
            </div>
          </>
        )}
      </div>
    </div>
  );
}

// ─── 汇总条 ─────────────────────────────────────────────
/** 和上期比：一句话、好坏（配色）、方向（图标）。 */
type Delta = { text: string; cls: string; dir?: 'up' | 'down' };

function Summary({ t, rangeLabel }: { t: Totals; rangeLabel: string }) {
  const up = 'up', down = 'down', flat = 'flat';
  const pctDelta = (cur: number, prev: number, goodWhenUp = true): Delta => {
    const p = pct(cur, prev);
    if (p == null) return { text: '与上期持平', cls: flat };
    if (p === Infinity) return { text: '上期没有记录', cls: flat };
    if (Math.abs(p) < 0.05) return { text: '与上期持平', cls: flat };
    const good = (p > 0) === goodWhenUp;
    return { text: `${Math.abs(p).toFixed(1)}% 较前 ${rangeLabel}`, cls: good ? up : down, dir: p > 0 ? 'up' : 'down' };
  };
  const userDiff = t.activeUsers - t.prevActiveUsers;
  const hitDiff = t.hitRate != null && t.prevHitRate != null ? t.hitRate - t.prevHitRate : null;
  const tpl = Object.entries(t.docsByTemplate).slice(0, 3).map(([k, v]) => `${k} ${v}`).join(' · ');
  const cells: (Delta & { l: string; v: string; u: string })[] = [
    { l: '调用次数', v: fmt(t.calls), u: '次', ...pctDelta(t.calls, t.prevCalls) },
    { l: '活跃用户', v: String(t.activeUsers), u: `/ ${t.totalUsers} 人`,
      text: userDiff === 0 ? '与上期持平' : `${Math.abs(userDiff)} 人`, cls: userDiff > 0 ? up : userDiff < 0 ? down : flat,
      dir: userDiff > 0 ? 'up' : userDiff < 0 ? 'down' : undefined },
    { l: '问答命中率', v: t.hitRate != null ? t.hitRate.toFixed(1) : '—', u: t.hitRate != null ? '%' : '',
      text: hitDiff == null ? (t.hitRate == null ? '本期没有知识问答' : '上期没有可比数据') : Math.abs(hitDiff) < 0.05 ? '与上期持平' : `${Math.abs(hitDiff).toFixed(1)} 个百分点`,
      dir: hitDiff != null && Math.abs(hitDiff) >= 0.05 ? (hitDiff > 0 ? 'up' : 'down') : undefined,
      cls: hitDiff == null || Math.abs(hitDiff) < 0.05 ? flat : hitDiff > 0 ? up : down },
    { l: '生成文档', v: String(t.docs), u: '份', text: tpl || (t.docs === 0 ? '本期还没有出文档' : ''), cls: flat },
    { l: '没答上来', v: String(t.noResult), u: '次', ...pctDelta(t.noResult, t.prevNoResult, false) },
  ];
  return (
    <section aria-label="汇总" className="us-sum">
      {cells.map((c) => (
        <div key={c.l} className="us-sum-c">
          <span className="dash-kpi-l">{c.l}</span>
          <span className="us-sum-v">{c.v}<small>{c.u}</small></span>
          <span className={`us-sum-d ${c.cls}`}>{c.dir && <Icon name={c.dir === 'up' ? 'trendUp' : 'trendDown'} size={12} className="gi l" />}{c.text}</span>
        </div>
      ))}
    </section>
  );
}

// ─── 每日调用（堆叠柱，悬停明细）────────────────────────────
function niceMax(v: number) {
  if (v <= 4) return 4;
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  for (const m of [1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10]) if (m * p >= v) return m * p;
  return 10 * p;
}

function DailyChart({ rows, only }: { rows: DayRow[]; only: string }) {
  const [hover, setHover] = useState<number | null>(null);
  // 一年按天太密：超过 90 天就按周合并
  const bars = useMemo(() => {
    const groups = only ? GROUPS.filter((g) => g.name === only) : GROUPS;
    const src = rows.length > 90
      ? rows.reduce<{ date: string; end: string; modules: Record<string, number> }[]>((acc, r, i) => {
        if (i % 7 === 0) acc.push({ date: r.date, end: r.date, modules: {} });
        const b = acc[acc.length - 1];
        b.end = r.date;
        for (const [k, v] of Object.entries(r.modules)) b.modules[k] = (b.modules[k] ?? 0) + v;
        return acc;
      }, [])
      : rows.map((r) => ({ date: r.date, end: r.date, modules: r.modules }));
    return src.map((b) => {
      const parts = groups.map((g) => ({ name: g.name, c: g.c, n: groupSum(b.modules, g) }));
      return { ...b, parts, total: parts.reduce((s, p) => s + p.n, 0) };
    });
  }, [rows, only]);
  const weekly = rows.length > 90;
  const max = niceMax(Math.max(0, ...bars.map((b) => b.total)));
  const ticks = [1, 0.75, 0.5, 0.25, 0].map((f) => Math.round(max * f));
  const every = Math.ceil(bars.length / 8);
  const label = (iso: string) => { const [, m, dd] = iso.split('-'); return `${+m}/${+dd}`; };
  const h = hover != null ? bars[hover] : null;
  const dow = (iso: string) => WEEKDAY[new Date(`${iso}T00:00:00`).getDay()];

  return (
    <div style={{ flex: '999 1 720px', minWidth: 0, display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div className="dash-sec-hd">
        <h2 className="dash-h2">{weekly ? '每周调用' : '每日调用'}</h2>
        <span className="dash-muted">按功能堆叠 · 悬停看{weekly ? '当周' : '当天'}明细</span>
      </div>
      <div className="us-chart">
        <div className="us-y">{ticks.map((t, i) => <span key={i}>{fmt(t)}</span>)}</div>
        <div style={{ flex: 1, minWidth: 0, position: 'relative', display: 'flex', flexDirection: 'column' }}>
          <div className="us-grid"><i /><i /><i /><i /><i className="base" /></div>
          <div className="us-bars" style={{ gap: bars.length > 45 ? 2 : 6 }} onMouseLeave={() => setHover(null)}>
            {bars.map((b, i) => (
              <div key={b.date} className="us-col" onMouseEnter={() => setHover(i)}>
                <div className={`us-stack${hover === i ? ' on' : ''}`} style={{ height: `${(b.total / max) * 100}%` }}>
                  {b.parts.map((p) => p.n > 0 && <div key={p.name} style={{ flexGrow: p.n, background: p.c }} />)}
                </div>
              </div>
            ))}
            {h && (
              <div className="us-tip" style={hover! > bars.length * 0.6
                ? { right: `${((bars.length - hover!) / bars.length) * 100}%`, marginRight: 8 }
                : { left: `${((hover! + 1) / bars.length) * 100}%`, marginLeft: 8 }}>
                <b>{weekly ? `${md(h.date)} 起一周` : `${md(h.date)} ${dow(h.date)}`} · {fmt(h.total)} 次</b>
                {h.parts.map((p) => (
                  <span key={p.name}><i style={{ background: p.c }} />{p.name}<em>{fmt(p.n)}</em></span>
                ))}
              </div>
            )}
          </div>
          <div className="us-x" style={{ gap: bars.length > 45 ? 2 : 6 }}>
            {bars.map((b, i) => <span key={b.date}>{i % every === 0 || i === bars.length - 1 ? label(b.date) : ''}</span>)}
          </div>
        </div>
      </div>
    </div>
  );
}

// ─── 按功能构成 ───────────────────────────────────────────
function Breakdown({ rows }: { rows: ModuleRow[] }) {
  const items = GROUPS.map((g) => {
    const pick = rows.filter((r) => g.of.includes(r.name));
    return { ...g, n: pick.reduce((s, r) => s + r.n, 0), prev: pick.reduce((s, r) => s + r.prev, 0) };
  }).filter((x) => x.n > 0 || x.prev > 0).sort((a, b) => b.n - a.n);
  const total = items.reduce((s, x) => s + x.n, 0);
  return (
    <div className="us-brk">
      <h2 className="dash-h2">按功能</h2>
      {items.length === 0 && <div className="dash-empty">这段时间还没有调用。</div>}
      {total > 0 && (
        <div aria-hidden="true" className="us-share">
          {items.map((m) => <div key={m.name} style={{ width: `${(m.n / total) * 100}%`, background: m.c }} />)}
        </div>
      )}
      {items.map((m) => {
        const p = pct(m.n, m.prev);
        return (
          <div key={m.name} className="us-brk-row">
            <i style={{ background: m.c }} />
            <span style={{ flexGrow: 1 }}>{m.name}</span>
            <b>{fmt(m.n)}</b>
            <span className="us-brk-s">{total ? `${Math.round((m.n / total) * 100)}%` : '—'}</span>
            <span className={`us-brk-d ${p == null || p === Infinity ? '' : p >= 0 ? 'up' : 'down'}`}>
              {p == null ? '—' : p === Infinity ? '新增' : `${p >= 0 ? '+' : '−'}${Math.abs(Math.round(p))}%`}
            </span>
          </div>
        );
      })}
    </div>
  );
}

// ─── 一年活跃热力图（GitHub 式，周日起一列）──────────────────
function Heatmap({ y }: { y: YearData }) {
  const { cols, months, level } = useMemo(() => {
    const first = new Date(`${y.days[0].date}T00:00:00`);
    const pad = first.getDay(); // 第一列前面空出的格
    const cells: ({ date: string; n: number } | null)[] = [...Array(pad).fill(null), ...y.days];
    const cols: (typeof cells)[] = [];
    for (let i = 0; i < cells.length; i += 7) cols.push(cells.slice(i, i + 7));
    const nz = y.days.map((x) => x.n).filter((n) => n > 0).sort((a, b) => a - b);
    const q = (f: number) => nz[Math.min(nz.length - 1, Math.floor(nz.length * f))] ?? 0;
    const cuts = [q(0.25), q(0.5), q(0.75)];
    const level = (n: number) => (n <= 0 ? 0 : n <= cuts[0] ? 1 : n <= cuts[1] ? 2 : n <= cuts[2] ? 3 : 4);
    const months: { col: number; label: string }[] = [];
    let last = -1;
    cols.forEach((c, i) => {
      const firstReal = c.find((x) => x);
      if (!firstReal) return;
      const m = +firstReal.date.split('-')[1];
      if (m !== last) { if (last !== -1 || i === 0) months.push({ col: i, label: `${m}月` }); last = m; }
    });
    // 第一列的月份如果和第二个标签挨太近就不显示，避免重叠
    if (months.length > 1 && months[1].col - months[0].col < 3) months.shift();
    return { cols, months, level };
  }, [y]);

  const facts = [
    `共 ${fmt(y.total)} 次调用`, `使用 ${y.activeDays} 天`, `最长连续 ${y.longestStreak} 天`,
    ...(y.busiestWeekday != null ? [`${WEEKDAY[y.busiestWeekday]}最忙`] : []),
  ].join(' · ');
  return (
    <section className="dash-card" style={{ padding: '20px 24px', display: 'flex', flexDirection: 'column', gap: 14 }}>
      <div className="dash-sec-hd">
        <div style={{ display: 'flex', alignItems: 'baseline', gap: 14, flexWrap: 'wrap' }}>
          <h2 className="dash-h2">过去一年的活跃</h2>
          <span className="dash-muted" style={{ fontSize: 13 }}>{facts}</span>
        </div>
        <div className="us-lv">少{LEVELS.map((c) => <i key={c} style={{ background: c }} />)}多</div>
      </div>
      <div style={{ overflowX: 'auto' }}>
        <div className="us-heat" style={{ ['--cols' as string]: cols.length }}>
          <div className="us-heat-d"><span /><span>一</span><span /><span>三</span><span /><span>五</span><span /></div>
          <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 6 }}>
            <div className="us-heat-m">
              {months.map((m) => <span key={m.col} style={{ gridColumn: `${m.col + 1} / span 4` }}>{m.label}</span>)}
            </div>
            <div className="us-heat-g">
              {cols.map((c, i) => (
                <div key={i}>
                  {Array.from({ length: 7 }, (_, r) => {
                    const x = c[r];
                    return <span key={r} title={x ? `${md(x.date)}：${x.n} 次` : undefined}
                      style={{ background: x ? LEVELS[level(x.n)] : 'transparent' }} />;
                  })}
                </div>
              ))}
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}

// ─── 按人员 ──────────────────────────────────────────────
function People({ rows, active, total }: { rows: PersonRow[]; active: number; total: number }) {
  const max = Math.max(1, ...rows.map((r) => r.n));
  const last = (iso: string) => {
    const t = new Date(iso);
    const now = new Date();
    const day = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
    const diff = Math.round((day(now) - day(t)) / 86400000);
    const hm = `${String(t.getHours()).padStart(2, '0')}:${String(t.getMinutes()).padStart(2, '0')}`;
    return diff === 0 ? `今天 ${hm}` : diff === 1 ? '昨天' : `${t.getMonth() + 1}/${t.getDate()}`;
  };
  return (
    <section className="dash-card" style={{ flex: '3 1 640px', padding: '20px 24px', display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div className="dash-sec-hd">
        <h2 className="dash-h2">按人员</h2>
        <span className="dash-muted">{active} 人活跃 · 共 {total} 个账号</span>
      </div>
      {rows.length === 0 ? <div className="dash-empty">这段时间还没有人用。</div> : (
        <div style={{ overflowX: 'auto' }}>
          <table className="us-tbl">
            <thead><tr><th>用户</th><th>角色</th><th style={{ width: '34%' }}>调用</th><th>最常用</th><th>最近活跃</th></tr></thead>
            <tbody>
              {rows.map((u) => {
                const parts = GROUPS.map((g) => ({ ...g, n: groupSum(u.modules, g) })).filter((p) => p.n > 0);
                const top = [...parts].sort((a, b) => b.n - a.n)[0];
                return (
                  <tr key={u.userId}>
                    <td><span className="us-who"><i aria-hidden="true">{u.name.slice(0, 1)}</i><b>{u.name}</b></span></td>
                    <td style={{ color: 'var(--ink-2)' }}>{u.role || '—'}</td>
                    <td>
                      <span style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                        <span aria-hidden="true" className="us-pbar" style={{ width: `${(u.n / max) * 80}%` }}>
                          {parts.map((p) => <span key={p.name} style={{ flexGrow: p.n, background: p.c }} />)}
                        </span>
                        <b>{fmt(u.n)}</b>
                      </span>
                    </td>
                    <td>{top?.name ?? '—'}</td>
                    <td style={{ color: 'var(--ink-3)' }}>{last(u.lastAt)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

// ─── 没答上来的问题（资料缺口，不是故障）──────────────────────
function Gaps({ rows, loading, hit, rangeLabel }: { rows: NoResultRow[]; loading: boolean; hit: number | null; rangeLabel: string }) {
  const max = Math.max(1, ...rows.map((r) => r.count));
  return (
    <section className="dash-card" style={{ flex: '2 1 420px', padding: '20px 24px', display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
        <h2 className="dash-h2">没答上来的问题</h2>
        <span className="dash-muted">
          {hit != null ? `命中率 ${hit.toFixed(1)}% · 剩下的 ${(100 - hit).toFixed(1)}% 按问题汇总，补上资料就能答` : '按问题汇总，补上资料就能答'}
        </span>
      </div>
      {loading && <Spinner text="载入…" />}
      {!loading && rows.length === 0 && <div className="dash-empty">近 {rangeLabel === '1 年' ? '90 天' : rangeLabel}没有答不上来的问题。</div>}
      {rows.slice(0, 10).map((q, i) => (
        <div key={i} className="us-gap">
          <span>{q.question}</span>
          <i aria-hidden="true" style={{ width: `${Math.max(6, (q.count / max) * 80)}px` }} />
          <b>{q.count}</b>
        </div>
      ))}
    </section>
  );
}
