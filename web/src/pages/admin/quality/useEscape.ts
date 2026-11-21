import { useEffect } from 'react';

/** 按 Esc 关掉对话框。 */
export function useEscape(onClose: () => void) {
  useEffect(() => {
    const f = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', f);
    return () => window.removeEventListener('keydown', f);
  }, [onClose]);
}
