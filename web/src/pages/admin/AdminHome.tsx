// 后台首页：按这个人的权限拼。每块写明要什么权限，登录后挑出他能看的，按他最关心的排先后——
// 系统管理员看系统和全部待办，资料管理员看审核和解析，管理层只看用量和质量，采购看价格，部门主管看审批和本部门，售后主管看工单和案例。
// 版式：页头一句话说清今天的事（关键数字加粗）→ 左边是主要工作（表头用页签带数字）→ 右边一条窄栏放次要的。
// 没有对应权限的块不请求（越权请求会进审计）、不显示。
import { Fragment, useState } from 'react';
import type { ReactNode } from 'react';
import { NavLink, useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post } from '../../lib/api';
import { useAuth } from '../../lib/auth';
import { Perm, TICKET_LABEL } from '../../lib/types';
import type { ApprovalSummary, CaseRow, ClauseRow, CustomerRequestRow, DocRow, KbRow, TermRow, TicketRow, UserRow } from '../../lib/types';
import { Icon } from '../../components/Icons';
import { ADMIN_REVIEW_API, HEAD_REVIEW_API } from './reviewApi';

// ── 接口返回 ─────────────────────────────────────────
interface OpsAlert { kind: string; runId: string; startedAt: string; error: string | null; consecutiveFailures: number; acknowledgedAt: string | null }
interface BackupStatus { rows: { kind: string; configured: boolean; last: { startedAt: string; ok: boolean | null } | null }[] }
interface Consistency { currentModel: string; totalChunks: number; inconsistent: number }
interface ModelStatus { mode: string; online: boolean; name: string; error: string | null }
interface UsageWeek { weekStart: string; totalCalls: number; activeUsers: number; hitRate: number | null }
interface Daily {
  totals: { calls: number; prevCalls: number; activeUsers: number; prevActiveUsers: number; totalUsers: number; hitRate: number | null; prevHitRate: number | null };
  moduleRows: { name: string; n: number; prev: number }[];
}
interface NoResult { question: string; count: number; lastAt: string }
interface AuditRow { id: number; at: string; username: string | null; action: string; detail: unknown }
interface DeptRows { rows: { name: string; members: number; active: number; calls: number; prevCalls: number }[] }
interface Quality { answers: number; helpful: number; notHelpful: number; prevNotHelpful: number; noResult: number }
interface QualityOpen { open: number }
interface MyDept {
  isHead: boolean; name: string; members: number; active: number; calls: number; prevCalls: number; notHelpful: number;
  people: { userId: string; name: string; calls: number; lastAt: string | null }[]; modules: { name: string; n: number }[];
}
interface Prices {
  items: number; suppliers: number; staleCount: number;
  stale: { name: string; spec: string | null; group: string | null; supplier: string | null; date: string; months: number }[];
  recent: { file: string; table: string; date: string; uploadedAt: string; items: number }[];
  uploadsThisMonth: number; itemsThisMonth: number; lookupsThisMonth: number; lookupsPrevMonth: number;
}

const BACKUP_LABEL: Record<string, string> = { LocalIncremental: '本地增量', LocalFull: '本地全量', RemoteFull: '异地全量' };

/** 动作 → 「最近操作」里的说法；只挑管理员关心的，问答这类高频动作不进。 */
const AUDIT_VERB: Record<string, string> = {
  'doc.upload': '上传', 'doc.approve': '审核通过', 'doc.reject': '驳回', 'doc.withdraw': '撤回上传', 'doc.delete': '删除文档', 'doc.reparse': '重新解析', 'doc.queue_parse': '提交解析',
  'template.upload': '上传模板', 'template.enable': '启用模板', 'template.disable': '停用模板', 'template.copy': '复制模板',
  'template.slot_update': '修改槽位', 'user.create': '新建账号', 'user.deactivate': '停用账号', 'user.reactivate': '启用账号',
  'user.reset_password': '重置口令', 'user.assign_role': '调整角色', 'config.update': '修改设置', 'kb.create': '新建知识库',
  'kb.update': '修改知识库', 'gen.render': '生成文档', 'backup.failed': '备份失败', 'backup.acknowledge': '知悉备份告警',
  'term.confirm': '确认术语', 'clause.approve': '审定条款',
};

const WEEKDAY = '日一二三四五六';
const DAY = 86_400_000;

function startOfWeek(d: Date): Date {
  const x = new Date(d.getFullYear(), d.getMonth(), d.getDate());
  x.setDate(x.getDate() - ((x.getDay() + 6) % 7));   // 周一为一周开始，与服务端统计口径一致
  return x;
}
const ymd = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

function when(iso: string | null | undefined): string {
  if (!iso) return '';
  const t = new Date(iso);
  if (Number.isNaN(t.getTime())) return '';
  const now = new Date();
  const hm = `${String(t.getHours()).padStart(2, '0')}:${String(t.getMinutes()).padStart(2, '0')}`;
  if (t.toDateString() === now.toDateString()) return `今天 ${hm}`;
  const y = new Date(now); y.setDate(now.getDate() - 1);
  if (t.toDateString() === y.toDateString()) return `昨天 ${hm}`;
  return `${t.getMonth() + 1}/${t.getDate()}`;
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

// ── 小零件 ───────────────────────────────────────────
const B = ({ children }: { children: ReactNode }) => <b className="ah-b">{children}</b>;

function Delta({ cur, prev, goodUp = true, unit = '%' }: { cur: number; prev: number; goodUp?: boolean; unit?: string }) {
  if (unit === '%' && prev === 0) return null;
  const v = unit === '%' ? Math.round(((cur - prev) / prev) * 100) : Math.round((cur - prev) * 10) / 10;
  if (v === 0) return <span className="ah-delta flat">持平</span>;
  const good = (v > 0) === goodUp;
  return <span className={'ah-delta ' + (good ? 'good' : 'bad')}>{v > 0 ? '↑' : '↓'} {Math.abs(v)}{unit}</span>;
}

function Tag({ tone = 'mute', children }: { tone?: 'red' | 'amber' | 'green' | 'mute'; children: ReactNode }) {
  return <span className={'ah-tag ' + tone}>{children}</span>;
}

const Dot = ({ tone }: { tone: 'ok' | 'warn' | 'bad' }) => <i className={'ah-dot ' + tone} aria-hidden="true" />;

function More({ to, children }: { to: string; children: ReactNode }) {
  return <NavLink to={to} className="ah-more">{children}<Icon name="right" size={13} /></NavLink>;
}

function Card({ title, sub, right, children }: { title: string; sub?: string; right?: ReactNode; children: ReactNode }) {
  return (
    <section className="ah-card">
      <div className="ah-card-hd">
        <div className="ah-card-t"><h2>{title}</h2>{sub && <span>{sub}</span>}</div>
        {right}
      </div>
      {children}
    </section>
  );
}

/** 表头是页签的卡：「待审核 4｜解析失败 2」，点哪个看哪个。 */
function TabCard<K extends string>({ tabs, cur, onTab, right, children }: {
  tabs: { key: K; label: string; n?: number; hot?: boolean }[]; cur: K; onTab: (k: K) => void; right?: ReactNode; children: ReactNode;
}) {
  return (
    <section className="ah-card">
      <div className="ah-tabs" role="tablist">
        <div className="ah-tabs-in">
          {tabs.map((t) => (
            <button key={t.key} type="button" role="tab" aria-selected={cur === t.key} className={'ah-tab' + (cur === t.key ? ' on' : '')} onClick={() => onTab(t.key)}>
              {t.label}{t.n !== undefined && <span className={'ah-tab-n' + (t.hot && t.n > 0 ? ' hot' : '')}>{t.n.toLocaleString()}</span>}
            </button>
          ))}
        </div>
        {right}
      </div>
      {children}
    </section>
  );
}

/** 右栏「名称 …… 数」。 */
function Rows({ rows }: { rows: [ReactNode, ReactNode][] }) {
  return <div className="ah-kv">{rows.map(([a, b], i) => <div key={i} className="ah-kv-r"><span className="ah-kv-a">{a}</span><span className="ah-kv-b">{b}</span></div>)}</div>;
}

/** 横向条：名称、数、占比。 */
function Bars({ rows }: { rows: { name: string; n: number; note?: ReactNode }[] }) {
  const hi = Math.max(1, ...rows.map((r) => r.n));
  return (
    <div className="ah-bars">
      {rows.map((r) => (
        <div key={r.name} className="ah-bar-r">
          <div className="ah-bar-t"><span>{r.name}</span><b>{r.n.toLocaleString()}</b><span className="ah-bar-note">{r.note}</span></div>
          <i className="ah-bar-track"><i style={{ width: `${(r.n / hi) * 100}%` }} /></i>
        </div>
      ))}
    </div>
  );
}

function Empty({ children }: { children: ReactNode }) {
  return <div className="ah-empty">{children}</div>;
}

/** 按周的柱子：上周涂品牌红，本周没过完浅红，更早的灰；柱子上直接写数。 */
function WeekBars({ weeks, fmt }: { weeks: { label: string; v: number | null }[]; fmt: (v: number) => string }) {
  const hi = Math.max(1, ...weeks.map((w) => w.v ?? 0));
  return (
    <div className="ah-weeks">
      <div className="ah-legend">
        <span><i className="last" />上周</span><span><i className="cur" />本周（还没过完）</span><span><i />更早</span>
      </div>
      <div className="ah-wbars">
        {weeks.map((w, i) => {
          const tone = i === weeks.length - 1 ? 'cur' : i === weeks.length - 2 ? 'last' : '';
          return (
            <div key={w.label} className={'ah-wbar ' + tone}>
              <span className="ah-wbar-n">{w.v === null ? '—' : fmt(w.v)}</span>
              <i style={{ height: `${Math.max(2, ((w.v ?? 0) / hi) * 100)}%` }} />
              <span className="ah-wbar-l">{w.label}</span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

type Fact = ReactNode;
interface Block { key: string; order: number; node: ReactNode }

export function AdminHome() {
  const { profile } = useAuth();
  const has = (p: string) => profile?.permissions.includes(p) ?? false;
  const nav = useNavigate();
  const qc = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [metric, setMetric] = useState<'calls' | 'users' | 'hit'>('calls');
  const [corpusTab, setCorpusTab] = useState<'review' | 'failed' | 'parsing'>('review');
  const [ticketTab, setTicketTab] = useState<'open' | 'late'>('open');

  const canOps = has(Perm.SystemConfig);
  const canStats = has(Perm.AuditRead);
  const canCorpus = has(Perm.CorpusManage);
  const canKbs = canCorpus || has(Perm.KbManage);
  const canUsers = has(Perm.UserManage);
  const canPrices = has(Perm.PriceManage);
  const canTickets = has(Perm.TicketHandle);
  const canCases = has(Perm.CaseReview);
  const canTerms = has(Perm.MetaManage);
  const canClauses = has(Perm.TemplateManage);

  const approvals = useQuery({ queryKey: ['approvals-summary'], retry: false, queryFn: () => get<ApprovalSummary>('/api/approvals/summary') });
  const isHead = !!approvals.data?.isHead && !canCorpus;   // 能管全部资料的人审批走「资料管理」

  const alerts = useQuery({ queryKey: ['ops-alerts'], enabled: canOps, queryFn: () => get<OpsAlert[]>('/api/ops/alerts') });
  const backup = useQuery({ queryKey: ['ops-backup'], enabled: canOps, queryFn: () => get<BackupStatus>('/api/ops/backup/status') });
  const consistency = useQuery({ queryKey: ['ops-consistency'], enabled: canOps, queryFn: () => get<Consistency>('/api/ops/embedding-consistency') });
  const model = useQuery({ queryKey: ['ops-model'], enabled: canOps, refetchInterval: 60_000, queryFn: () => get<ModelStatus>('/api/ops/model-status') });
  const usage = useQuery({ queryKey: ['stats-usage', 13], enabled: canStats, queryFn: () => get<UsageWeek[]>('/api/stats/usage?weeks=13') });
  const daily = useQuery({ queryKey: ['stats-daily', 30], enabled: canStats, queryFn: () => get<Daily>('/api/stats/daily?days=30') });
  const depts = useQuery({ queryKey: ['ah-depts'], enabled: canStats, queryFn: () => get<DeptRows>('/api/admin-home/departments?days=30') });
  const quality = useQuery({ queryKey: ['ah-quality'], enabled: canStats, queryFn: () => get<Quality>('/api/admin-home/quality?days=30') });
  const gaps = useQuery({ queryKey: ['stats-no-result', 30], enabled: canStats, queryFn: () => get<NoResult[]>('/api/stats/no-result?days=30') });
  const audit = useQuery({ queryKey: ['audit-recent'], enabled: canStats && canOps, queryFn: () => get<AuditRow[]>('/api/audit/logs?limit=300') });
  const docs = useQuery({ queryKey: ['docs-all'], enabled: canCorpus, queryFn: () => get<DocRow[]>('/api/documents') });
  const qOpen = useQuery({ queryKey: ['quality-summary', 7], enabled: canCorpus, queryFn: () => get<QualityOpen>('/api/quality/summary?days=7') });
  const kbs = useQuery({ queryKey: ['kbs'], enabled: canKbs, queryFn: () => get<KbRow[]>('/api/kbs') });
  const users = useQuery({ queryKey: ['users'], enabled: canUsers, queryFn: () => get<UserRow[]>('/api/users') });
  const terms = useQuery({ queryKey: ['terms-pending'], enabled: canTerms, queryFn: () => get<TermRow[]>('/api/terms?status=Pending') });
  const clauses = useQuery({ queryKey: ['clauses-pending'], enabled: canClauses, queryFn: () => get<ClauseRow[]>('/api/clauses?status=Pending') });
  const myDept = useQuery({ queryKey: ['ah-my-dept'], enabled: isHead, queryFn: () => get<MyDept>('/api/admin-home/my-dept?days=30') });
  const headFiles = useQuery({ queryKey: [HEAD_REVIEW_API.key], enabled: isHead, queryFn: () => get<DocRow[]>(HEAD_REVIEW_API.list) });
  const headCustomers = useQuery({ queryKey: ['approvals-customers'], enabled: isHead, queryFn: () => get<CustomerRequestRow[]>('/api/approvals/customers') });
  const prices = useQuery({ queryKey: ['ah-prices'], enabled: canPrices, queryFn: () => get<Prices>('/api/admin-home/prices') });
  const tickets = useQuery({ queryKey: ['tickets'], enabled: canTickets, queryFn: () => get<TicketRow[]>('/api/tickets') });
  const cases = useQuery({ queryKey: ['review-pending'], enabled: canCases, queryFn: () => get<CaseRow[]>('/api/review/cases') });

  const run = async (fn: () => Promise<unknown>, keys: string[][]) => {
    setBusy(true);
    try { await fn(); keys.forEach((k) => void qc.invalidateQueries({ queryKey: k })); } finally { setBusy(false); }
  };

  if (!profile) return null;
  const now = new Date();
  const facts: { order: number; node: Fact }[] = [];
  const main: Block[] = [];
  const rail: Block[] = [];
  const titles: string[] = [];

  // ── 部门主管：审批 + 本部门 ───────────────────────
  if (isHead) {
    const d = myDept.data;
    titles.push(d?.name || '本部门');
    const files = headFiles.data ?? [];
    const custs = headCustomers.data ?? [];
    const n = files.length + custs.length;
    if (n > 0) facts.push({ order: 10, node: <>有 <B>{n} 件</B>等你审批</> });
    if (d && d.members > 0) {
      const idle = d.people.filter((p) => p.calls === 0);
      facts.push({ order: 60, node: <>部门 {d.members} 个人里 {d.active} 个在用，近 30 天用了 {d.calls.toLocaleString()} 次{idle.length > 0 && idle.length <= 2 ? `；${idle.map((p) => p.name).join('、')}还没用过` : ''}</> });
    }
    main.push({
      key: 'approvals', order: 10, node: (
        <Card title="待我审批" sub="通过后文件才会入库，新客户才会出现在名单里" right={<More to="/approvals">全部审批</More>}>
          {n === 0 ? <Empty>没有等你审批的。</Empty> : (
            <table className="ah-table">
              <thead><tr><th>内容</th><th>类型</th><th>谁提的</th><th className="r">提交</th><th /></tr></thead>
              <tbody>
                {files.slice(0, 6).map((f) => (
                  <tr key={f.id}>
                    <td className="ah-ell">{f.title || f.fileName}</td><td><Tag>文件</Tag></td>
                    <td className="ah-mute">{f.uploaderName ?? ''}</td><td className="r ah-mute">{when(f.uploadedAt)}</td>
                    <td className="r ah-acts">
                      <button type="button" className="ah-btn dark" disabled={busy} onClick={() => void run(() => post(HEAD_REVIEW_API.approve, { docIds: [f.id] }), [[HEAD_REVIEW_API.key], ['approvals-summary']])}>通过</button>
                      <button type="button" className="ah-btn" onClick={() => nav('/approvals')}>退回</button>
                    </td>
                  </tr>
                ))}
                {custs.slice(0, 4).map((c) => (
                  <tr key={c.id}>
                    <td className="ah-ell">{c.name}</td><td><Tag>新客户</Tag></td>
                    <td className="ah-mute">{c.requesterName}</td><td className="r ah-mute">{when(c.requestedAt)}</td>
                    <td className="r ah-acts">
                      <button type="button" className="ah-btn dark" disabled={busy} onClick={() => void run(() => post(`/api/approvals/customers/${c.id}/approve`, { name: null }), [['approvals-customers'], ['approvals-summary']])}>通过</button>
                      <button type="button" className="ah-btn" onClick={() => nav('/approvals')}>退回</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </Card>
      ),
    });
    if (d) {
      const hi = Math.max(1, ...d.people.map((p) => p.calls));
      main.push({
        key: 'people', order: 55, node: (
          <Card title="本部门的人" sub="近 30 天">
            <table className="ah-table">
              <thead><tr><th>姓名</th><th className="r">用了几次</th><th className="r">最近</th></tr></thead>
              <tbody>
                {d.people.map((p) => (
                  <tr key={p.userId}>
                    <td>{p.name}</td>
                    <td className="r"><span className="ah-inbar"><i><i style={{ width: `${(p.calls / hi) * 100}%` }} /></i><span>{p.calls}</span></span></td>
                    <td className="r">{p.lastAt ? <span className="ah-mute">{when(p.lastAt)}</span> : <Tag tone="amber">没用过</Tag>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
        ),
      });
      rail.push({
        key: 'dept-sum', order: 50, node: (
          <Card title={d.name || '本部门'}>
            <Rows rows={[['人数', d.members], ['近 30 天在用', d.active], ['调用', <>{d.calls.toLocaleString()} 次 <Delta cur={d.calls} prev={d.prevCalls} /></>], ['没帮上的回答', `${d.notHelpful} 条`]]} />
          </Card>
        ),
      });
      const total = d.modules.reduce((s, m) => s + m.n, 0);
      if (total > 0) rail.push({ key: 'dept-use', order: 51, node: <Card title="主要用来做什么"><Bars rows={d.modules.map((m) => ({ name: m.name, n: m.n, note: `${Math.round((m.n / total) * 100)}%` }))} /></Card> });
    }
  }

  // ── 资料：审核、解析 ───────────────────────────────
  if (canCorpus) {
    titles.push('资料');
    const rows = docs.data ?? [];
    const pending = rows.filter((d) => d.review === 'Pending').sort((a, b) => a.uploadedAt.localeCompare(b.uploadedAt));
    const failed = rows.filter((d) => d.parseStatus === 'Failed');
    const parsing = rows.filter((d) => ['Queued', 'Parsing', 'Reparsing', 'Waiting'].includes(d.parseStatus));
    if (pending.length > 0) {
      const days = Math.floor((now.getTime() - new Date(pending[0].uploadedAt).getTime()) / DAY);
      facts.push({ order: 20, node: <>有 <B>{pending.length} 份</B>上传等你审核{days >= 1 ? `，最早的已经等了 ${days} 天` : ''}</> });
    }
    if (failed.length > 0) facts.push({ order: 21, node: <><B>{failed.length} 份</B>文件解析失败，要换引擎或重传</> });
    const list = corpusTab === 'review' ? pending : corpusTab === 'failed' ? failed : parsing;
    main.push({
      key: 'corpus', order: 20, node: (
        <TabCard cur={corpusTab} onTab={setCorpusTab} right={<More to="/admin/corpus">资料管理</More>}
          tabs={[{ key: 'review', label: '待审核', n: pending.length, hot: true }, { key: 'failed', label: '解析失败', n: failed.length, hot: true }, { key: 'parsing', label: '正在解析', n: parsing.length }]}>
          {list.length === 0 ? <Empty>{corpusTab === 'review' ? '没有等审核的上传。' : corpusTab === 'failed' ? '没有解析失败的文件。' : '没有正在解析的文件。'}</Empty> : (
            <table className="ah-table">
              <thead><tr><th>文件</th><th>{corpusTab === 'failed' ? '原因' : '谁传的'}</th><th className="r">上传</th>{corpusTab !== 'parsing' && <th />}</tr></thead>
              <tbody>
                {list.slice(0, 6).map((d) => (
                  <tr key={d.id}>
                    <td className="ah-ell">{d.title || d.fileName}</td>
                    <td className="ah-mute ah-ell2">{corpusTab === 'failed' ? (d.parseError ?? '没写原因') : (d.uploaderName ?? '')}</td>
                    <td className="r ah-mute">{when(d.uploadedAt)}</td>
                    {corpusTab === 'review' && (
                      <td className="r ah-acts">
                        <button type="button" className="ah-btn dark" disabled={busy} onClick={() => void run(() => post(ADMIN_REVIEW_API.approve, { docIds: [d.id] }), [['docs-all'], [ADMIN_REVIEW_API.key]])}>通过</button>
                        <button type="button" className="ah-btn" onClick={() => nav('/admin/corpus?tab=review')}>退回</button>
                      </td>
                    )}
                    {corpusTab === 'failed' && <td className="r"><More to="/admin/corpus">去处理</More></td>}
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {corpusTab === 'review' && pending.length > 0 && <div className="ah-foot">通过后自动解析入库；要改密级或退回，到资料管理里审核。</div>}
        </TabCard>
      ),
    });
    const weekStart = startOfWeek(now).getTime();
    const thisWeek = rows.filter((d) => new Date(d.uploadedAt).getTime() >= weekStart);
    rail.push({
      key: 'corpus-week', order: 30, node: (
        <Card title="这周">
          <Rows rows={[['上传', `${thisWeek.length} 份`], ['正在解析', `${parsing.length} 份`], ['解析失败', failed.length > 0 ? <span className="ah-red">{failed.length} 份</span> : '0'], ['资料总数', `${rows.length.toLocaleString()} 份`]]} />
        </Card>
      ),
    });
  }

  // ── 售后：工单、案例 ───────────────────────────────
  if (canTickets || canCases) titles.push('售后');
  if (canTickets) {
    const all = tickets.data ?? [];
    const open = all.filter((t) => t.status !== 'Resolved' && t.status !== 'Closed');
    const late = open.filter((t) => now.getTime() - new Date(t.createdAt).getTime() > 2 * DAY);
    const sorted = [...late, ...open.filter((t) => !late.includes(t)).sort((a, b) => b.createdAt.localeCompare(a.createdAt))];
    if (open.length > 0) facts.push({ order: 30, node: <><B>{open.length} 张</B>工单没结{late.length > 0 && <>，其中 <B>{late.length} 张</B>超过 48 小时</>}</> });
    const list = ticketTab === 'late' ? late : sorted;
    main.push({
      key: 'tickets', order: 30, node: (
        <TabCard cur={ticketTab} onTab={setTicketTab} right={<More to="/tickets">全部工单</More>}
          tabs={[{ key: 'open', label: '没结的', n: open.length, hot: true }, { key: 'late', label: '超过 48 小时', n: late.length, hot: true }]}>
          {list.length === 0 ? <Empty>没有没结的工单。</Empty> : (
            <table className="ah-table">
              <thead><tr><th>单号</th><th>客户</th><th>问题</th><th>状态</th><th>处理人</th><th className="r">报修</th></tr></thead>
              <tbody>
                {list.slice(0, 6).map((t) => (
                  <tr key={t.id}>
                    <td className="ah-mono">{t.ticketNo}</td><td>{t.customerNo}</td><td className="ah-ell ah-mute">{t.description}</td>
                    <td>{late.includes(t) ? <Tag tone="red">超 48 小时</Tag> : <Tag tone={t.status === 'Submitted' ? 'amber' : 'mute'}>{TICKET_LABEL[t.status]}</Tag>}</td>
                    <td className="ah-mute">{t.assigneeName ?? '未分配'}</td><td className="r ah-mute">{when(t.createdAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </TabCard>
      ),
    });
    const monthStart = new Date(now.getFullYear(), now.getMonth(), 1).getTime();
    const done = all.filter((t) => (t.status === 'Resolved' || t.status === 'Closed') && new Date(t.updatedAt).getTime() >= monthStart);
    const fresh = all.filter((t) => new Date(t.createdAt).getTime() >= monthStart);
    rail.push({ key: 'tickets-month', order: 40, node: <Card title="这个月"><Rows rows={[['新报修', `${fresh.length} 张`], ['已结', `${done.length} 张`], ['没结', `${open.length} 张`]]} /></Card> });
  }
  if (canCases) {
    const list = cases.data ?? [];
    if (list.length > 0) facts.push({ order: 31, node: <><B>{list.length} 条</B>故障案例等你审定</> });
    main.push({
      key: 'cases', order: 31, node: (
        <Card title="等你审定的故障案例" sub="审定后同事才查得到" right={<More to="/review">全部</More>}>
          {list.length === 0 ? <Empty>没有等审定的案例。</Empty> : (
            <table className="ah-table">
              <thead><tr><th>案例</th><th>设备</th><th className="r">提交</th><th /></tr></thead>
              <tbody>
                {list.slice(0, 5).map((c) => (
                  <tr key={c.id}>
                    <td className="ah-ell">{c.phenomenon}</td><td className="ah-mute">{c.deviceModel}</td><td className="r ah-mute">{when(c.createdAt)}</td>
                    <td className="r"><button type="button" className="ah-btn dark" onClick={() => nav('/review')}>审定</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </Card>
      ),
    });
  }

  // ── 价格库 ─────────────────────────────────────────
  if (canPrices) {
    titles.push('价格库');
    const p = prices.data;
    if (p && p.staleCount > 0) facts.push({ order: 40, node: <><B>{p.staleCount} 种</B>材料的价格超过 6 个月没更新</> });
    main.push({
      key: 'prices', order: 40, node: (
        <Card title="该更新的价格" sub="同一种材料看最新的价；最旧的在前" right={<More to="/admin/prices">价格库</More>}>
          {!p || p.stale.length === 0 ? <Empty>{p ? '价格都在 6 个月以内。' : '读取中…'}</Empty> : (
            <table className="ah-table">
              <thead><tr><th>材料</th><th>类别</th><th>供应商</th><th className="r">价格日期</th><th className="r">多久没更新</th></tr></thead>
              <tbody>
                {p.stale.map((s, i) => (
                  <tr key={i}>
                    <td className="ah-ell">{s.name}{s.spec ? <span className="ah-mute"> {s.spec}</span> : null}</td>
                    <td className="ah-mute">{s.group ?? ''}</td><td className="ah-mute">{s.supplier ?? ''}</td>
                    <td className="r ah-mute ah-mono">{s.date.slice(0, 7)}</td>
                    <td className="r"><Tag tone={s.months >= 9 ? 'red' : 'amber'}>{s.months} 个月</Tag></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {p && p.staleCount > p.stale.length && <div className="ah-foot">还有 {p.staleCount - p.stale.length} 种，到价格库里按日期排序看。</div>}
        </Card>
      ),
    });
    if (p) {
      rail.push({
        key: 'prices-month', order: 60, node: (
          <Card title="这个月">
            <Rows rows={[['助手查价格', <>{p.lookupsThisMonth} 次 <Delta cur={p.lookupsThisMonth} prev={p.lookupsPrevMonth} /></>], ['上传价格表', `${p.uploadsThisMonth} 份 · ${p.itemsThisMonth.toLocaleString()} 条`],
              ['现行价格', `${p.items.toLocaleString()} 条`], ['供应商', `${p.suppliers} 家`]]} />
          </Card>
        ),
      });
      if (p.recent.length > 0) rail.push({ key: 'prices-recent', order: 61, node: <Card title="最近上传" right={<More to="/admin/prices">全部</More>}><Rows rows={p.recent.map((r): [ReactNode, ReactNode] => [<span className="ah-ell" key="f">{r.file}</span>, <span className="ah-mute" key="d">{when(r.uploadedAt)} · {r.items} 条</span>])} /></Card> });
    }
  }

  // ── 用量（审计与统计）──────────────────────────────
  if (canStats) {
    const t = daily.data?.totals;
    if (t) facts.push({ order: 70, node: <>近 30 天 <B>{t.activeUsers} 位</B>同事用了 <B>{t.calls.toLocaleString()} 次</B>{t.prevCalls > 0 && `，比前 30 天${t.calls >= t.prevCalls ? '多' : '少'} ${Math.abs(Math.round(((t.calls - t.prevCalls) / t.prevCalls) * 100))}%`}</> });
    const thisWeek = startOfWeek(now);
    const byWeek = new Map((usage.data ?? []).map((w) => [w.weekStart, w]));
    const weeks = Array.from({ length: 13 }, (_, i) => {
      const d = new Date(thisWeek); d.setDate(d.getDate() - 7 * (12 - i));
      const w = byWeek.get(ymd(d));
      const v = metric === 'calls' ? (w?.totalCalls ?? 0) : metric === 'users' ? (w?.activeUsers ?? 0) : (w?.hitRate ?? null);
      return { label: i === 12 ? '本周' : `${d.getMonth() + 1}/${d.getDate()}`, v };
    });
    const tabs: { key: typeof metric; label: string; value: string; unit: string; delta: ReactNode }[] = [
      { key: 'calls', label: '调用次数', value: t ? t.calls.toLocaleString() : '—', unit: '近 30 天', delta: t && <Delta cur={t.calls} prev={t.prevCalls} /> },
      { key: 'users', label: '在用的人', value: t ? String(t.activeUsers) : '—', unit: t ? `/ ${t.totalUsers} 人` : '', delta: t && <Delta cur={t.activeUsers} prev={t.prevActiveUsers} unit=" 人" /> },
      { key: 'hit', label: '资料命中率', value: t?.hitRate != null ? `${t.hitRate.toFixed(1)}%` : '—', unit: '', delta: t?.hitRate != null && t.prevHitRate != null && <Delta cur={t.hitRate} prev={t.prevHitRate} unit=" 点" /> },
    ];
    main.push({
      key: 'usage', order: 50, node: (
        <section className="ah-card">
          <div className="ah-metrics" role="tablist">
            {tabs.map((m) => (
              <button key={m.key} type="button" role="tab" aria-selected={metric === m.key} className={'ah-metric' + (metric === m.key ? ' on' : '')} onClick={() => setMetric(m.key)}>
                <span className="ah-metric-l">{m.label}</span>
                <span className="ah-metric-v"><b>{m.value}</b><span>{m.unit}</span>{m.delta}</span>
              </button>
            ))}
          </div>
          <WeekBars weeks={weeks} fmt={(v) => (metric === 'hit' ? `${Math.round(v)}%` : v.toLocaleString())} />
        </section>
      ),
    });
    const dr = depts.data?.rows ?? [];
    if (dr.length > 1) {
      main.push({
        key: 'depts', order: 52, node: (
          <Card title="各部门" sub="近 30 天" right={<More to="/admin/audit">按人看</More>}>
            <table className="ah-table">
              <thead><tr><th>部门</th><th className="r">在用 / 人数</th><th className="r">调用</th><th className="r">较前 30 天</th></tr></thead>
              <tbody>
                {dr.map((r) => (
                  <tr key={r.name}>
                    <td>{r.name}</td><td className="r">{r.active} / {r.members}</td><td className="r">{r.calls.toLocaleString()}</td>
                    <td className="r">{r.prevCalls === 0 ? (r.calls > 0 ? <Tag>新用起来</Tag> : <span className="ah-mute">—</span>) : <Delta cur={r.calls} prev={r.prevCalls} />}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
        ),
      });
    }
    const mods = (daily.data?.moduleRows ?? []).filter((m) => m.n > 0).sort((a, b) => b.n - a.n);
    const mt = mods.reduce((s, m) => s + m.n, 0);
    if (mt > 0) rail.push({ key: 'modules', order: 70, node: <Card title="主要用来做什么" sub="近 30 天"><Bars rows={mods.map((m) => ({ name: m.name, n: m.n, note: `${Math.round((m.n / mt) * 100)}%` }))} /></Card> });
    const q = quality.data;
    if (q) rail.push({
      key: 'quality', order: 71, node: (
        <Card title="回答质量" sub="近 30 天" right={canCorpus ? <More to="/admin/quality">明细</More> : undefined}>
          <Rows rows={[['回答', `${q.answers.toLocaleString()} 次`], ['点了「有帮助」', q.helpful], ['点了「没帮上」', <>{q.notHelpful} <Delta cur={q.notHelpful} prev={q.prevNotHelpful} goodUp={false} /></>], ['资料里找不到答案', `${q.noResult} 个问题`]]} />
        </Card>
      ),
    });
    const g = (gaps.data ?? []).slice(0, 6);
    if (g.length > 0) {
      main.push({
        key: 'gaps', order: canCorpus ? 25 : 54, node: (
          <Card title="没答上来的问题" sub="近 30 天 · 补上资料，这些就能答" right={(gaps.data?.length ?? 0) > g.length ? <More to="/admin/audit">全部 {gaps.data!.length} 条</More> : undefined}>
            <table className="ah-table">
              <thead><tr><th>问题</th><th className="r">次数</th><th className="r">最近</th>{canCorpus && <th />}</tr></thead>
              <tbody>
                {g.map((x) => (
                  <tr key={x.question}>
                    <td className="ah-ell">{x.question}</td><td className="r">{x.count}</td><td className="r ah-mute">{when(x.lastAt)}</td>
                    {canCorpus && <td className="r"><NavLink to="/admin/corpus" className="ah-more">补资料</NavLink></td>}
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
        ),
      });
    }
  }

  // ── 系统（系统设置）────────────────────────────────
  if (canOps) {
    const items: [ReactNode, ReactNode][] = [];
    const m = model.data;
    if (m) items.push(['对话模型', m.mode === 'stub' ? <><span className="ah-amber">演示模式</span><Dot tone="warn" /></> : m.online ? <>{m.name.split('/').pop()}<Dot tone="ok" /></> : <><span className="ah-red">不在线</span><Dot tone="bad" /></>]);
    if (m && m.mode !== 'stub' && !m.online) facts.push({ order: 1, node: <>对话模型<B>不在线</B>，同事现在问不了问题</> });
    const c = consistency.data;
    if (c) items.push(['向量索引', c.inconsistent > 0 ? <><span className="ah-amber">{c.inconsistent} 个要重建</span><Dot tone="warn" /></> : <>{c.totalChunks.toLocaleString()} 一致<Dot tone="ok" /></>]);
    const b = backup.data;
    if (b) {
      const local = b.rows.filter((r) => r.kind !== 'RemoteFull' && r.last).sort((x, y) => (y.last!.startedAt > x.last!.startedAt ? 1 : -1))[0];
      items.push(['本地备份', local ? (local.last!.ok === false ? <><span className="ah-red">{when(local.last!.startedAt)} 失败</span><Dot tone="bad" /></> : <>{when(local.last!.startedAt)}<Dot tone="ok" /></>) : <><span className="ah-amber">还没有</span><Dot tone="warn" /></>]);
      const remote = b.rows.find((r) => r.kind === 'RemoteFull');
      if (remote && !remote.configured) {
        items.push(['异地备份', <><span className="ah-amber">未配置</span><Dot tone="warn" /></>]);
        facts.push({ order: 5, node: <>异地备份还没配置</> });
      } else if (remote?.last) items.push(['异地备份', remote.last.ok === false ? <><span className="ah-red">失败</span><Dot tone="bad" /></> : <>{when(remote.last.startedAt)}<Dot tone="ok" /></>]);
    }
    rail.push({ key: 'system', order: 10, node: <Card title="系统" right={<More to="/admin/settings">设置</More>}><Rows rows={items} /></Card> });
    const ops = (audit.data ?? []).filter((a) => AUDIT_VERB[a.action]).slice(0, 6);
    if (ops.length > 0) main.push({
      key: 'ops', order: 90, node: (
        <Card title="最近操作" right={<More to="/admin/audit">审计日志</More>}>
          <table className="ah-table">
            <thead><tr><th>操作</th><th>操作人</th><th className="r">时间</th></tr></thead>
            <tbody>
              {ops.map((a) => (
                <tr key={a.id}>
                  <td className="ah-ell">{AUDIT_VERB[a.action]} <span className="ah-mute">{detailName(a.detail) ?? ''}</span></td>
                  <td className="ah-mute">{a.username ?? '系统'}</td><td className="r ah-mute">{when(a.at)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      ),
    });
  }

  // ── 要处理（别处没列的零碎待办）─────────────────────
  const todo: [ReactNode, ReactNode][] = [];
  if (canCorpus && qOpen.data) todo.push(['没帮上的回答', qOpen.data.open > 0 ? <><b className="ah-red">{qOpen.data.open}</b><More to="/admin/quality">处理</More></> : <span className="ah-mute">0</span>]);
  if (canTerms && terms.data) todo.push(['待确认术语', terms.data.length > 0 ? <><b className="ah-red">{terms.data.length}</b><More to="/admin/meta">术语表</More></> : <span className="ah-mute">0</span>]);
  if (canClauses && clauses.data) todo.push(['待审定条款', clauses.data.length > 0 ? <><b className="ah-red">{clauses.data.length}</b><More to="/admin/templates">条款库</More></> : <span className="ah-mute">0</span>]);
  if (todo.length > 0) rail.push({ key: 'todo', order: 20, node: <Card title="要处理"><Rows rows={todo} /></Card> });

  // ── 账号 ───────────────────────────────────────────
  if (canUsers && users.data) {
    const emp = users.data.filter((u) => u.kind === 'Employee');
    const active = emp.filter((u) => u.isActive);
    const weekStart = startOfWeek(now).getTime();
    const idle = active.filter((u) => !u.lastLoginAt || now.getTime() - new Date(u.lastLoginAt).getTime() > 30 * DAY);
    rail.push({
      key: 'users', order: 25, node: (
        <Card title="账号" right={<More to="/admin/users">用户与权限</More>}>
          <Rows rows={[['在用账号', active.length], ['本周新建', emp.filter((u) => new Date(u.createdAt).getTime() >= weekStart).length],
            ['30 天没登录', idle.length > 0 ? <><span className="ah-amber">{idle.length}</span><More to="/admin/users">看是谁</More></> : '0'], ['停用', <span className="ah-mute" key="x">{emp.length - active.length}</span>]]} />
        </Card>
      ),
    });
  }

  // ── 知识库 ─────────────────────────────────────────
  if (canKbs && kbs.data) {
    const list = kbs.data.filter((k) => k.isActive);
    rail.push({ key: 'kbs', order: 35, node: <Card title="知识库" right={<More to="/admin/kbs">管理</More>}>{list.length === 0 ? <Empty>还没有知识库。</Empty> : <Rows rows={list.map((k): [ReactNode, ReactNode] => [k.name, `${k.docCount.toLocaleString()} 份`])} />}</Card> });
  }

  // ── 页头 ───────────────────────────────────────────
  const title = canOps || titles.length !== 1 ? '概览' : titles[0];
  const sentence = facts.sort((a, b) => a.order - b.order).slice(0, 3);
  const alertRows = (alerts.data ?? []).filter((a) => !a.acknowledgedAt);
  main.sort((a, b) => a.order - b.order);
  rail.sort((a, b) => a.order - b.order);

  return (
    <div className="sc ah" style={{ flexGrow: 1, minHeight: 0 }}>
      <div className="ah-in">
        <header className="ah-head">
          <div className="ah-head-t">
            <span className="ah-date">{now.getMonth() + 1} 月 {now.getDate()} 日 周{WEEKDAY[now.getDay()]}</span>
            <h1>{title}</h1>
            <p>
              {sentence.length === 0 ? (canStats || canOps ? '系统在正常运行，今天没有要处理的事。' : '今天没有要处理的事。')
                : sentence.map((f, i) => <Fragment key={i}>{f.node}{i < sentence.length - 1 ? '；' : '。'}</Fragment>)}
            </p>
          </div>
          <div className="ah-head-a">
            {canUsers && <button className="gbtn ah-hbtn" onClick={() => nav('/admin/users')}>新建用户</button>}
            {canPrices && !canCorpus && <button className="pbtn ah-hbtn" onClick={() => nav('/admin/prices')}>上传价格表</button>}
            {canCorpus && <button className="pbtn ah-hbtn" onClick={() => nav('/admin/corpus')}>上传资料</button>}
          </div>
        </header>

        {isHead && myDept.data?.name && <div className="ah-scope"><Icon name="eye" size={14} />这一页本部门的数字只算「{myDept.data.name}」，别的部门看不到。</div>}

        {/* 告警（FR-9.3：任务失败须告警，不得仅写日志）：知悉不撤，同类型成功一次才消 */}
        {alertRows.map((a) => (
          <div key={a.runId} className="ah-alert">
            <Icon name="warn" size={15} />
            <span><b>{BACKUP_LABEL[a.kind] ?? a.kind}备份失败{a.consecutiveFailures > 1 ? `（连续 ${a.consecutiveFailures} 次）` : ''}。</b>{a.error ?? '未记录具体原因'} · {when(a.startedAt)}</span>
            <button type="button" className="ah-btn" disabled={busy} onClick={() => void run(() => post(`/api/ops/backup/${a.runId}/ack`), [['ops-alerts']])}>我已知悉</button>
          </div>
        ))}

        <div className={'ah-grid' + (rail.length === 0 ? ' solo' : '')}>
          <div className="ah-col">{main.map((b) => <Fragment key={b.key}>{b.node}</Fragment>)}</div>
          {rail.length > 0 && <div className="ah-col ah-rail">{rail.map((b) => <Fragment key={b.key}>{b.node}</Fragment>)}</div>}
        </div>
        {main.length === 0 && rail.length === 0 && <Empty>你的账号还没有后台里能看的内容。</Empty>}
      </div>
    </div>
  );
}
