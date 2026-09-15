// E15 模型与运行参数 + E16 备份与远程接入。
// 三条界面必须自己扛的需求：换向量化模型必须连带全量重建（哨兵行常驻页底）、
// 阈值须以真实问题集标定（不是能照抄的数）、把「没备份什么」写在界面上（灰卡）。
import React, { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, put, ApiError } from '../../lib/api';
import { ErrorBox, InfoBox, Spinner } from '../../components/Common';

interface BackupStatus {
  rows: { kind: string; configured: boolean; last: null | {
    startedAt: string; finishedAt: string | null; ok: boolean | null; sizeBytes: number | null;
    target: string | null; error: string | null; acknowledgedBy: string | null; acknowledgedAt: string | null;
  } }[];
  note: string;
}
interface EmbedConsistency { currentModel: string; totalChunks: number; inconsistent: number; }

const KIND_LABEL: Record<string, string> = {
  LocalIncremental: '本地增量', LocalFull: '本地全量', RemoteFull: '异地全量',
};

/** 可编辑的运行参数分组（键名即 sys_config 键，改后即时生效——10.4 无硬编码）。 */
const GROUPS: { title: string; keys: [string, string][] }[] = [
  {
    title: '检索与问答',
    keys: [
      ['retrieval.recall_top_k', '召回条数'], ['retrieval.rerank_top_n', '重排取前 N'],
      ['retrieval.score_threshold', '重排分数阈值'], ['retrieval.hybrid_alpha', '混合权重 α'],
      ['retrieval.history_turns', '多轮改写回看轮数'], ['retrieval.context_tokens', '上下文长度上限'],
    ],
  },
  {
    title: '切分与解析',
    keys: [
      ['chunking.target_length', '分块目标长度'], ['chunking.overlap', '分块重叠'],
      ['parser.url', '解析服务地址'], ['parser.backend', '解析后端'],
      ['parser.timeout_seconds', '解析超时（秒）'],
      ['parser.concurrency', '解析并发数'], ['parser.max_retries', '解析重试次数'],
    ],
  },
  {
    title: '上传与会话',
    keys: [
      ['upload.max_file_mb', '单文件上限 MB'], ['auth.jwt_lifetime_minutes', '登录有效期（分钟）'],
      ['translate.batch_chars', '翻译单批字数'],
    ],
  },
  {
    // 嵌入/重排走 OpenAI 兼容 /v1/embeddings 与 /rerank 形态（本地推理服务）。
    // 改嵌入模型名后页底哨兵会亮出不一致分块数——用旁边的重建按钮全量补齐。
    // 维度不在此列：结构性参数，随部署定，改它意味着重建整个向量列。
    title: '模型服务（向量化与重排）',
    keys: [
      ['model.embedding.url', '向量化服务地址'], ['model.embedding.name', '向量化模型名'],
      ['model.rerank.url', '重排服务地址'], ['model.rerank.name', '重排模型名'],
    ],
  },
];

export default function SettingsPage() {
  const [tab, setTab] = useState<'params' | 'backup'>('params');
  const tabStyle = (on: boolean): React.CSSProperties => ({
    height: 40, padding: '0 13px', display: 'inline-flex', alignItems: 'center', fontSize: 12.5, cursor: 'pointer',
    borderBottom: on ? '2px solid var(--accent)' : '2px solid transparent',
    color: on ? 'var(--accent)' : 'var(--ink-2)', fontWeight: on ? 600 : 400,
  });
  return (
    <>
      <div style={{ flexShrink: 0, display: 'flex', gap: 2, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <span style={tabStyle(tab === 'params')} onClick={() => setTab('params')}>模型与运行参数</span>
        <span style={tabStyle(tab === 'backup')} onClick={() => setTab('backup')}>备份与远程接入</span>
      </div>
      {tab === 'params' ? <ParamsTab /> : <BackupTab />}
    </>
  );
}

// ─── E15 · 对话模型选择 ─────────────────────────────────
// 三个提供方：内置演示（不调外部模型）/ DeepSeek 在线（OpenAI 兼容 + 密钥）/
// 本地 Qwen（OpenAI 兼容端点）。名称与地址都是配置值（界面文案约束的例外面）；
// 图标走外链，加载失败降级为字母块。只切对话模型——嵌入/重排不随之切换。
interface ModelOption {
  id: string; title: string; badge: string; iconUrl: string | null;
  iconFallback: string; fallbackBg: string; fallbackFg: string;
  desc: string; url: string | null; modelName: string | null; needsKey: boolean; urlEditable: boolean;
}
const MODEL_OPTIONS: ModelOption[] = [
  {
    id: 'stub', title: '内置演示应答', badge: '无外部依赖',
    iconUrl: null, iconFallback: '演', fallbackBg: '#eef1f5', fallbackFg: '#5a6673',
    desc: '不调用任何外部模型，返回带来源标注的演示应答——用于没有模型服务的环境验证整条链路。',
    url: null, modelName: null, needsKey: false, urlEditable: false,
  },
  {
    id: 'deepseek', title: 'DeepSeek', badge: '在线服务',
    // 官方站点图标外链；离线或内网白名单环境加载不到时降级为品牌色块
    iconUrl: 'https://www.deepseek.com/favicon.ico', iconFallback: 'D', fallbackBg: '#4D6BFE', fallbackFg: '#ffffff',
    desc: '官方在线接口，需要接口密钥。对话数据会发往外部服务——机密内容是否允许出网，请先按公司规定确认。',
    url: 'https://api.deepseek.com/v1', modelName: 'deepseek-chat', needsKey: true, urlEditable: false,
  },
  {
    id: 'qwen-local', title: 'Qwen/Qwen3.8-27B', badge: '本地部署',
    iconUrl: 'https://qwenlm.github.io/favicon.ico', iconFallback: 'Q', fallbackBg: '#6A48F6', fallbackFg: '#ffffff',
    desc: '本机或内网推理服务上的本地模型，数据不出内网。服务地址可按实际部署修改。',
    url: 'http://127.0.0.1:8000/v1', modelName: 'Qwen/Qwen3.8-27B', needsKey: false, urlEditable: true,
  },
];

function ModelIcon({ opt }: { opt: ModelOption }) {
  const [failed, setFailed] = useState(false);
  if (!opt.iconUrl || failed) {
    return (
      <div style={{ width: 30, height: 30, borderRadius: 7, background: opt.fallbackBg, color: opt.fallbackFg, display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: 14, fontWeight: 700, flexShrink: 0 }}>
        {opt.iconFallback}
      </div>
    );
  }
  return <img src={opt.iconUrl} alt="" width={30} height={30} style={{ borderRadius: 7, flexShrink: 0 }} onError={() => setFailed(true)} />;
}

function ChatModelPicker({ cfg }: { cfg: Record<string, string> }) {
  const qc = useQueryClient();
  const current = cfg['model.chat.provider'] ?? 'stub';
  const [selected, setSelected] = useState(current);
  const [apiKey, setApiKey] = useState('');
  const [localUrl, setLocalUrl] = useState(cfg['model.chat.url'] && current === 'qwen-local' ? cfg['model.chat.url'] : 'http://127.0.0.1:8000/v1');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [applied, setApplied] = useState(false);

  const opt = MODEL_OPTIONS.find((o) => o.id === selected)!;
  const keyMasked = cfg['model.chat.api_key'] ?? '';
  const keyReady = !opt.needsKey || apiKey.trim().length > 0 || keyMasked.length > 0;
  const dirty = selected !== current || apiKey.trim().length > 0 || (opt.urlEditable && localUrl.trim() !== (cfg['model.chat.url'] ?? ''));

  const apply = async () => {
    setBusy(true);
    setError(null);
    setApplied(false);
    try {
      const values: Record<string, string> = { 'model.chat.provider': selected };
      if (opt.url) values['model.chat.url'] = opt.urlEditable ? localUrl.trim() : opt.url;
      if (opt.modelName) values['model.chat.name'] = opt.modelName;
      if (opt.needsKey && apiKey.trim()) values['model.chat.api_key'] = apiKey.trim();
      await put('/api/config', { values });
      setApiKey('');
      setApplied(true);
      void qc.invalidateQueries({ queryKey: ['config'] });
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '保存失败');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card" style={{ padding: '14px 16px', marginBottom: 16 }}>
      <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 4 }}>对话模型</div>
      <div className="hint" style={{ marginBottom: 12, lineHeight: 1.65 }}>
        只切换问答、翻译与生成所用的对话模型，改后即时生效、不用重启。嵌入与重排模型不随此切换——换向量化模型须全量重建（见下方警示）。
      </div>
      <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', marginBottom: 12 }}>
        {MODEL_OPTIONS.map((o) => {
          const on = selected === o.id;
          const isCurrent = current === o.id;
          return (
            <div key={o.id} onClick={() => setSelected(o.id)}
              style={{
                width: 226, padding: '12px 13px', borderRadius: 7, cursor: 'pointer',
                border: `1.5px solid ${on ? 'var(--accent)' : 'var(--line)'}`,
                background: on ? 'var(--accent-bg)' : 'var(--panel)',
              }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 9, marginBottom: 7 }}>
                <ModelIcon opt={o} />
                <div style={{ minWidth: 0 }}>
                  <div style={{ fontSize: 12.5, fontWeight: 600, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{o.title}</div>
                  <div style={{ display: 'flex', gap: 5, marginTop: 2 }}>
                    <span className="pill pill-neutral">{o.badge}</span>
                    {isCurrent && <span className="pill" style={{ background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' }}>当前生效</span>}
                  </div>
                </div>
              </div>
              <div style={{ fontSize: 11, lineHeight: 1.6, color: 'var(--ink-2)' }}>{o.desc}</div>
            </div>
          );
        })}
      </div>

      {opt.needsKey && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
          <span style={{ fontSize: 11.5, color: 'var(--ink-2)', width: 64 }}>接口密钥</span>
          <input className="m" type="password" value={apiKey} onChange={(e) => setApiKey(e.target.value)}
            placeholder={keyMasked ? `已配置（${keyMasked}），要更换才输入` : '必填，形如 sk-…'}
            style={{ height: 28, width: 300, padding: '0 9px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5 }} />
          <span className="hint">读取端只回显掩码，明文不回传浏览器</span>
        </div>
      )}
      {opt.urlEditable && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
          <span style={{ fontSize: 11.5, color: 'var(--ink-2)', width: 64 }}>服务地址</span>
          <input className="m" value={localUrl} onChange={(e) => setLocalUrl(e.target.value)}
            style={{ height: 28, width: 300, padding: '0 9px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5 }} />
          <span className="hint">本地推理服务的兼容端点</span>
        </div>
      )}
      {opt.needsKey && !keyReady && <div className="hint" style={{ color: 'var(--cls-int-fg)', marginBottom: 10 }}>未配置接口密钥前无法应用该选项。</div>}

      {error && <div style={{ marginBottom: 10 }}><ErrorBox message={error} /></div>}
      {applied && <div style={{ marginBottom: 10 }}><InfoBox>已应用，约 5 秒内对新请求生效。模型异常时问答会如实报错，不会退回旧应答。</InfoBox></div>}
      <button className="pbtn" style={{ height: 28, padding: '0 14px' }} disabled={busy || !dirty || !keyReady} onClick={() => void apply()}>
        应用模型选择
      </button>
    </div>
  );
}

// ─── E15 ────────────────────────────────────────────────
function ParamsTab() {
  const qc = useQueryClient();
  const cfg = useQuery({ queryKey: ['config'], queryFn: () => get<Record<string, string>>('/api/config') });
  const sentinel = useQuery({ queryKey: ['embed-consistency'], queryFn: () => get<EmbedConsistency>('/api/ops/embedding-consistency') });
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [rebuilding, setRebuilding] = useState(false);
  const [rebuildMsg, setRebuildMsg] = useState<string | null>(null);

  const rebuild = async () => {
    setRebuilding(true);
    setRebuildMsg(null);
    try {
      const r = await post<{ documents: number; chunks: number }>('/api/ops/embedding-rebuild', {});
      setRebuildMsg(`已清空 ${r.chunks} 个分块的旧向量并入队 ${r.documents} 篇文档重算。重算由解析队列逐篇执行，期间这些内容按关键词检索；本行数字会随进度回落。`);
      void qc.invalidateQueries({ queryKey: ['embed-consistency'] });
    } catch (err) {
      setRebuildMsg(err instanceof ApiError ? err.message : '重建入队失败，请稍后重试');
    } finally {
      setRebuilding(false);
    }
  };

  const values = { ...(cfg.data ?? {}), ...draft };
  const dirty = Object.keys(draft).filter((k) => draft[k] !== (cfg.data ?? {})[k]);
  const thresholdEdited = dirty.includes('retrieval.score_threshold');

  const save = async () => {
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      const changes = Object.fromEntries(dirty.map((k) => [k, draft[k]]));
      await put('/api/config', { values: changes });
      setDraft({});
      setSaved(true);
      void qc.invalidateQueries({ queryKey: ['config'] });
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '保存失败');
    } finally {
      setBusy(false);
    }
  };

  if (cfg.isLoading) return <div style={{ padding: 30 }}><Spinner text="载入配置…" /></div>;
  const fi: React.CSSProperties = { height: 28, width: 130, padding: '0 9px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
      <div style={{ maxWidth: 860, margin: '0 auto' }}>
        <ChatModelPicker cfg={cfg.data ?? {}} />

        {/* 换向量化模型的琥珀警示（整份 PRD 里最容易被漏掉的一条） */}
        <div style={{ padding: '13px 15px', borderRadius: 6, background: 'var(--cls-int-bg)', border: '1px solid var(--cls-int-line)', marginBottom: 16 }}>
          <div style={{ fontSize: 12.5, fontWeight: 600, color: 'var(--cls-int-fg)', marginBottom: 4 }}>
            向量化模型：{sentinel.data?.currentModel || '（未配置，当前使用打桩客户端）'}
          </div>
          <div style={{ fontSize: 12, lineHeight: 1.7, color: 'var(--cls-int-fg)' }}>
            更换向量化模型必须连带全量重建向量——新旧向量不在同一空间，混用时检索会持续返回错的结果且不报错。
            模型地址与名称在下方「模型服务」组维护；改名保存后，用页底的「重建不一致向量」逐篇补齐，
            重建期间这些内容的检索按关键词降级。
          </div>
        </div>

        {GROUPS.map((g) => (
          <div key={g.title} className="card" style={{ padding: '14px 16px', marginBottom: 12 }}>
            <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 10 }}>{g.title}</div>
            {g.keys.map(([key, label]) => (
              <div key={key} style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '6px 0', borderBottom: '1px solid var(--line-soft)' }}>
                <span style={{ fontSize: 12.5, width: 170 }}>{label}</span>
                <span className="m hint" style={{ width: 220 }}>{key}</span>
                <input className="m" style={{ ...fi, width: key.endsWith('url') ? 280 : 130 }} value={values[key] ?? ''}
                  onChange={(e) => setDraft((d) => ({ ...d, [key]: e.target.value }))} />
              </div>
            ))}
          </div>
        ))}

        {/* 阈值标定说明（4.3：非标定值，不能照抄） */}
        <div style={{ padding: '12px 14px', borderRadius: 6, background: 'var(--bg-soft)', border: '1px solid var(--line-soft)', marginBottom: 12 }}>
          <div style={{ fontSize: 12, lineHeight: 1.7, color: 'var(--ink-2)' }}>
            重排分数阈值不是能照抄的数——须在试运行期间以真实问题集标定（问题集、命中率、误拦率一并记录）。
            当前尚无标定记录；正式运行前请完成一轮标定，改阈值后须重标。
          </div>
          {thresholdEdited && (
            <div style={{ fontSize: 12, lineHeight: 1.7, color: 'var(--cls-int-fg)', marginTop: 6 }}>
              你正在修改阈值：没有标定记录支撑的调整，代价是未知的多拦或放行——保存前请确认这是标定结论而不是估的。
            </div>
          )}
        </div>

        {error && <div style={{ marginBottom: 10 }}><ErrorBox message={error} /></div>}
        {saved && <div style={{ marginBottom: 10 }}><InfoBox>已保存，改后即时生效（约 5 秒内）。变更已入操作留痕。</InfoBox></div>}
        <button className="pbtn" style={{ height: 30, padding: '0 16px' }} disabled={busy || dirty.length === 0} onClick={() => void save()}>
          保存 {dirty.length > 0 ? `${dirty.length} 项变更` : ''}
        </button>

        {/* 哨兵行：常驻页底，不能只写日志；重建动作就近放在数字旁边 */}
        <div className="m" style={{ marginTop: 20, paddingTop: 12, borderTop: '1px solid var(--line)', display: 'flex', alignItems: 'center', gap: 12, fontSize: 12, color: (sentinel.data?.inconsistent ?? 0) > 0 ? 'var(--cls-conf-fg)' : 'var(--ink-3)' }}>
          <span>
            与当前向量化模型（{sentinel.data?.currentModel || '…'}）不一致的分块 = {sentinel.data ? `${sentinel.data.inconsistent} / ${sentinel.data.totalChunks}` : '…'}
            {(sentinel.data?.inconsistent ?? 0) > 0 && '　——存在不一致分块时，这些内容的语义检索结果不可信'}
          </span>
          {(sentinel.data?.inconsistent ?? 0) > 0 && (
            <button className="pbtn" style={{ height: 26, padding: '0 12px', flexShrink: 0 }} disabled={rebuilding}
              onClick={() => void rebuild()}>
              {rebuilding ? '入队中…' : '重建不一致向量'}
            </button>
          )}
        </div>
        {rebuildMsg && <div className="hint" style={{ marginTop: 6 }}>{rebuildMsg}</div>}
      </div>
    </div>
  );
}

// ─── E16 ────────────────────────────────────────────────
function BackupTab() {
  const qc = useQueryClient();
  const status = useQuery({ queryKey: ['backup-status'], queryFn: () => get<BackupStatus>('/api/ops/backup/status') });
  const [running, setRunning] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const run = async (kind: string) => {
    setRunning(kind);
    setError(null);
    try {
      const r = await post<{ ok: boolean | null; error: string | null }>('/api/ops/backup/run', { kind });
      if (r.ok === false) setError(`${KIND_LABEL[kind]}执行失败：${r.error ?? '未记录原因'}`);
      void qc.invalidateQueries({ queryKey: ['backup-status'] });
      void qc.invalidateQueries({ queryKey: ['ops-alerts'] });
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '执行失败');
    } finally {
      setRunning(null);
    }
  };

  const fmt = (n: number | null) => n == null ? '—' : n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`;

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
      <div style={{ maxWidth: 980, margin: '0 auto' }}>
        {error && <div style={{ marginBottom: 12 }}><ErrorBox message={error} /></div>}
        {status.isLoading && <Spinner text="载入备份状态…" />}
        {status.data && (
          <>
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 8 }}>备份任务</div>
            {status.data.rows.map((r) => (
              <div key={r.kind} className="card" style={{ display: 'flex', alignItems: 'center', gap: 14, padding: '12px 15px', marginBottom: 8 }}>
                <div style={{ width: 90, fontSize: 12.5, fontWeight: 600 }}>{KIND_LABEL[r.kind] ?? r.kind}</div>
                <div style={{ flexGrow: 1, minWidth: 0 }}>
                  {!r.configured && <span className="pill" style={{ background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' }}>未配置——异地容灾这条线现在是断的</span>}
                  {r.configured && !r.last && <span className="hint">尚未执行过</span>}
                  {r.last && (
                    <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
                      <span className="pill" style={r.last.ok
                        ? { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' }
                        : { background: 'var(--cls-conf-bg)', color: 'var(--cls-conf-fg)', border: '1px solid var(--cls-conf-line)' }}>
                        {r.last.ok ? '成功' : '失败'}
                      </span>
                      <span className="m hint">{r.last.startedAt.slice(0, 16).replace('T', ' ')}</span>
                      <span className="m hint">{fmt(r.last.sizeBytes)}</span>
                      {r.last.error && <span style={{ fontSize: 11.5, color: 'var(--cls-conf-fg)' }}>{r.last.error}</span>}
                    </div>
                  )}
                </div>
                <button className="gbtn" style={{ flexShrink: 0 }} disabled={running !== null} onClick={() => void run(r.kind)}>
                  {running === r.kind ? '执行中…' : '立即执行'}
                </button>
              </div>
            ))}

            <div style={{ fontSize: 13, fontWeight: 600, margin: '18px 0 8px' }}>覆盖范围——恢复当天不该有意外</div>
            <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', marginBottom: 8 }}>
              {[
                ['原始文件', '完整备份', true], ['业务数据库', '完整备份', true], ['配置项', '完整备份', true],
              ].map(([name, note]) => (
                <div key={name as string} style={{ width: 176, padding: '12px 14px', borderRadius: 6, background: '#e6f2ec', border: '1px solid #c2ded1' }}>
                  <div style={{ fontSize: 12.5, fontWeight: 600, color: '#1c6b45' }}>{name}</div>
                  <div style={{ fontSize: 11.5, color: '#1c6b45', marginTop: 2 }}>{note}</div>
                </div>
              ))}
              <div style={{ width: 176, padding: '12px 14px', borderRadius: 6, background: '#eef1f5', border: '1px solid #dde3ea' }}>
                <div style={{ fontSize: 12.5, fontWeight: 600, color: 'var(--ink-2)' }}>向量索引</div>
                <div style={{ fontSize: 11.5, color: 'var(--ink-2)', marginTop: 2 }}>不备份——恢复后由原始文件重建</div>
              </div>
            </div>
            <div className="hint" style={{ lineHeight: 1.7, marginBottom: 20 }}>{status.data.note}</div>

            <RemoteAccessView />
          </>
        )}
      </div>
    </div>
  );
}

function RemoteAccessView() {
  const q = useQuery({
    queryKey: ['remote-access'],
    queryFn: () => get<{ bindings: { terminalId: string; name: string | null; isActive: boolean; lastSeenAt: string | null; lastIp: string | null; username?: string; displayName?: string }[] }>('/api/ops/remote-access'),
  });
  const bindings = (q.data as { bindings?: { terminalId: string; name: string | null; isActive: boolean; lastSeenAt: string | null; lastIp: string | null; username?: string; displayName?: string }[] } | undefined)?.bindings ?? [];
  return (
    <>
      <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 8 }}>远程接入（终端绑定）</div>
      {bindings.length === 0 && <div className="hint">没有绑定过终端的账号——绑定过的员工账号只允许从绑定终端接入（FR-9.7）。</div>}
      {bindings.map((b) => (
        <div key={b.terminalId} style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '8px 0', borderBottom: '1px solid var(--line-soft)', fontSize: 12.5 }}>
          <span className="m" style={{ width: 160 }}>{b.terminalId}</span>
          <span style={{ width: 130 }}>{b.displayName ?? b.username ?? '—'}</span>
          <span className="hint">{b.name ?? '—'}</span>
          <div style={{ flexGrow: 1 }} />
          <span className="m hint">{b.lastSeenAt ? b.lastSeenAt.slice(0, 16).replace('T', ' ') : '未接入过'}</span>
          <span className="m hint">{b.lastIp ?? ''}</span>
        </div>
      ))}
    </>
  );
}
