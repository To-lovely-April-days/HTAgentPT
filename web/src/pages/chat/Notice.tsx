// 回答下面的提示条：中断了、计划没做完、写到上限停了……同一套样式，按轻重分三种（warn 琥珀、info 蓝、默认灰）。
import { Icon } from '../../components/Icons';

export function Notice({ tone, icon, title, detail, action, actionIcon, primary, disabled, onAction }: {
  tone?: 'warn' | 'info'; icon: string; title: string; detail?: string | null;
  action?: string | null; actionIcon?: string; primary?: boolean; disabled?: boolean; onAction?: () => void;
}) {
  return (
    <div className={'notice' + (tone ? ' ' + tone : '')} role="status">
      <span className="notice-ic" aria-hidden="true"><Icon name={icon} size={12} stroke={2.6} /></span>
      <div>
        <b>{title}</b>
        {detail && <span className="d">{detail}</span>}
      </div>
      {action && onAction && (
        <button type="button" className={primary ? 'primary' : undefined} disabled={disabled} onClick={onAction}>
          {actionIcon && <Icon name={actionIcon} size={12} />}{action}
        </button>
      )}
    </div>
  );
}
