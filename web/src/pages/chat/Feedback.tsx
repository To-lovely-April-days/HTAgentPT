// 每条回答下面的「有帮助 / 没帮上」。没帮上的选一下哪里不对、可补一句，进后台「回答质量」等管理员处理；
// 勾了「处理完告诉我」的，处理好以后首页会提示。点「有帮助」只记一下，不弹东西。
import { useState } from 'react';
import type { ReactNode } from 'react';
import { post, ApiError } from '../../lib/api';
import { Icon } from '../../components/Icons';
import { FEEDBACK_REASONS } from './feedbackReasons';

export function FeedbackBar({ messageId, helpful: initial, reason: initialReason, extra }: {
  messageId: string; helpful?: boolean | null; reason?: string | null;
  /** 这一排右边多放的按钮（比如「深度再答」） */
  extra?: ReactNode;
}) {
  const [helpful, setHelpful] = useState<boolean | null>(initial ?? null);
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState<string>(initialReason ?? 'no_docs');
  const [comment, setComment] = useState('');
  const [notify, setNotify] = useState(true);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const send = async (h: boolean) => {
    setBusy(true); setErr(null);
    try {
      await post(`/api/qa-sessions/messages/${messageId}/feedback`,
        h ? { helpful: true } : { helpful: false, reason, comment: comment.trim() || null, notify });
      setHelpful(h); setOpen(false);
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : '没记上，稍后再试');
    } finally { setBusy(false); }
  };

  return (
    <div className="fb">
      <div className="fb-row">
        <button type="button" className={'fb-btn' + (helpful === true ? ' on' : '')} disabled={busy}
          aria-pressed={helpful === true} onClick={() => void send(true)}>
          <Icon name="thumbUp" size={14} />有帮助
        </button>
        <button type="button" className={'fb-btn' + (helpful === false || open ? ' on' : '')} disabled={busy}
          aria-pressed={helpful === false} aria-expanded={open} onClick={() => setOpen(!open)}>
          <Icon name="thumbDown" size={14} />没帮上
        </button>
        {helpful === false && !open && <span className="fb-note">已记下，管理员会看</span>}
        {helpful === true && <span className="fb-note">谢谢</span>}
        {extra && <span style={{ marginLeft: 'auto' }}>{extra}</span>}
      </div>
      {open && (
        <div className="fb-panel" role="group" aria-label="哪里不对">
          <b>哪里不对？</b>
          <div className="fb-chips">
            {FEEDBACK_REASONS.map((r) => (
              <button key={r.code} type="button" className={'fb-chip' + (reason === r.code ? ' on' : '')}
                aria-pressed={reason === r.code} onClick={() => setReason(r.code)}>{r.label}</button>
            ))}
          </div>
          <textarea value={comment} onChange={(e) => setComment(e.target.value)} maxLength={500} rows={2}
            placeholder="正确答案是什么、应该在哪份资料里（可不填）" />
          <div className="fb-foot">
            <label className="fb-check">
              <input type="checkbox" checked={notify} onChange={(e) => setNotify(e.target.checked)} />
              <Icon name={notify ? 'boxOn' : 'boxOff'} size={15} />处理完告诉我
            </label>
            {err && <span className="fb-err">{err}</span>}
            <button type="button" className="fb-cancel" onClick={() => setOpen(false)}>取消</button>
            <button type="button" className="fb-send" disabled={busy} onClick={() => void send(false)}>{busy ? '正在发…' : '发送'}</button>
          </div>
        </div>
      )}
    </div>
  );
}
