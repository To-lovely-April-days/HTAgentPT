// D1 翻译工作台：文本翻译 + docx 整篇翻译回填。
// 三条最容易做丢的需求都在界面上自证：术语注入看得见（命中标记 + 全文 N 处译法一致，M6）、
// 无法回填的元素逐条列出（FR-6.3 末句）、合同类提示同时写入下载文件页眉（FR-6.6）。
import React, { useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get, post, upload, download, ApiError } from '../../lib/api';
import type { FileTranslationResult, TermHit, TermRow, TextTranslationResult } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';
import TermSubmitModal from './TermSubmitModal';

type Mode = 'text' | 'file';
type Direction = 'zh2en' | 'en2zh';

export default function TranslatePage() {
  const [mode, setMode] = useState<Mode>('text');
  const [direction, setDirection] = useState<Direction>('zh2en');
  const [domain, setDomain] = useState('');
  const [bilingual, setBilingual] = useState(true);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [textResult, setTextResult] = useState<TextTranslationResult | null>(null);
  const [fileResult, setFileResult] = useState<FileTranslationResult | null>(null);
  const [fileName, setFileName] = useState<string | null>(null);
  const [termFix, setTermFix] = useState<TermHit | null>(null); // D2 弹层
  const fileInput = useRef<HTMLInputElement>(null);

  // 术语域下拉：来自已审定术语的领域集合
  const terms = useQuery({ queryKey: ['terms'], queryFn: () => get<TermRow[]>('/api/terms?status=Approved'), staleTime: 60_000 });
  const domains = useMemo(() => [...new Set((terms.data ?? []).map((t) => t.domain))], [terms.data]);

  const runText = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!text.trim()) return;
    setBusy(true);
    setError(null);
    setFileResult(null);
    try {
      setTextResult(await post<TextTranslationResult>('/api/translate', {
        text, direction, termDomain: domain || null, bilingual,
      }));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '翻译失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  };

  const runFile = async (f: File) => {
    setBusy(true);
    setError(null);
    setTextResult(null);
    setFileName(f.name);
    try {
      const form = new FormData();
      form.append('file', f);
      form.append('direction', direction);
      if (domain) form.append('termDomain', domain);
      setFileResult(await upload<FileTranslationResult>('/api/translate/file', form));
    } catch (err) {
      setFileResult(null);
      setError(err instanceof ApiError ? err.message : '文档翻译失败，请稍后重试');
    } finally {
      setBusy(false);
    }
  };

  const result = textResult ?? fileResult;
  const applied = result?.termsApplied ?? [];
  const notice = result?.contractNotice ?? null;
  // 「全文 N 处，译法一致」：按译入语一侧在译文中计数（M6 的界面自证）
  const targetSide = direction === 'zh2en' ? 'en' : 'zh';
  const bodyText = textResult ? textResult.translation : '';
  const countOf = (t: TermHit) => {
    const needle = targetSide === 'en' ? t.en : t.zh;
    if (!needle || !bodyText) return null;
    const m = bodyText.toLowerCase().split(needle.toLowerCase()).length - 1;
    return m > 0 ? m : null;
  };

  const tabStyle = (on: boolean): React.CSSProperties => ({
    display: 'inline-flex', alignItems: 'center', height: 26, padding: '0 10px', borderRadius: 4, fontSize: 12, cursor: 'pointer',
    ...(on ? { fontWeight: 500, color: 'var(--accent)', background: 'var(--accent-bg)', border: '1px solid var(--accent-line)' }
          : { color: 'var(--ink-2)', background: 'var(--panel)', border: '1px solid #dde3ea' }),
  });
  const fi: React.CSSProperties = { height: 28, padding: '0 9px', background: 'var(--panel)', border: '1px solid var(--line-strong)', borderRadius: 4, fontSize: 12.5, fontFamily: 'var(--font)' };

  return (
    <div style={{ flexGrow: 1, display: 'flex', flexDirection: 'column', minHeight: 0, minWidth: 0 }}>
      {/* 任务条 */}
      <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 11, padding: '12px 18px', background: 'var(--panel)', borderBottom: '1px solid var(--line)', flexWrap: 'wrap' }}>
        <span style={tabStyle(mode === 'text')} onClick={() => setMode('text')}>文本翻译</span>
        <span style={tabStyle(mode === 'file')} onClick={() => setMode('file')}>文档翻译（docx）</span>
        <div style={{ width: 1, height: 20, background: 'var(--line)' }} />
        <select style={fi} value={direction} onChange={(e) => setDirection(e.target.value as Direction)}>
          <option value="zh2en">中 → 英</option>
          <option value="en2zh">英 → 中</option>
        </select>
        <select style={fi} value={domain} onChange={(e) => setDomain(e.target.value)} title="术语域">
          <option value="">术语域：全部已审定</option>
          {domains.map((d) => <option key={d} value={d}>{d}</option>)}
        </select>
        {mode === 'text' && (
          <label style={{ display: 'flex', alignItems: 'center', gap: 5, fontSize: 12.5, cursor: 'pointer' }}>
            <input type="checkbox" checked={bilingual} onChange={(e) => setBilingual(e.target.checked)} />
            双语对照
          </label>
        )}
        <div style={{ flexGrow: 1 }} />
        {fileResult && (
          <button className="gbtn" onClick={() => void download(`/api/translate/files/${fileResult.taskId}`, fileResult.outputFileName)
            .catch((err) => alert(err instanceof ApiError ? err.message : '下载失败'))}>
            下载译文 ↓
          </button>
        )}
      </div>

      {/* 合同类提示（FR-6.6） */}
      {notice && (
        <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 10, padding: '11px 18px', background: 'var(--cls-int-bg)', borderBottom: '1px solid var(--cls-int-line)', color: 'var(--cls-int-fg)' }}>
          <span style={{ fontSize: 12.5, fontWeight: 500 }}>识别为合同类文本</span>
          <span style={{ fontSize: 11.5 }}>{notice}{fileResult ? '　该提示已写入下载文件页眉。' : ''}</span>
        </div>
      )}

      <div style={{ flexGrow: 1, display: 'flex', minHeight: 0 }}>
        {/* 主区 */}
        <div className="sc" style={{ flexGrow: 1, minWidth: 0, background: 'var(--panel)', padding: '18px 22px 24px' }}>
          {mode === 'text' && (
            <form onSubmit={runText} style={{ maxWidth: 900 }}>
              <div style={{ fontSize: 11, color: 'var(--ink-3)', letterSpacing: '.04em', marginBottom: 8 }}>
                原文 · {direction === 'zh2en' ? '中文' : 'EN'}
              </div>
              <textarea
                value={text}
                onChange={(e) => setText(e.target.value)}
                placeholder="粘贴要翻译的文本。长文本按段落边界分批翻译，不在句中断开，长度不设硬上限。"
                style={{ width: '100%', minHeight: 130, padding: '10px 12px', border: '1px solid var(--line-strong)', borderRadius: 5, fontSize: 13, lineHeight: 1.8, fontFamily: 'var(--font)', resize: 'vertical' }}
              />
              <div style={{ display: 'flex', gap: 8, margin: '10px 0 18px' }}>
                <button type="submit" className="pbtn" style={{ height: 30, padding: '0 16px' }} disabled={busy || !text.trim()}>
                  {busy ? '翻译中…' : '翻译'}
                </button>
                {textResult && <button type="button" className="gbtn" style={{ height: 30 }} onClick={() => { setTextResult(null); setText(''); }}>清空</button>}
              </div>
              {error && <ErrorBox message={error} />}
              {busy && !textResult && <Spinner text="分批翻译中——按段落边界切分，不在句中断开…" />}

              {textResult && !bilingual && (
                <>
                  <div style={{ fontSize: 11, color: 'var(--ink-3)', letterSpacing: '.04em', marginBottom: 8 }}>译文 · {direction === 'zh2en' ? 'EN' : '中文'}</div>
                  <div style={{ fontSize: 13.5, lineHeight: 1.9, whiteSpace: 'pre-wrap' }}>{markTerms(textResult.translation, applied, targetSide)}</div>
                </>
              )}
              {textResult && bilingual && (
                <div style={{ display: 'flex', flexDirection: 'column', gap: 0 }}>
                  <div style={{ display: 'flex', gap: 22, marginBottom: 10 }}>
                    <div style={{ flex: '1 1 0', fontSize: 11, color: 'var(--ink-3)', letterSpacing: '.04em' }}>原文 · {direction === 'zh2en' ? '中文' : 'EN'}</div>
                    <div style={{ flex: '1 1 0', fontSize: 11, color: 'var(--ink-3)', letterSpacing: '.04em' }}>译文 · {direction === 'zh2en' ? 'EN' : '中文'}</div>
                  </div>
                  {textResult.pairs.map((p, i) => (
                    <div key={i} style={{ display: 'flex', gap: 22, padding: '9px 0', borderBottom: '1px solid var(--line-soft)' }}>
                      <div style={{ flex: '1 1 0', minWidth: 0, fontSize: 13, lineHeight: 1.8, whiteSpace: 'pre-wrap', color: 'var(--ink-2)' }}>
                        {markTerms(p.source, applied, targetSide === 'en' ? 'zh' : 'en')}
                      </div>
                      <div style={{ flex: '1 1 0', minWidth: 0, fontSize: 13, lineHeight: 1.8, whiteSpace: 'pre-wrap' }}>
                        {markTerms(p.target, applied, targetSide)}
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </form>
          )}

          {mode === 'file' && (
            <div style={{ maxWidth: 900 }}>
              {!fileResult && (
                <div
                  style={{ border: '1.5px dashed var(--line-strong)', borderRadius: 6, padding: '42px 20px', textAlign: 'center', cursor: 'pointer', background: 'var(--bg-soft)' }}
                  onClick={() => fileInput.current?.click()}
                >
                  <input ref={fileInput} type="file" accept=".docx" hidden
                    onChange={(e) => { const f = e.target.files?.[0]; if (f) void runFile(f); e.target.value = ''; }} />
                  <div style={{ fontSize: 14, fontWeight: 500, marginBottom: 6 }}>{busy ? `正在翻译 ${fileName ?? ''}…` : '点击选择 .docx 文档'}</div>
                  <div className="hint" style={{ maxWidth: 520, margin: '0 auto', lineHeight: 1.7 }}>
                    整篇翻译并保留原格式回填，覆盖正文、表格单元格、页眉页脚与文本框；无法回填的元素会逐条列出。长文档按段落边界分批处理，不设长度上限。
                  </div>
                  {busy && <div style={{ marginTop: 14 }}><Spinner text="分批翻译与回填中…" /></div>}
                </div>
              )}
              {error && <div style={{ marginTop: 14 }}><ErrorBox message={error} /></div>}
              {fileResult && (
                <>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 14 }}>
                    <div>
                      <div style={{ fontSize: 13.5, fontWeight: 600 }}>{fileName}</div>
                      <div className="hint" style={{ marginTop: 2 }}>输出文件：{fileResult.outputFileName}</div>
                    </div>
                    <div style={{ flexGrow: 1 }} />
                    <button className="gbtn" onClick={() => { setFileResult(null); setFileName(null); }}>翻译另一份</button>
                  </div>
                  <div className="card" style={{ padding: '14px 16px', marginBottom: 14 }}>
                    <div style={{ fontSize: 12.5, fontWeight: 600, marginBottom: 8 }}>回填报告</div>
                    <div style={{ display: 'flex', gap: 26 }}>
                      <Stat label="段落总数" value={fileResult.report.paragraphs} />
                      <Stat label="已译出" value={fileResult.report.translated} />
                      <Stat label="无法回填" value={fileResult.report.unfillable.length} warn={fileResult.report.unfillable.length > 0} />
                    </div>
                    {fileResult.report.unfillable.length === 0 && (
                      <div className="hint" style={{ marginTop: 8 }}>全部可回填元素均已替换为译文，原格式保留。</div>
                    )}
                  </div>
                  <div className="hint" style={{ lineHeight: 1.7 }}>
                    用右上角「下载译文」取回文件。{notice ? '合同类提示已写入文件页眉。' : ''}
                  </div>
                </>
              )}
            </div>
          )}
        </div>

        {/* 右栏：命中术语 + 无法回填（FR-6.1 / FR-6.3 末句） */}
        <div style={{ width: 380, flexShrink: 0, borderLeft: '1px solid var(--line)', background: 'var(--panel)', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <div className="sc" style={{ flexGrow: 1, minHeight: 0, padding: '16px 16px 18px' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 5 }}>
              <span style={{ fontSize: 13, fontWeight: 600 }}>命中术语</span>
              <span className="pill pill-neutral">{applied.length} 条</span>
            </div>
            <div style={{ fontSize: 11.5, color: 'var(--ink-2)', lineHeight: 1.7, marginBottom: 11 }}>
              以强制对照形式写入提示词，保证同一设备或工艺在全文译名一致。{textResult ? '正文中已标出。' : ''}
            </div>
            {applied.length === 0 && <div className="hint" style={{ marginBottom: 20 }}>本次翻译没有命中已审定术语。</div>}
            {applied.length > 0 && (
              <div style={{ border: '1px solid var(--line)', borderRadius: 5, overflow: 'hidden', marginBottom: 20 }}>
                {applied.map((t) => {
                  const n = countOf(t);
                  return (
                    <div key={`${t.zh}|${t.en}`} style={{ padding: '10px 12px', borderBottom: '1px solid var(--line-soft)' }}>
                      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}>
                        <span style={{ fontSize: 12.5, fontWeight: 500 }}>{t.zh}</span>
                        <span style={{ fontSize: 11.5, color: 'var(--ink-2)' }}>{t.en}</span>
                        <div style={{ flexGrow: 1 }} />
                        <span className="pill" style={{ background: 'var(--bg-soft)', color: 'var(--ink-3)', border: '1px solid var(--line-soft)' }}>{t.domain}</span>
                      </div>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 6 }}>
                        <span style={{ fontSize: 11, color: 'var(--ink-3)' }}>
                          {n != null ? `全文 ${n} 处，译法一致` : '已注入术语对照'}
                        </span>
                        <div style={{ flexGrow: 1 }} />
                        <button onClick={() => setTermFix(t)}
                          style={{ fontSize: 11, color: 'var(--accent)', background: 'none', border: 'none', cursor: 'pointer', fontFamily: 'var(--font)', padding: 0 }}>
                          译法不当
                        </button>
                      </div>
                    </div>
                  );
                })}
              </div>
            )}

            {fileResult && (
              <>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 5 }}>
                  <span style={{ fontSize: 13, fontWeight: 600 }}>无法回填的元素</span>
                  <span className="pill" style={fileResult.report.unfillable.length > 0
                    ? { background: 'var(--cls-int-bg)', color: 'var(--cls-int-fg)', border: '1px solid var(--cls-int-line)' }
                    : { background: '#e6f2ec', color: '#1c6b45', border: '1px solid #c2ded1' }}>
                    {fileResult.report.unfillable.length} 处
                  </span>
                </div>
                <div style={{ fontSize: 11.5, color: 'var(--ink-2)', lineHeight: 1.7, marginBottom: 11 }}>
                  这些位置的原文保持不变，需人工处理。回填范围覆盖正文、表格单元格、页眉页脚与文本框。
                </div>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 7 }}>
                  {fileResult.report.unfillable.map((u, i) => (
                    <div key={i} style={{ padding: '10px 12px', background: 'var(--cls-int-bg)', border: '1px solid var(--cls-int-line)', borderRadius: 5 }}>
                      <div style={{ fontSize: 11.5, lineHeight: 1.65, color: 'var(--cls-int-fg)' }}>{u}</div>
                    </div>
                  ))}
                </div>
              </>
            )}
          </div>
        </div>
      </div>

      {termFix && (
        <TermSubmitModal
          hit={termFix}
          direction={direction}
          domains={domains}
          onClose={() => setTermFix(null)}
        />
      )}
    </div>
  );
}

/** 命中术语在正文中的标记（FR-6.1 可见性）：浅底色标出对应语言一侧。 */
function markTerms(text: string, hits: TermHit[], side: 'zh' | 'en'): React.ReactNode {
  const targets = [...new Set(hits.map((h) => (side === 'zh' ? h.zh : h.en)))].filter(Boolean);
  if (targets.length === 0 || !text) return text;
  const re = new RegExp(targets.map((t) => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|'), 'gi');
  const out: React.ReactNode[] = [];
  let last = 0;
  for (const m of text.matchAll(re)) {
    if (m.index! > last) out.push(text.slice(last, m.index));
    out.push(<span key={m.index} style={{ background: 'var(--accent-bg)', borderBottom: '1.5px solid var(--accent-line)', padding: '0 1px' }}>{m[0]}</span>);
    last = m.index! + m[0].length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

function Stat({ label, value, warn }: { label: string; value: number; warn?: boolean }) {
  return (
    <div>
      <div className="m" style={{ fontSize: 20, fontWeight: 600, color: warn ? 'var(--cls-int-fg)' : 'var(--ink)' }}>{value}</div>
      <div style={{ fontSize: 11, color: 'var(--ink-3)', marginTop: 2 }}>{label}</div>
    </div>
  );
}
