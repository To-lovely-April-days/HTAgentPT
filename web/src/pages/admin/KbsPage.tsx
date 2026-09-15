// E5 知识库管理 + E6 共享库同步 + E7 公开库发布撤回。
// 三条界面必须自己扛的需求：库层级一经设定不可修改（新建面板红警示）、
// 同步包附带向量模型版本标识（一致可复用向量，省去重算——不显示复用结果管理员无从判断）、
// 发布是复制不是引用（之后改原文不会自动同步到副本；撤回只删副本，原库正本不受影响）。
import React, { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { get, post, ApiError } from '../../lib/api';
import { KB_TIER_LABEL, STRATEGY_LABEL } from '../../lib/types';
import type { DocRow, KbRow, PublishRow, SyncResult } from '../../lib/types';
import { ErrorBox, InfoBox, Spinner } from '../../components/Common';

export default function KbsPage() {
  const [tab, setTab] = useState<'kbs' | 'sync' | 'publish'>('kbs');
  const tabStyle = (on: boolean): React.CSSProperties => ({
    height: 40, padding: '0 13px', display: 'inline-flex', alignItems: 'center', fontSize: 12.5, cursor: 'pointer',
    borderBottom: on ? '2px solid var(--accent)' : '2px solid transparent',
    color: on ? 'var(--accent)' : 'var(--ink-2)', fontWeight: on ? 600 : 400,
  });
  return (
    <>
      <div style={{ flexShrink: 0, display: 'flex', gap: 2, padding: '0 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)' }}>
        <span style={tabStyle(tab === 'kbs')} onClick={() => setTab('kbs')}>知识库</span>
        <span style={tabStyle(tab === 'sync')} onClick={() => setTab('sync')}>共享库同步</span>
        <span style={tabStyle(tab === 'publish')} onClick={() => setTab('publish')}>公开库发布</span>
      </div>
      {tab === 'kbs' && <KbListTab />}
      {tab === 'sync' && <SyncTab />}
      {tab === 'publish' && <PublishTab />}
    </>
  );
}

// ─── E5 ────────────────────────────────────────────────
function KbListTab() {
  const qc = useQueryClient();
  const kbs = useQuery({ queryKey: ['kbs'], queryFn: () => get<KbRow[]>('/api/kbs') });
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState('');
  const [tier, setTier] = useState('Private');
  const [strategy, setStrategy] = useState('General');
  const [desc, setDesc] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const create = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await post('/api/kbs', { name: name.trim(), tier, defaultChunkStrategy: strategy, description: desc.trim() || null });
      setCreating(false);
      setName('');
      setDesc('');
      void qc.invalidateQueries({ queryKey: ['kbs'] });
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '创建失败');
    } finally {
      setBusy(false);
    }
  };

  const fi: React.CSSProperties = { height: 28, padding: '0 9px', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };
  const TIER_PILL: Record<string, React.CSSProperties> = {
    Shared: { background: 'var(--cls-pub-bg)', color: 'var(--cls-pub-fg)', border: '1px solid var(--cls-pub-line)' },
    Private: { background: 'var(--accent-bg)', color: 'var(--accent)', border: '1px solid var(--accent-line)' },
    Public: { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' },
  };

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
      <div style={{ maxWidth: 860 }}>
        {kbs.isLoading && <Spinner text="载入…" />}
        <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', marginBottom: 16 }}>
          {(kbs.data ?? []).map((k) => (
            <div key={k.id} className="card" style={{ width: 268, padding: '14px 16px' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 6 }}>
                <span style={{ fontSize: 13, fontWeight: 600 }}>{k.name}</span>
                <span className="pill" style={TIER_PILL[k.tier]}>{KB_TIER_LABEL[k.tier]}</span>
                {!k.isActive && <span className="pill pill-neutral">停用</span>}
              </div>
              <div className="hint" style={{ lineHeight: 1.7 }}>
                文档 <span className="m" style={{ color: 'var(--ink)' }}>{k.docCount}</span>
                　·　分块 <span className="m" style={{ color: 'var(--ink)' }}>{k.chunkCount}</span><br />
                默认切分：{STRATEGY_LABEL[k.defaultChunkStrategy]}（随时可改）<br />
                {k.tier === 'Shared' && '总部下发，本地只读——同步是唯一合法写入通道'}
                {k.tier === 'Public' && '仅承载已审定发布的副本'}
                {k.tier === 'Private' && (k.description || '本公司语料')}
              </div>
              {k.lastUpdatedAt && <div className="hint" style={{ marginTop: 6 }}>最近更新 {k.lastUpdatedAt.slice(0, 16).replace('T', ' ')}</div>}
            </div>
          ))}
        </div>

        {!creating && <button className="pbtn" style={{ height: 28, padding: '0 14px' }} onClick={() => setCreating(true)}>新建知识库</button>}
        {creating && (
          <form onSubmit={create} className="card" style={{ maxWidth: 560, padding: '14px 16px' }}>
            <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 10 }}>新建知识库</div>
            <div style={{ display: 'flex', gap: 10, marginBottom: 10 }}>
              <input style={{ ...fi, flexGrow: 1 }} placeholder="库名称" value={name} onChange={(e) => setName(e.target.value)} autoFocus />
              <select style={fi} value={tier} onChange={(e) => setTier(e.target.value)}>
                <option value="Private">本公司私有库</option>
                <option value="Public">对外公开库</option>
              </select>
              <select style={fi} value={strategy} onChange={(e) => setStrategy(e.target.value)}>
                {Object.entries(STRATEGY_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </select>
            </div>
            <input style={{ ...fi, width: '100%', marginBottom: 10 }} placeholder="用途说明（可选）" value={desc} onChange={(e) => setDesc(e.target.value)} />
            <div style={{ padding: '10px 12px', borderRadius: 5, background: 'var(--cls-conf-bg)', border: '1px solid var(--cls-conf-line)', marginBottom: 10 }}>
              <span style={{ fontSize: 11.5, lineHeight: 1.7, color: 'var(--cls-conf-fg)' }}>
                库的层级一经设定不可修改——库里每一份文档的可见范围都由它推导，改层级等于改动全部历史文档的权限边界。需变更时新建库并迁移。默认切分策略则随时可改。
              </span>
            </div>
            {error && <div style={{ marginBottom: 10 }}><ErrorBox message={error} /></div>}
            <div style={{ display: 'flex', gap: 8 }}>
              <button type="submit" className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={busy || !name.trim()}>创建</button>
              <button type="button" className="gbtn" onClick={() => setCreating(false)}>取消</button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}

// ─── E6 ────────────────────────────────────────────────
function SyncTab() {
  const qc = useQueryClient();
  const [busy, setBusy] = useState<'inc' | 'full' | null>(null);
  const [result, setResult] = useState<SyncResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  const run = async (full: boolean) => {
    setBusy(full ? 'full' : 'inc');
    setError(null);
    setResult(null);
    try {
      setResult(await post<SyncResult>(`/api/sync/shared?full=${full}`));
      void qc.invalidateQueries({ queryKey: ['kbs'] });
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '同步失败');
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
      <div style={{ maxWidth: 720 }}>
        <div className="hint" style={{ lineHeight: 1.75, marginBottom: 14 }}>
          从总部拉取集团共享库并合入本地。同步包附带总部所用向量化模型的版本标识：
          与本机一致时直接复用向量省去重算；不一致时新增与更新的文档要在本地重算向量，
          完成前不参与语义检索——耗时差数量级，请据此决定是否排在非工作时段。
        </div>
        <div style={{ display: 'flex', gap: 8, marginBottom: 16 }}>
          <button className="pbtn" style={{ height: 30, padding: '0 16px' }} disabled={busy !== null} onClick={() => void run(false)}>
            {busy === 'inc' ? '同步中…' : '增量同步'}
          </button>
          <button className="gbtn" style={{ height: 30 }} disabled={busy !== null} onClick={() => void run(true)}>
            {busy === 'full' ? '同步中…' : '全量同步（含撤回对齐）'}
          </button>
        </div>
        {error && <div style={{ marginBottom: 12 }}><ErrorBox message={error} /></div>}
        {result && (
          <div className="card" style={{ padding: '14px 16px', marginBottom: 14 }}>
            <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 10 }}>本次同步（批次 <span className="m">{result.batchNo}</span>）</div>
            <div style={{ display: 'flex', gap: 26, flexWrap: 'wrap' }}>
              {[
                ['合入文档', result.docsUpserted], ['写入分块', result.chunksWritten],
                ['向量直接复用', result.vectorsReused], ['需本地重算', result.docsQueuedForEmbedding],
                ['标记撤回', result.withdrawn],
              ].map(([label, v]) => (
                <div key={label as string}>
                  <div className="m" style={{ fontSize: 20, fontWeight: 600, color: label === '需本地重算' && (v as number) > 0 ? 'var(--cls-int-fg)' : 'var(--ink)' }}>{v}</div>
                  <div style={{ fontSize: 11, color: 'var(--ink-3)', marginTop: 2 }}>{label}</div>
                </div>
              ))}
            </div>
            {result.docsQueuedForEmbedding > 0 && (
              <div className="hint" style={{ marginTop: 10, color: 'var(--cls-int-fg)' }}>
                版本标识不一致：{result.docsQueuedForEmbedding} 篇文档已进重算队列，完成前这些内容不参与语义检索（进度见语料管理 › 解析队列）。
              </div>
            )}
            {result.warnings.map((w, i) => <div key={i} className="hint" style={{ marginTop: 6, color: 'var(--cls-conf-fg)' }}>{w}</div>)}
          </div>
        )}
        <InfoBox>
          总部已删除或撤回的文档，同步后在本地标记为已撤回并停止参与检索；原文件保留至下一次全量同步后清理——万一是误撤回还能恢复。
        </InfoBox>
      </div>
    </div>
  );
}

// ─── E7 ────────────────────────────────────────────────
function PublishTab() {
  const qc = useQueryClient();
  const records = useQuery({ queryKey: ['publish-records'], queryFn: () => get<PublishRow[]>('/api/publish') });
  const docs = useQuery({ queryKey: ['docs', '', 'Parsed', ''], queryFn: () => get<DocRow[]>('/api/documents?status=Parsed') });
  const [picking, setPicking] = useState(false);
  const [withdrawing, setWithdrawing] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = () => void qc.invalidateQueries({ queryKey: ['publish-records'] });
  const act = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await fn();
      refresh();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '操作失败');
    } finally {
      setBusy(false);
    }
  };

  // 只有私有库已解析文档可发布（共享库正本归总部，公开库本身是副本容器）
  const candidates = (docs.data ?? []).filter((d) => d.kbName.includes('私有'));
  // 服务端 Status 直接给用户话术：待确认 / 已发布 / 已撤回
  const STATUS_PILL: Record<string, React.CSSProperties> = {
    待确认: { background: 'var(--cls-int-bg)', color: 'var(--cls-int-fg)', border: '1px solid var(--cls-int-line)' },
    已发布: { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' },
    已撤回: { background: '#eef1f5', color: 'var(--ink-2)', border: '1px solid #dde3ea' },
  };

  return (
    <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '18px 22px' }}>
      <div style={{ maxWidth: 800 }}>
        <div className="hint" style={{ lineHeight: 1.75, marginBottom: 12 }}>
          发布是复制不是引用：在公开库创建独立副本并单独解析，原文档不变——<strong style={{ fontWeight: 600 }}>之后修改原文档不会自动同步到已发布的副本</strong>，需重新发布。
          副本确认前不对外可见，这一步是去掉内部分机、供应商联系人、流程号的地方。
        </div>
        {error && <div style={{ marginBottom: 12 }}><ErrorBox message={error} /></div>}

        {!picking && <button className="pbtn" style={{ height: 28, padding: '0 14px', marginBottom: 14 }} onClick={() => setPicking(true)}>发布文档到公开库</button>}
        {picking && (
          <div className="card" style={{ padding: '12px 14px', marginBottom: 14 }}>
            <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 8 }}>选择要发布的私有库文档（建副本草稿，确认前不对外可见）</div>
            {candidates.map((d) => (
              <div key={d.id} style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '7px 0', borderBottom: '1px solid var(--line-soft)' }}>
                <span style={{ fontSize: 12.5, flexGrow: 1 }}>{d.title}</span>
                <button className="gbtn" style={{ height: 24, fontSize: 11.5 }} disabled={busy}
                  onClick={() => { void act(() => post('/api/publish', { docId: d.id })); setPicking(false); }}>
                  建发布草稿
                </button>
              </div>
            ))}
            <button className="gbtn" style={{ height: 26, marginTop: 10 }} onClick={() => setPicking(false)}>取消</button>
          </div>
        )}

        {records.isLoading && <Spinner text="载入发布记录…" />}
        {(records.data ?? []).map((r) => (
          <div key={r.recordId} className="card" style={{ padding: '12px 14px', marginBottom: 8 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 9, marginBottom: 4 }}>
              <span style={{ fontSize: 12.5, fontWeight: 600 }}>{r.sourceTitle}</span>
              <span className="pill" style={STATUS_PILL[r.status] ?? STATUS_PILL['待确认']}>{r.status}</span>
              {r.status === '待确认' && <span className="hint">副本解析：{r.publicDocStatus === 'Parsed' ? '已完成，可确认' : '进行中…'}</span>}
              <div style={{ flexGrow: 1 }} />
              <span className="m hint">{r.createdAt.slice(0, 16).replace('T', ' ')} · {r.operatorName}</span>
            </div>
            {r.status === '已撤回' && (
              <div className="hint" style={{ marginBottom: 6 }}>
                撤回于 {r.withdrawnAt?.slice(0, 16).replace('T', ' ')}：{r.withdrawReason}——删的是公开库副本，原库正本不受影响；对客户一律表现为未找到。
              </div>
            )}
            <div style={{ display: 'flex', gap: 6 }}>
              {r.status === '待确认' && (
                <button className="pbtn" style={{ height: 26, fontSize: 12 }} disabled={busy || r.publicDocStatus !== 'Parsed'}
                  onClick={() => void act(() => post(`/api/publish/${r.recordId}/confirm`))}>
                  确认发布（即刻对外可见）
                </button>
              )}
              {r.status === '已发布' && withdrawing !== r.recordId && (
                <button className="gbtn" style={{ height: 26, fontSize: 12 }} onClick={() => { setWithdrawing(r.recordId); setReason(''); }}>撤回</button>
              )}
            </div>
            {withdrawing === r.recordId && (
              <div style={{ marginTop: 8, padding: '10px 12px', borderRadius: 5, background: 'var(--cls-conf-bg)', border: '1px solid var(--cls-conf-line)' }}>
                <input value={reason} onChange={(e) => setReason(e.target.value)} placeholder="撤回原因（必填，入记录）" autoFocus
                  style={{ width: '100%', height: 28, padding: '0 9px', border: '1px solid var(--cls-conf-line)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)', marginBottom: 8 }} />
                <div style={{ display: 'flex', gap: 6 }}>
                  <button disabled={busy || !reason.trim()}
                    onClick={() => { void act(() => post(`/api/publish/${r.recordId}/withdraw`, { reason: reason.trim() })); setWithdrawing(null); }}
                    style={{ height: 26, padding: '0 11px', borderRadius: 4, fontSize: 12, fontWeight: 500, color: '#fff', background: 'var(--cls-conf-fg)', border: 'none', cursor: 'pointer', fontFamily: 'var(--font)' }}>
                    确认撤回（立即停止对外可见）
                  </button>
                  <button className="gbtn" style={{ height: 26 }} onClick={() => setWithdrawing(null)}>取消</button>
                </div>
              </div>
            )}
          </div>
        ))}
        {records.data && records.data.length === 0 && <div className="hint">还没有发布记录。</div>}
      </div>
    </div>
  );
}
