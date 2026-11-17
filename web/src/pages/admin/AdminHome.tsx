// E0 后台首页：先回答「系统好不好」（状态条 + 告警），再回答「用得怎么样」（指标、趋势），
// 最后是「我该做什么」（待办、语料缺口）。每块都取真实接口；没有对应权限的块不请求（越权请求会进审计）、不显示。
import { useState } from 'react';
import { NavLink, useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { KB_TIER_LABEL, Perm } from '../../lib/types';
import type { ClauseRow, DocRow, KbRow, TermRow } from '../../lib/types';

interface OpsAlert {
  kind: string; runId: string; startedAt: string; error: string | null;
  consecutiveFailures: number; acknowledgedBy: string | null; acknowledgedAt: string | null;
}
interface BackupStatus {
  rows: { kind: string; configured: boolean; last: { startedAt: string; finishedAt: string | null; ok: boolean | null; error: string | null } | null }[];
}
interface Consistency { currentModel: string; totalChunks: number; inconsistent: number }
interface ModelStatus { mode: string; online: boolean; name: string; maxModelLen: number | null; vision: boolean; maxTokens: number; error: string | null }
interface UsageWeek { weekStart: string; modules: Record<string, number>; totalCalls: number; activeUsers: number; hitRate: number | null }
interface NoResult { question: string; count: number; lastAt: string }
interface AuditRow { id: number; at: string; username: string | null; action: string; targetType: string | null; detail: unknown }

const BACKUP_LABEL: Record<string, string> = { LocalIncremental: '本地增量', LocalFull: '本地全量', RemoteFull: '异地全量' };

/** 趋势图的模块：常用的单独一色，其余并成「其他」。 */
const MODULES: { name: string; keys: string[]; color: string }[] = [
  { name: '检索问答', keys: ['检索问答'], color: '#141414' },
  { name: '项目查询', keys: ['项目查询'], color: '#6E7B8B' },
  { name: '出文档', keys: ['方案生成'], color: '#C8102E' },
  { name: '故障案例', keys: ['故障案例'], color: '#B9C1CA' },
  { name: '其他', keys: ['报价合同', '翻译', '报修工单'], color: '#E2E5E8' },
];

/** 审计动作 → 首页「最近操作」里的说法；只挑管理员关心的，问答这类高频动作不进这个列表。 */
const AUDIT_KIND: [string, string][] = [
  ['doc.upload', '上传'], ['doc.delete', '删除'], ['doc.reparse', '重新解析'], ['doc.queue_parse', '提交解析'],
  ['template.', '模板'], ['user.', '用户'], ['config.update', '设置'], ['kb.', '知识库'],
  ['gen.render', '生成'], ['backup.', '备份'], ['term.', '术语'], ['clause.', '条款'],
];

/** 动作 → 一句话里的动词（「新建账号 wang」「启用模板 技术协议」）。 */
const AUDIT_VERB: Record<string, string> = {
  'doc.upload': '上传', 'doc.delete': '删除文档', 'doc.reparse': '重新解析', 'doc.queue_parse': '提交解析',
  'template.upload': '上传模板', 'template.enable': '启用模板', 'template.disable': '停用模板', 'template.copy': '复制模板',
  'template.slot_update': '修改槽位', 'user.create': '新建账号', 'user.deactivate': '停用账号', 'user.reactivate': '启用账号',
  'user.reset_password': '重置口令', 'user.assign_role': '调整角色', 'config.update': '修改设置', 'kb.create': '新建知识库',
  'kb.update': '修改知识库', 'gen.render': '生成文档', 'backup.failed': '备份失败', 'backup.acknowledge': '知悉备份告警',
};

const WEEKDAY = '日一二三四五六';

function startOfWeek(d: Date): Date {
  const x = new Date(d.getFullYear(), d.getMonth(), d.getDate());
  x.setDate(x.getDate() - ((x.getDay() + 6) % 7));   // 周一为一周开始，与服务端统计口径一致
  return x;
}
const ymd = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

function when(iso: string): string {
  const t = new Date(iso);
  if (Number.isNaN(t.getTime())) return '';
  const now = new Date();
  const hm = `${String(t.getHours()).padStart(2, '0')}:${String(t.getMinutes()).padStart(2, '0')}`;
  if (t.toDateString() === now.toDateString()) return `今天 ${hm}`;
  const y = new Date(now); y.setDate(now.getDate() - 1);
  if (t.toDateString() === y.toDateString()) return `昨天 ${hm}`;
  return `${t.getMonth() + 1}/${t.getDate()} ${hm}`;
}

function detailName(detail: unknown): string | null {
  let d = detail;
  if (typeof d === 'string') { try { d = JSON.parse(d); } catch { return null; } }
  if (!d || typeof d !== 'object') return null;
  const o = d as Record<string, unknown>;
  for (const k of ['name', 'title', 'fileName', 'username', 'displayName', 'key']) {
    if (typeof o[k] === 'string' && (o[k] as string).length > 0) return o[k] as string;
  }
  return null;
}

type Tone = 'ok' | 'warn' | 'bad';
interface StatusItem { label: string; value: string; note: string; tone: Tone }

export function AdminHome() {
  const { profile } = useAuth();
  const has = (p: string) => profile?.permissions.includes(p) ?? false;
  const canOps = has(Perm.SystemConfig);
  const canStats = has(Perm.AuditRead);
  const canCorpus = has(Perm.CorpusManage);
  const canKbs = canCorpus || has(Perm.KbManage) || has(Perm.QaInternal);
  const nav = useNavigate();
  const qc = useQueryClient();
  const [acking, setAcking] = useState(false);

  const alerts = useQuery({ queryKey: ['ops-alerts'], enabled: canOps, queryFn: () => get<OpsAlert[]>('/api/ops/alerts') });
  const backup = useQuery({ queryKey: ['ops-backup'], enabled: canOps, queryFn: () => get<BackupStatus>('/api/ops/backup/status') });
  const consistency = useQuery({ queryKey: ['ops-consistency'], enabled: canOps, queryFn: () => get<Consistency>('/api/ops/embedding-consistency') });
  const model = useQuery({ queryKey: ['ops-model'], enabled: canOps, refetchInterval: 60_000, queryFn: () => get<ModelStatus>('/api/ops/model-status') });
  const usage = useQuery({ queryKey: ['stats-usage', 8], enabled: canStats, queryFn: () => get<UsageWeek[]>('/api/stats/usage?weeks=8') });
  const gaps = useQuery({ queryKey: ['stats-no-result', 7], enabled: canStats, queryFn: () => get<NoResult[]>('/api/stats/no-result?days=7') });
  const audit = useQuery({ queryKey: ['audit-recent'], enabled: canStats, queryFn: () => get<AuditRow[]>('/api/audit/logs?limit=300') });
  const docs = useQuery({ queryKey: ['docs-all'], enabled: canCorpus, queryFn: () => get<DocRow[]>('/api/documents') });
  const kbs = useQuery({ queryKey: ['kbs'], enabled: canKbs, queryFn: () => get<KbRow[]>('/api/kbs') });
  const terms = useQuery({ queryKey: ['terms-pending'], queryFn: () => get<TermRow[]>('/api/terms?status=Pending') });
  const clauses = useQuery({ queryKey: ['clauses-pending'], queryFn: () => get<ClauseRow[]>('/api/clauses?status=Pending') });

  const ack = async (runId: string) => {
    setAcking(true);
    try {
      await post(`/api/ops/backup/${runId}/ack`);
      void qc.invalidateQueries({ queryKey: ['ops-alerts'] });
    } finally { setAcking(false); }
  };

  // ── 近 8 周：服务端只回有记录的周，没有调用的周补零，横轴才是连续的 ──
  const thisWeek = startOfWeek(new Date());
  const weekKeys = Array.from({ length: 8 }, (_, i) => { const d = new Date(thisWeek); d.setDate(d.getDate() - 7 * (7 - i)); return d; });
  const byWeek = new Map((usage.data ?? []).map((w) => [w.weekStart, w]));
  const weeks = weekKeys.map((d, i) => {
    const w = byWeek.get(ymd(d));
    return {
      label: i === 7 ? '本周' : `${d.getMonth() + 1}/${d.getDate()}`,
      total: w?.totalCalls ?? 0, users: w?.activeUsers ?? 0, hit: w?.hitRate ?? null,
      parts: MODULES.map((m) => ({ color: m.color, n: m.keys.reduce((s, k) => s + (w?.modules[k] ?? 0), 0) })),
    };
  });
  const cur = weeks[7], prev = weeks[6];
  const maxTotal = Math.max(1, ...weeks.map((w) => w.parts.reduce((s, p) => s + p.n, 0)));

  // ── 文档：解析队列、按类别、每周累计（知识库规模的迷你柱）──
  const docRows = docs.data ?? [];
  const inQueue = docRows.filter((d) => ['Queued', 'Parsing', 'Reparsing'].includes(d.parseStatus)).length;
  const waiting = docRows.filter((d) => d.parseStatus === 'Waiting').length;
  const notParsed = docRows.filter((d) => d.parseStatus === 'NotParsed').length;
  const failed = docRows.filter((d) => d.parseStatus === 'Failed').length;
  const cats = new Map<string, number>();
  for (const d of docRows) cats.set(d.docCategory || '未分类', (cats.get(d.docCategory || '未分类') ?? 0) + 1);
  const catTop = [...cats.entries()].sort((a, b) => b[1] - a[1]);
  const catLine = [...catTop.slice(0, 4).map(([k, n]) => `${k} ${n}`),
    ...(catTop.length > 4 ? [`其他 ${catTop.slice(4).reduce((s, [, n]) => s + n, 0)}`] : [])].join(' · ');
  const docsByWeek = weekKeys.map((d) => { const end = new Date(d); end.setDate(end.getDate() + 7); return docRows.filter((x) => new Date(x.uploadedAt) < end).length; });
  const newThisWeek = docRows.filter((x) => new Date(x.uploadedAt) >= thisWeek).length;

  const kbList = (kbs.data ?? []).filter((k) => k.isActive);
  const kbDocs = kbList.reduce((s, k) => s + k.docCount, 0);
  const kbChunks = kbList.reduce((s, k) => s + k.chunkCount, 0);

  // ── 状态条 ──
  const status: StatusItem[] = [];
  if (canOps && backup.data) {
    const runs = backup.data.rows.filter((r) => r.last).sort((a, b) => (b.last!.startedAt > a.last!.startedAt ? 1 : -1));
    const latest = runs[0];
    const remoteOff = backup.data.rows.some((r) => r.kind === 'RemoteFull' && !r.configured);
    status.push(latest
      ? {
        label: '数据备份',
        value: `${when(latest.last!.startedAt)} ${BACKUP_LABEL[latest.kind] ?? latest.kind}${latest.last!.ok === false ? '失败' : latest.last!.ok ? '成功' : '进行中'}`,
        note: remoteOff ? '异地备份未配置' : '本地与异地均已配置',
        tone: latest.last!.ok === false ? 'bad' : remoteOff ? 'warn' : 'ok',
      }
      : { label: '数据备份', value: '还没有备份记录', note: '去系统设置里配置并执行一次', tone: 'warn' });
  }
  if (canOps && consistency.data) {
    const c = consistency.data;
    status.push({
      label: '向量一致性', value: `${(c.totalChunks - c.inconsistent).toLocaleString()} / ${c.totalChunks.toLocaleString()} 分块一致`,
      note: c.inconsistent > 0 ? `${c.inconsistent} 个需要重建` : `模型 ${c.currentModel}`, tone: c.inconsistent > 0 ? 'warn' : 'ok',
    });
  }
  if (canCorpus && docs.data) {
    status.push(waiting > 0
      ? { label: '解析队列', value: `${waiting} 份等待中`, note: '解析引擎暂不可用，会自动重试', tone: 'warn' }
      : { label: '解析队列', value: inQueue > 0 ? `${inQueue} 份解析中` : '空闲', note: inQueue > 0 ? '解析完即可检索' : '没有排队的文档', tone: 'ok' });
  }
  if (canOps && model.data) {
    const m = model.data;
    const ctx = m.maxModelLen ? `上下文 ${Math.round(m.maxModelLen / 1024)}K` : '上下文未知';
    status.push(m.mode === 'stub'
      ? { label: '对话模型', value: '演示模式', note: '在系统设置里接入真实模型', tone: 'warn' }
      : m.online
        ? { label: '对话模型', value: `${m.name.split('/').pop()} 在线`, note: `${ctx} · 看图${m.vision ? '已开启' : '未开启'}`, tone: 'ok' }
        : { label: '对话模型', value: '不在线', note: m.error ?? '模型服务没有响应', tone: 'bad' });
  }

  // ── 指标卡 ──
  const pct = (a: number, b: number) => (b === 0 ? null : Math.round(((a - b) / b) * 100));
  const callDelta = pct(cur.total, prev.total);
  const kpis = [
    canStats && {
      label: '本周调用', value: cur.total.toLocaleString(), unit: '次',
      delta: callDelta === null ? '上周没有调用' : `较上周 ${callDelta >= 0 ? '+' : ''}${callDelta}%`,
      up: (callDelta ?? 0) >= 0, series: weeks.map((w) => w.total),
    },
    canStats && {
      label: '活跃用户', value: String(cur.users), unit: '人',
      delta: `较上周 ${cur.users - prev.users >= 0 ? '+' : ''}${cur.users - prev.users}`,
      up: cur.users >= prev.users, series: weeks.map((w) => w.users),
    },
    canStats && {
      label: '问答命中率', value: cur.hit === null ? '—' : cur.hit.toFixed(1), unit: cur.hit === null ? '本周还没有提问' : '%',
      delta: cur.hit !== null && prev.hit !== null ? `较上周 ${cur.hit - prev.hit >= 0 ? '+' : ''}${(cur.hit - prev.hit).toFixed(1)}` : '没有可比的上周',
      up: cur.hit === null || prev.hit === null || cur.hit >= prev.hit, series: weeks.map((w) => w.hit ?? 0),
    },
    canKbs && kbs.data && {
      label: '知识库', value: kbDocs.toLocaleString(), unit: `份文档 · ${kbChunks.toLocaleString()} 个分块`,
      delta: canCorpus ? `本周新增 ${newThisWeek}` : '已入库',
      up: null, series: canCorpus ? docsByWeek : [],
    },
  ].filter(Boolean) as { label: string; value: string; unit: string; delta: string; up: boolean | null; series: number[] }[];

  const todo: { label: string; hint: string; n: number | undefined; to: string; action: string }[] = [
    { label: '待解析文档', hint: '上传后还没提交解析', n: canCorpus ? notParsed : undefined, to: '/admin/corpus', action: '去解析' },
    { label: '解析失败待处理', hint: '看失败原因，换引擎或重传', n: canCorpus ? failed : undefined, to: '/admin/corpus', action: '查看原因' },
    { label: '待确认术语', hint: '翻译里新出现的术语', n: terms.data?.length, to: '/admin/meta', action: '术语表' },
    { label: '待审定条款', hint: '合同条款库的新版本', n: clauses.data?.length, to: '/admin/templates', action: '条款库' },
  ];

  const gapRows = (gaps.data ?? []).slice(0, 6);
  const gapMax = Math.max(1, ...gapRows.map((g) => g.count));
  const auditRows = (audit.data ?? [])
    .map((a) => ({ a, kind: AUDIT_KIND.find(([p]) => a.action.startsWith(p))?.[1] }))
    .filter((x) => x.kind).slice(0, 6);

  const now = new Date();
  const summary = canStats && usage.data
    ? `本周 ${cur.users} 人在用，${cur.total.toLocaleString()} 次调用`
    : profile?.companyName ?? '';

  return (
    <div className="sc dash" style={{ flexGrow: 1, minHeight: 0 }}>
      <div className="dash-in">
        <div className="dash-head">
          <div>
            <h1 className="dash-title">管理后台</h1>
            <p className="dash-sub">{now.getMonth() + 1} 月 {now.getDate()} 日 周{WEEKDAY[now.getDay()]}{summary ? ` · ${summary}` : ''}</p>
          </div>
          <div className="dash-actions">
            {canOps && <button className="gbtn dash-btn" onClick={() => nav('/admin/settings')}>备份与设置</button>}
            {has(Perm.UserManage) && <button className="gbtn dash-btn" onClick={() => nav('/admin/users')}>新建用户</button>}
            {canCorpus && <button className="pbtn dash-btn" onClick={() => nav('/admin/corpus')}>上传资料</button>}
          </div>
        </div>

        {/* 告警（FR-9.3：任务失败须告警，不得仅写日志）：知悉不撤，同类型成功一次才消 */}
        {(alerts.data ?? []).map((a) => (
          <div key={a.runId} className="dash-alert">
            <div style={{ flexGrow: 1, minWidth: 0 }}>
              <div style={{ fontWeight: 600 }}>
                {BACKUP_LABEL[a.kind] ?? a.kind}备份失败{a.consecutiveFailures > 1 ? `（连续 ${a.consecutiveFailures} 次）` : ''}
              </div>
              <div style={{ fontSize: 12.5, marginTop: 3, lineHeight: 1.6 }}>
                {a.error ?? '未记录具体原因'} · {when(a.startedAt)}
                {a.acknowledgedAt && ` · 已知悉，告警保持到该类备份成功一次`}
              </div>
            </div>
            {!a.acknowledgedAt && <button className="gbtn" disabled={acking} onClick={() => void ack(a.runId)}>我已知悉</button>}
          </div>
        ))}

        {status.length > 0 && (
          <section className="dash-status" aria-label="运行状态">
            {status.map((s) => (
              <div key={s.label} className="dash-st">
                <span className={'dash-dot ' + s.tone} aria-hidden="true" />
                <div className="dash-st-txt">
                  <span className="dash-st-l">{s.label}</span>
                  <span className="dash-st-v">{s.value}</span>
                  <span className={'dash-st-n ' + s.tone}>{s.note}</span>
                </div>
              </div>
            ))}
          </section>
        )}

        {kpis.length > 0 && (
          <section className="dash-kpis" aria-label="关键指标">
            {kpis.map((k) => {
              const hi = Math.max(1, ...k.series);
              return (
                <div key={k.label} className="dash-card dash-kpi">
                  <span className="dash-kpi-l">{k.label}</span>
                  <div className="dash-kpi-v"><b>{k.value}</b><span>{k.unit}</span></div>
                  <div className="dash-kpi-f">
                    <span className={'dash-delta ' + (k.up === null ? 'flat' : k.up ? 'up' : 'down')}>{k.delta}</span>
                    {k.series.length > 0 && (
                      <span className="dash-mini" aria-hidden="true" title="近 8 周">
                        {k.series.map((v, i) => (
                          <i key={i} style={{ height: Math.max(5, Math.round((v / hi) * 28)), background: i === k.series.length - 1 ? '#141414' : '#DCDCD8' }} />
                        ))}
                      </span>
                    )}
                  </div>
                </div>
              );
            })}
          </section>
        )}

        <div className="dash-row">
          {canStats && (
            <section className="dash-card dash-wide" aria-labelledby="dash-trend">
              <div className="dash-sec-hd">
                <h2 id="dash-trend" className="dash-h2">使用趋势 · 近 8 周</h2>
                <div className="dash-legend">
                  {MODULES.map((m) => <span key={m.name}><i style={{ background: m.color }} />{m.name}</span>)}
                </div>
              </div>
              <div className="dash-bars">
                {weeks.map((w, i) => (
                  <div key={i} className="dash-bar">
                    <span className="dash-bar-n">{w.total > 0 ? w.total.toLocaleString() : ''}</span>
                    <div className="dash-bar-stack" title={MODULES.map((m, j) => `${m.name} ${w.parts[j].n}`).join('，')}>
                      {w.parts.map((p, j) => p.n > 0 && <div key={j} style={{ height: Math.round((p.n / maxTotal) * 170), background: p.color }} />)}
                    </div>
                    <span className="dash-bar-l">{w.label}</span>
                  </div>
                ))}
              </div>
              {cur.total + prev.total === 0 && <div className="hint">近两周还没有调用记录，有人用起来以后这里会出现每周的使用量。</div>}
            </section>
          )}

          <section className="dash-card dash-side" aria-labelledby="dash-todo">
            <h2 id="dash-todo" className="dash-h2" style={{ marginBottom: 6 }}>待办</h2>
            {todo.map((t) => (
              <NavLink key={t.label} to={t.to} className="dash-todo">
                <span className={'dash-todo-n' + ((t.n ?? 0) > 0 ? ' hot' : '')}>{t.n ?? '—'}</span>
                <span className="dash-todo-t"><b>{t.label}</b><span>{t.hint}</span></span>
                <span className="dash-todo-a">{t.action} ›</span>
              </NavLink>
            ))}
          </section>
        </div>

        <div className="dash-row">
          {canStats && (
            <section className="dash-card dash-wide" aria-labelledby="dash-gap">
              <div className="dash-sec-hd">
                <div>
                  <h2 id="dash-gap" className="dash-h2">语料缺口 · 近 7 天没答上来的问题</h2>
                  <div className="hint" style={{ marginTop: 3 }}>问的人越多越该补：补进对应资料后，这些问题就能答上来</div>
                </div>
                {(gaps.data?.length ?? 0) > gapRows.length && <NavLink to="/admin/audit" className="dash-link">全部 {gaps.data!.length} 条 ›</NavLink>}
              </div>
              {gapRows.length === 0 ? (
                <div className="dash-empty">近 7 天的提问都在资料里找到了答案。</div>
              ) : (
                <div style={{ overflowX: 'auto' }}>
                  <table className="dash-table">
                    <thead><tr><th>问题</th><th style={{ textAlign: 'right' }}>次数</th><th>最近</th><th /></tr></thead>
                    <tbody>
                      {gapRows.map((g) => (
                        <tr key={g.question}>
                          <td>{g.question}</td>
                          <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                            <i className="dash-gap-bar" style={{ width: Math.round((g.count / gapMax) * 64) }} />
                            <b>{g.count}</b>
                          </td>
                          <td className="dash-muted" style={{ whiteSpace: 'nowrap' }}>{when(g.lastAt)}</td>
                          <td style={{ textAlign: 'right' }}>
                            {canCorpus && <button className="gbtn" onClick={() => nav('/admin/corpus')}>补资料</button>}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </section>
          )}

          {canKbs && kbs.data && (
            <section className="dash-card dash-side" aria-labelledby="dash-kb">
              <div className="dash-sec-hd">
                <h2 id="dash-kb" className="dash-h2">知识库</h2>
                <NavLink to="/admin/kbs" className="dash-link">管理 ›</NavLink>
              </div>
              {kbList.length === 0 && <div className="dash-empty">还没有知识库。</div>}
              {kbList.map((k) => (
                <div key={k.id} className="dash-kb">
                  <div className="dash-kb-hd">
                    <b>{k.name}</b>
                    <span className={'dash-tier ' + k.tier}>{KB_TIER_LABEL[k.tier]}</span>
                    <span className="dash-muted" style={{ marginLeft: 'auto' }}>{k.lastUpdatedAt ? when(k.lastUpdatedAt) : '未更新'}</span>
                  </div>
                  <div className="dash-kb-bar"><i style={{ width: `${kbDocs === 0 ? 0 : Math.round((k.docCount / kbDocs) * 100)}%` }} /></div>
                  <span className="dash-muted">{k.docCount} 份文档 · {k.chunkCount.toLocaleString()} 个分块</span>
                </div>
              ))}
              {canCorpus && catLine && <div className="dash-kb-cat">按类别：{catLine}</div>}
            </section>
          )}
        </div>

        {canStats && auditRows.length > 0 && (
          <section className="dash-card" aria-labelledby="dash-audit">
            <div className="dash-sec-hd">
              <h2 id="dash-audit" className="dash-h2">最近操作</h2>
              <NavLink to="/admin/audit" className="dash-link">审计与统计 ›</NavLink>
            </div>
            {auditRows.map(({ a, kind }) => (
              <div key={a.id} className="dash-ev">
                <span className="dash-muted dash-ev-t">{when(a.at)}</span>
                <span className="dash-ev-k">{kind}</span>
                <b>{a.username ?? '系统'}</b>
                <span className="dash-ev-w">{[AUDIT_VERB[a.action] ?? a.action, detailName(a.detail)].filter(Boolean).join(' ')}</span>
              </div>
            ))}
          </section>
        )}
      </div>
    </div>
  );
}
