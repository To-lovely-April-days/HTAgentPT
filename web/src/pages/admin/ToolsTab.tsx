// 系统设置 › 助手工具：助手回答时可以自己调用这些工具，什么时候用、用哪个由它判断。
// 这里只决定：开哪些、给哪些角色用；外加运行环境检查和联网搜索的设置。每次调用都记入审计。
import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put, ApiError } from '../../lib/api';
import type { RoleRow } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';
import { KbMultiSelect } from '../../components/KbMultiSelect';
import { Icon } from '../../components/Icons';

interface Stats { total: number; asks: number; tools: { tool: string; calls: number; failed: number; avgMs: number }[] }
interface Health { sandbox: boolean; search: boolean; hazchem: boolean; office: boolean; epo: boolean; vision: boolean; memory: { users: number; total: number } }

interface ToolDef { key: string; name: string; ic: string; tone: 'in' | 'calc' | 'ext'; tag?: string; desc: string; fixed?: boolean; settings?: Panel;
  /** 开关用的配置键（默认 assistant.tool.{key}）；noRoles：不按角色分配。 */ cfgKey?: string; noRoles?: boolean }
type Panel = 'search' | 'sandbox' | 'epo';
const GROUPS: { title: string; note: string; tools: ToolDef[] }[] = [
  { title: '公司内部', note: '始终按权限、密级过滤', tools: [
    { key: 'search_documents', name: '搜资料 · 打开原文', ic: '资', tone: 'in', desc: '在知识库里搜、读原文，回答带编号引用', fixed: true },
    { key: 'query_projects', name: '查项目台账', ic: '台', tone: 'in', desc: '做过的项目；金额只给有「机密」密级的人', fixed: true },
    { key: 'search_standards', name: '查标准规范', ic: '标', tone: 'in', desc: '只在「标准规范」类资料里搜，回答写清标准号和条款；上传规范时类别选「标准规范」', fixed: true },
    { key: 'ask_user', name: '请用户确认', ic: '选', tone: 'in', desc: '有歧义或查到多个候选时，给选项让人点' },
    { key: 'inquiry_form', name: '询价缺项单', ic: '单', tone: 'in', desc: '需求不全时列出已知项和缺的项，可当场填、导出发给客户' },
    { key: 'search_prices', name: '查价格 · 材料成本估算', ic: '价', tone: 'in', desc: '在价格库里查采购价，按用量估材料成本（不含人工和利润）；还要有「查看采购价格」权限' },
    { key: 'memory', name: '长期记忆', ic: '记', tone: 'in', desc: '记住每个人交代的偏好、工作范围、在跟的客户；内容只有本人能看、能删', cfgKey: 'assistant.memory', noRoles: true },
  ] },
  { title: '计算与文件', note: '在服务器上运行，不出公司', tools: [
    { key: 'run_python', name: '代码解释器', ic: '算', tone: 'calc', tag: '免费', desc: '隔离环境里跑 Python：工程计算、数据分析、作图；含物性计算（CoolProp）', settings: 'sandbox' },
    { key: 'compare_documents', name: '文件对比', ic: '比', tone: 'calc', tag: '免费', desc: '两份文件逐条对照：需求书对方案，找没满足的' },
    { key: 'view_image', name: '看图 · 识别文字', ic: '图', tone: 'calc', desc: '看上传的图纸、照片、截图；把扫描件和手写单的文字读出来（用看图模型）' },
    { key: 'translate_file', name: '整份翻译', ic: '译', tone: 'calc', desc: '上传的 Word 整份翻译，保留版式、套用已审定术语；需要「翻译」权限' },
    { key: 'show_table', name: '按场景排版表格', ic: '表', tone: 'calc', tag: '免费', desc: '比价、涨跌、历史项目、方案对比、参数清单各用各的表格样式，可导出 Excel' },
    { key: 'show_block', name: '回答里的组件', ic: '件', tone: 'calc', tag: '免费', desc: '关键数字、提示框、图表、步骤、清单、数据表、偏离表、操作窗口、甘特图等 21 种，助手按内容选用' },
    { key: 'export_pdf', name: '导出 PDF / PPT', ic: '出', tone: 'calc', tag: '免费', desc: '回答导出 PDF（服务器上 LibreOffice 转换）和 PPT（浏览器里生成）', fixed: true },
  ] },
  { title: '外部数据', note: '会访问外网；发出去的内容先过滤', tools: [
    { key: 'fetch_url', name: '打开网页', ic: '网', tone: 'ext', tag: '免费', desc: '读用户给的链接或搜索结果里的网页全文（只连公网地址）' },
    { key: 'web_search', name: '联网搜索', ic: '搜', tone: 'ext', tag: '试用', desc: '自建 SearXNG 聚合必应、百度、搜狗；偶尔会搜不到', settings: 'search' },
    { key: 'chemical_info', name: '化学品安全数据', ic: '化', tone: 'ext', tag: '免费', desc: 'PubChem 查 GHS 危险性、闪点等；本地《危险化学品目录》判断是否危化品' },
    { key: 'search_literature', name: '专利 / 论文检索', ic: '献', tone: 'ext', tag: '免费', desc: '论文用 OpenAlex（不用注册）；专利用欧洲专利局 OPS（含中国专利，需填免费密钥）', settings: 'epo' },
  ] },
];
const STAT_KEY: Record<string, string[]> = {
  search_documents: ['search_documents', 'read_document'], query_projects: ['query_projects', 'list_catalog'],
};

export default function ToolsTab() {
  const qc = useQueryClient();
  const cfg = useQuery({ queryKey: ['config'], queryFn: () => get<Record<string, string>>('/api/config') });
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => get<RoleRow[]>('/api/roles') });
  const stats = useQuery({ queryKey: ['tool-stats'], queryFn: () => get<Stats>('/api/ops/tools/stats') });
  const health = useQuery({ queryKey: ['tool-health'], queryFn: () => get<Health>('/api/ops/tools/health') });
  const [panel, setPanel] = useState<Panel | null>(null);
  const [error, setError] = useState<string | null>(null);

  const c = cfg.data ?? {};
  const keyOf = (t: ToolDef) => t.cfgKey ?? `assistant.tool.${t.key}`;
  const isOn = (t: ToolDef) => ['on', 'true'].includes((c[keyOf(t)] ?? 'on').toLowerCase());
  const rolesOf = (k: string) => (c[`assistant.tool.${k}.roles`] ?? '').split(',').map((x) => x.trim()).filter(Boolean);
  const save = async (values: Record<string, string>) => {
    setError(null);
    try { await put('/api/config', { values }); void qc.invalidateQueries({ queryKey: ['config'] }); }
    catch (err) { setError(err instanceof ApiError ? err.message : '保存失败'); }
  };
  const callsOf = (k: string) => (stats.data?.tools ?? []).filter((t) => (STAT_KEY[k] ?? [k]).includes(t.tool)).reduce((s, t) => s + t.calls, 0);
  const failedOf = (k: string) => (stats.data?.tools ?? []).filter((t) => (STAT_KEY[k] ?? [k]).includes(t.tool)).reduce((s, t) => s + t.failed, 0);
  const status = (t: ToolDef): [string, 'ok' | 'warn' | 'off'] => {
    if (!t.fixed && !isOn(t)) return ['已关闭', 'off'];
    const h = health.data;
    if (!h) return ['检查中…', 'off'];
    if (t.key === 'memory') return [`${h.memory.users} 人记了 ${h.memory.total} 条（这里只显示条数）`, 'ok'];
    if (t.key === 'view_image') return h.vision ? ['看图模型已开启', 'ok'] : ['看图模型没开启（参数里打开「看图」并配好模型）', 'warn'];
    if (t.key === 'export_pdf') return h.office ? ['转换服务正常', 'ok'] : ['PDF 转换服务连不上（office 容器没起来？）· PPT 不受影响', 'warn'];
    if (t.key === 'search_literature') return h.epo ? ['论文 · 专利都可查', 'ok'] : ['只能查论文 · 专利还没填密钥', 'warn'];
    if (t.key === 'run_python') return health.data ? (health.data.sandbox ? ['运行环境正常', 'ok'] : ['运行环境连不上（sandbox 容器没起来？）', 'warn']) : ['检查中…', 'off'];
    if (t.key === 'web_search') return health.data ? (health.data.search ? ['搜索服务正常', 'ok'] : ['搜索服务连不上（searxng 容器没起来？）', 'warn']) : ['检查中…', 'off'];
    if (t.key === 'chemical_info') return health.data ? (health.data.hazchem ? ['正常', 'ok'] : ['正常 · 本地危化品目录未导入（只查 PubChem）', 'warn']) : ['检查中…', 'off'];
    return ['正常', 'ok'];
  };
  const roleOptions = (roles.data ?? []).filter((r) => r.code !== 'customer').map((r) => ({ id: r.code, name: r.name }));
  const byTool = (k: string) => stats.data?.tools.find((t) => t.tool === k);

  return (
    <div className="acc">
      <div className="acc-main sc">
        <div>
          <h1 className="dash-title" style={{ fontSize: 24 }}>助手工具</h1>
          <p className="dash-sub">助手回答问题时可以自己调用这些工具：什么时候用、用哪个由它判断。这里决定开哪些、给谁用。每次调用都记入审计；外部客户只能用「请用户确认」。</p>
        </div>
        {error && <ErrorBox message={error} />}
        <section className="us-sum" aria-label="近 7 天">
          <div className="us-sum-c"><span className="dash-kpi-l">近 7 天工具调用</span><span className="us-sum-v">{stats.data?.total ?? '—'}<small>次</small></span></div>
          <div className="us-sum-c"><span className="dash-kpi-l">请用户确认</span><span className="us-sum-v">{stats.data?.asks ?? '—'}<small>次</small></span></div>
          <div className="us-sum-c"><span className="dash-kpi-l">联网搜索</span><span className="us-sum-v">{byTool('web_search')?.calls ?? 0}<small>次</small></span>
            <span className="us-sum-d flat">失败 {byTool('web_search')?.failed ?? 0} 次</span></div>
          <div className="us-sum-c"><span className="dash-kpi-l">代码运行</span><span className="us-sum-v">{byTool('run_python')?.calls ?? 0}<small>次</small></span>
            <span className="us-sum-d flat">平均 {((byTool('run_python')?.avgMs ?? 0) / 1000).toFixed(1)} 秒</span></div>
        </section>

        {cfg.isLoading && <Spinner text="载入…" />}
        {GROUPS.map((g) => (
          <section key={g.title} className="dash-card" style={{ padding: 0, overflow: 'visible' }}>
            <div className="tl-hd"><b>{g.title}</b><span className="dash-muted">{g.note}</span></div>
            {g.tools.map((t) => {
              const [st, tone] = status(t);
              const on = t.fixed || isOn(t);
              return (
                <div key={t.key} className="tl-row">
                  <span className={'tl-ic ' + t.tone} aria-hidden="true">{t.ic}</span>
                  <div className="tl-main">
                    <span><b>{t.name}</b>{t.tag && <em className={'tl-tag ' + (t.tag === '试用' ? 'try' : 'free')}>{t.tag}</em>}</span>
                    <span className="tl-desc">{t.desc}</span>
                    <span className={'tl-st ' + tone}><Icon name="dot" size={12} style={{ verticalAlign: '-2px' }} /> {st}</span>
                  </div>
                  <div className="tl-roles">
                    <span className="dash-muted" style={{ fontSize: 12 }}>谁能用</span>
                    {t.fixed || t.noRoles ? <span style={{ fontSize: 12.5 }}>{t.noRoles ? '每人自己开关' : '按功能权限'}</span> : (
                      <KbMultiSelect options={roleOptions} value={rolesOf(t.key)} placeholder="所有内部员工"
                        onChange={(v) => void save({ [`assistant.tool.${t.key}.roles`]: v.join(',') })} />
                    )}
                  </div>
                  <div className="tl-usage">近 7 天 {callsOf(t.key)} 次{failedOf(t.key) > 0 && <span style={{ color: '#8a5a00' }}> · 失败 {failedOf(t.key)}</span>}</div>
                  {t.settings ? <button type="button" className="fm-act" onClick={() => setPanel(t.settings!)}>设置</button> : <span style={{ width: 28 }} />}
                  <button type="button" role="switch" aria-checked={on} aria-label={`${t.name}开关`} className={'tl-sw' + (on ? ' on' : '')} disabled={t.fixed}
                    onClick={() => void save({ [keyOf(t)]: on ? 'off' : 'on' })}><i /></button>
                </div>
              );
            })}
          </section>
        ))}
      </div>
      {panel === 'epo' && <EpoPanel cfg={c} onClose={() => { setPanel(null); void qc.invalidateQueries({ queryKey: ['tool-health'] }); }} onSave={save} />}
      {panel && panel !== 'epo' && <SettingsPanel kind={panel} cfg={c} onClose={() => setPanel(null)} onSave={save} />}
    </div>
  );
}

const ENGINES = [['bing', '必应'], ['baidu', '百度'], ['sogou', '搜狗'], ['360search', '360']] as const;

function SettingsPanel({ kind, cfg, onClose, onSave }: {
  kind: 'search' | 'sandbox'; cfg: Record<string, string>; onClose: () => void; onSave: (v: Record<string, string>) => Promise<void>;
}) {
  const [url, setUrl] = useState(kind === 'search' ? cfg['assistant.searx_url'] ?? 'http://searxng:8080' : cfg['assistant.sandbox_url'] ?? 'http://sandbox:8000');
  const [engines, setEngines] = useState((cfg['assistant.searx_engines'] ?? 'bing,baidu,sogou').split(',').filter(Boolean));
  const [timeout, setTimeoutS] = useState(cfg['assistant.sandbox_timeout'] ?? '30');
  const [q, setQ] = useState('哈氏合金 C276 耐盐酸腐蚀');
  const [test, setTest] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const runTest = async () => {
    setBusy(true); setTest(null);
    try {
      await onSave({ 'assistant.searx_url': url, 'assistant.searx_engines': engines.join(',') });
      const r = await post<{ ok: boolean; seconds?: number; count?: number; engines?: Record<string, number>; error?: string; redacted?: boolean; query: string }>('/api/ops/tools/test-search', { query: q });
      setTest(r.ok
        ? { ok: true, text: `${r.seconds} 秒 · 返回 ${r.count} 条（${Object.entries(r.engines ?? {}).map(([k, v]) => `${k} ${v}`).join(' · ')}）${r.redacted ? ` · 发出去的是「${r.query}」` : ''}` }
        : { ok: false, text: `没搜到：${r.error ?? '搜索服务没有返回结果'}` });
    } catch (err) { setTest({ ok: false, text: err instanceof ApiError ? err.message : '测试失败' }); }
    finally { setBusy(false); }
  };
  return (
    <aside className="acc-drawer" aria-label="工具设置">
      <div className="acc-drawer-hd"><b>{kind === 'search' ? '联网搜索 · 设置' : '代码解释器 · 设置'}</b><button type="button" className="refs-x" aria-label="关闭" onClick={onClose}><Icon name="x" size={14} style={{ display: 'block' }} /></button></div>
      <div className="acc-drawer-body sc">
        {kind === 'search' ? (
          <>
            <p className="dash-muted" style={{ margin: 0, lineHeight: 1.7 }}>用服务器上自建的搜索聚合（SearXNG，免费）。它把问题转给必应、百度、搜狗等再合并结果，不需要账号和密钥。偶尔会被搜索引擎临时拦截，表现为「这次没搜到」。</p>
            <label className="acc-field"><span>搜索服务地址</span><input className="inp m" style={{ height: 38 }} value={url} onChange={(e) => setUrl(e.target.value)} /></label>
            <div className="acc-field"><span>用哪些搜索引擎</span>
              <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                {ENGINES.map(([k, n]) => (
                  <button key={k} type="button" className={'rv-chip' + (engines.includes(k) ? ' on' : '')}
                    onClick={() => setEngines((e) => (e.includes(k) ? e.filter((x) => x !== k) : [...e, k]))}>{n}{engines.includes(k) && <Icon name="check" size={13} stroke={2.2} style={{ marginLeft: 4 }} />}</button>
                ))}
              </div>
            </div>
            <div className="acc-preview"><b>发出去之前系统会拦掉：</b>客户名单里的名称 · 台账里的项目编号 · 金额与报价。被拦掉的部分换成「某客户」「某项目」，助手照常回答。</div>
            <div className="acc-field"><span>试一下</span>
              <span style={{ display: 'flex', gap: 6 }}>
                <input className="inp" style={{ height: 38, flex: 1 }} value={q} onChange={(e) => setQ(e.target.value)} />
                <button type="button" className="gbtn" disabled={busy || !q.trim()} onClick={() => void runTest()}>{busy ? '搜索中…' : '搜索'}</button>
              </span>
              {test && <small style={{ color: test.ok ? '#1c6b45' : 'var(--brand-fg)' }}>{test.ok && <Icon name="check" size={13} style={{ verticalAlign: '-2px', marginRight: 4 }} />}{test.text}</small>}
            </div>
          </>
        ) : (
          <>
            <p className="dash-muted" style={{ margin: 0, lineHeight: 1.7 }}>代码在服务器上一个隔离的容器里运行：不能上网、碰不到数据库和公司文件，每次限时、限内存，跑完就清掉。</p>
            <label className="acc-field"><span>运行服务地址</span><input className="inp m" style={{ height: 38 }} value={url} onChange={(e) => setUrl(e.target.value)} /></label>
            <label className="acc-field"><span>每次最长运行（秒）</span><input className="inp m" style={{ height: 38, width: 120 }} inputMode="numeric" value={timeout} onChange={(e) => setTimeoutS(e.target.value.replace(/\D/g, ''))} /></label>
          </>
        )}
      </div>
      <div className="acc-drawer-f">
        <button type="button" className="gbtn" onClick={onClose}>取消</button>
        <button type="button" className="pbtn" onClick={() => void onSave(kind === 'search'
          ? { 'assistant.searx_url': url, 'assistant.searx_engines': engines.join(',') }
          : { 'assistant.sandbox_url': url, 'assistant.sandbox_timeout': timeout || '30' }).then(onClose)}>保存</button>
      </div>
    </aside>
  );
}

/** 专利检索的密钥。读配置时密钥是打码的，所以这里留空表示「不改」。 */
function EpoPanel({ cfg, onClose, onSave }: { cfg: Record<string, string>; onClose: () => void; onSave: (v: Record<string, string>) => Promise<void> }) {
  const has = !!cfg['assistant.epo_api_key'];
  const [key, setKey] = useState('');
  const [secret, setSecret] = useState('');
  const [mailto, setMailto] = useState(cfg['assistant.openalex_mailto'] ?? '');
  const [test, setTest] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const values = () => {
    const v: Record<string, string> = { 'assistant.openalex_mailto': mailto.trim() };
    if (key.trim()) v['assistant.epo_api_key'] = key.trim();
    if (secret.trim()) v['assistant.epo_secret_api_key'] = secret.trim();
    return v;
  };
  const runTest = async () => {
    setBusy(true); setTest(null);
    try {
      await onSave(values());
      const r = await post<{ ok: boolean; error?: string }>('/api/ops/tools/test-epo');
      setTest(r.ok ? { ok: true, text: '密钥可用，专利检索已就绪' } : { ok: false, text: r.error ?? '密钥换不到令牌，请检查是否填反或复制多了空格' });
    } catch (err) { setTest({ ok: false, text: err instanceof ApiError ? err.message : '测试失败' }); }
    finally { setBusy(false); }
  };
  return (
    <aside className="acc-drawer" aria-label="专利论文检索设置">
      <div className="acc-drawer-hd"><b>专利 / 论文检索 · 设置</b><button type="button" className="refs-x" aria-label="关闭" onClick={onClose}><Icon name="x" size={14} style={{ display: 'block' }} /></button></div>
      <div className="acc-drawer-body sc">
        <p className="dash-muted" style={{ margin: 0, lineHeight: 1.7 }}>
          论文走 OpenAlex，免费、不用注册。专利走欧洲专利局 OPS（覆盖中国、美国、欧洲等专利），免费，但要先在 developers.epo.org 注册账号、建一个 App，拿到 Consumer Key 和 Consumer Secret 填在下面。
        </p>
        <label className="acc-field"><span>Consumer Key</span>
          <input className="inp m" style={{ height: 38 }} value={key} placeholder={has ? '已填（留空不改）' : '粘贴 Consumer Key'} onChange={(e) => setKey(e.target.value)} /></label>
        <label className="acc-field"><span>Consumer Secret</span>
          <input className="inp m" style={{ height: 38 }} type="password" value={secret} placeholder={has ? '已填（留空不改）' : '粘贴 Consumer Secret'} onChange={(e) => setSecret(e.target.value)} /></label>
        <label className="acc-field"><span>论文检索联系邮箱（可不填）</span>
          <input className="inp m" style={{ height: 38 }} value={mailto} placeholder="填了 OpenAlex 给的配额更宽" onChange={(e) => setMailto(e.target.value)} /></label>
        <div className="acc-preview"><b>发出去之前系统会拦掉：</b>客户名单里的名称 · 台账里的项目编号 · 金额与报价，和联网搜索一样。</div>
        <div className="acc-field">
          <button type="button" className="gbtn" style={{ alignSelf: 'flex-start' }} disabled={busy} onClick={() => void runTest()}>{busy ? '测试中…' : '保存并测试密钥'}</button>
          {test && <small style={{ color: test.ok ? '#1c6b45' : 'var(--brand-fg)' }}>{test.ok && <Icon name="check" size={13} style={{ verticalAlign: '-2px', marginRight: 4 }} />}{test.text}</small>}
        </div>
      </div>
      <div className="acc-drawer-f">
        <button type="button" className="gbtn" onClick={onClose}>取消</button>
        <button type="button" className="pbtn" onClick={() => void onSave(values()).then(onClose)}>保存</button>
      </div>
    </aside>
  );
}
