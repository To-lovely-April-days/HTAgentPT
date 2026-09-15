// D2 术语修正提交（FR-6.4）：补充至术语表，经管理员确认后从下一次翻译起生效——
// 本次译文不会自动改写，这一点必须在界面上写死，否则用户提交完会以为译文跟着变了。
import React, { useState } from 'react';
import { post, ApiError } from '../../lib/api';
import type { TermHit } from '../../lib/types';
import { ErrorBox } from '../../components/Common';

export default function TermSubmitModal({ hit, direction, domains, onClose }: {
  hit: TermHit;
  direction: 'zh2en' | 'en2zh';
  domains: string[];
  onClose: () => void;
}) {
  // 修正的是译入语一侧：中→英 改英文译法，英→中 改中文译法
  const fixEn = direction === 'zh2en';
  const [suggest, setSuggest] = useState('');
  const [domain, setDomain] = useState(hit.domain);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!suggest.trim()) return;
    setBusy(true);
    setError(null);
    try {
      await post('/api/terms/suggest', {
        domain: domain.trim() || hit.domain,
        zh: fixEn ? hit.zh : suggest.trim(),
        en: fixEn ? suggest.trim() : hit.en,
        note: reason.trim() || null,
      });
      setDone(true);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '提交失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  };

  const fl: React.CSSProperties = { fontSize: 11.5, color: 'var(--ink-3)', marginBottom: 5 };
  const fi: React.CSSProperties = { height: 28, width: '100%', padding: '0 9px', background: 'var(--panel)', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };

  return (
    <div style={{ position: 'fixed', inset: 0, background: 'rgba(28,37,48,.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 50 }}
      onClick={onClose}>
      <div style={{ width: 560, maxHeight: '86vh', display: 'flex', flexDirection: 'column', background: 'var(--panel)', borderRadius: 8, overflow: 'hidden', boxShadow: '0 18px 50px rgba(16,24,40,.22)' }}
        onClick={(e) => e.stopPropagation()}>
        <div style={{ flexShrink: 0, padding: '16px 20px 13px', borderBottom: '1px solid var(--line)' }}>
          <div style={{ fontSize: 15, fontWeight: 600, lineHeight: 1.4 }}>提交术语修正</div>
          <div className="hint" style={{ marginTop: 3 }}>补充至术语表，经管理员确认后生效</div>
        </div>

        {done ? (
          <div style={{ padding: '22px 20px' }}>
            <div style={{ fontSize: 13.5, fontWeight: 600, marginBottom: 8 }}>已提交，等待管理员确认</div>
            <div style={{ fontSize: 12.5, lineHeight: 1.8, color: 'var(--ink-2)', marginBottom: 16 }}>
              确认后术语进表，<strong style={{ fontWeight: 600 }}>从下一次翻译起生效</strong>。本次译文不会自动改写——若本次就要用新译法，请在译文里手工改。
            </div>
            <button className="pbtn" style={{ height: 30, padding: '0 16px' }} onClick={onClose}>知道了</button>
          </div>
        ) : (
          <form onSubmit={submit} className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '16px 20px 10px' }}>
            <div style={{ display: 'flex', gap: 12, marginBottom: 16 }}>
              <div style={{ flex: '1 1 0' }}>
                <div style={fl}>{fixEn ? '中文原词' : '英文原词'}</div>
                <div style={{ ...fi, display: 'flex', alignItems: 'center', background: 'var(--bg-soft)' }} className={fixEn ? undefined : 'm'}>
                  {fixEn ? hit.zh : hit.en}
                </div>
              </div>
              <div style={{ flex: '1 1 0' }}>
                <div style={fl}>当前译法</div>
                <div style={{ ...fi, display: 'flex', alignItems: 'center', color: 'var(--cls-int-fg)', background: 'var(--cls-int-bg)', borderColor: 'var(--cls-int-line)' }} className={fixEn ? 'm' : undefined}>
                  {fixEn ? hit.en : hit.zh}
                </div>
              </div>
              <div style={{ flex: '1 1 0' }}>
                <div style={fl}>建议译法 <span style={{ color: 'var(--cls-conf-fg)' }}>*</span></div>
                <input style={{ ...fi, fontWeight: 500 }} className={fixEn ? 'm' : undefined} value={suggest} autoFocus
                  onChange={(e) => setSuggest(e.target.value)} />
              </div>
            </div>

            <div style={{ display: 'flex', gap: 12, marginBottom: 16 }}>
              <div style={{ width: 200 }}>
                <div style={fl}>领域分类</div>
                <input style={fi} list="term-domains" value={domain} onChange={(e) => setDomain(e.target.value)} />
                <datalist id="term-domains">{domains.map((d) => <option key={d} value={d} />)}</datalist>
              </div>
              <div style={{ flexGrow: 1 }}>
                <div style={fl}>修正理由</div>
                <input style={fi} value={reason} placeholder="为什么当前译法不合适" onChange={(e) => setReason(e.target.value)} />
              </div>
            </div>

            {error && <div style={{ marginBottom: 12 }}><ErrorBox message={error} /></div>}

            <div style={{ display: 'flex', alignItems: 'flex-start', gap: 9, padding: '11px 13px', background: 'var(--bg-soft)', border: '1px solid var(--line-soft)', borderRadius: 5, marginBottom: 14 }}>
              <div style={{ fontSize: 11.5, lineHeight: 1.75, color: 'var(--ink-2)' }}>
                提交只是建议，<strong style={{ fontWeight: 600 }}>本次译文不会自动改写</strong>。管理员确认后术语进表，从下一次翻译起生效。若本次就要用新译法，请在译文里手工改。<br />
                术语表同时供问答检索改写使用——确认后，提问用新译法也能召回旧写法的资料。
              </div>
            </div>

            <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '3px 0 14px' }}>
              <span className="hint">将出现在管理后台待确认列表</span>
              <div style={{ flexGrow: 1 }} />
              <button type="button" className="gbtn" onClick={onClose}>取消</button>
              <button type="submit" className="pbtn" style={{ height: 28, padding: '0 13px' }} disabled={busy || !suggest.trim()}>
                {busy ? '提交中…' : '提交'}
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}
