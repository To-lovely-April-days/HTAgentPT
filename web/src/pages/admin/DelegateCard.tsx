// 系统设置 · 分头查：助手按任务复杂度自己决定要不要派子代理（只在标准、深度档；快速档不拆）。
// 默认值按显卡能力填：模型服务同时 4 路，子代理同时最多 3 个，留一路给别人。
import { useQuery } from '@tanstack/react-query';
import { get } from '../../lib/api';

interface Stats { delegate?: { runs: number; avgTasks: number; failedPct: number } }

const DEFAULTS: Record<string, string> = {
  'assistant.delegate': 'on', 'assistant.delegate.max_tasks': '4', 'assistant.delegate.parallel': '3', 'assistant.delegate.rounds': '5',
};

export function DelegateCard({ values, onChange }: { values: Record<string, string>; onChange: (key: string, value: string) => void }) {
  const stats = useQuery({ queryKey: ['tool-stats'], queryFn: () => get<Stats>('/api/ops/tools/stats') });
  const v = (k: string) => values[k] ?? DEFAULTS[k];
  const on = v('assistant.delegate') !== 'off';
  const num = (k: string, label: string) => (
    <input className="m lv-in" style={{ width: 56 }} inputMode="numeric" value={v(k)} aria-label={label} onChange={(e) => onChange(k, e.target.value.trim())} />
  );
  const d = stats.data?.delegate;
  return (
    <div className="card lv-card">
      <div className="lv-hd">
        <b>分头查</b>
        <span className="hint">复杂任务（几份文件逐份对照、几块各查各的）助手自己拆成几个子任务，派子代理同时去查，各自交回结论再汇总。只在标准、深度档拆，快速档不拆。</span>
      </div>
      <table className="lv-t">
        <tbody>
          <tr>
            <td style={{ width: 170 }}>分头查</td>
            <td style={{ width: 120 }}>
              <button type="button" role="switch" aria-checked={on} aria-label="分头查开关" className={'tl-sw' + (on ? ' on' : '')}
                onClick={() => onChange('assistant.delegate', on ? 'off' : 'on')}><i /></button>
            </td>
            <td className="hint">关掉就和以前一样，一个助手自己查</td>
          </tr>
          <tr><td>一次最多拆几个</td><td>{num('assistant.delegate.max_tasks', '一次最多拆几个')}</td><td className="hint">2–6，再多 27B 模型容易拆乱</td></tr>
          <tr><td>同时跑几个</td><td>{num('assistant.delegate.parallel', '同时跑几个')}</td><td className="hint">模型服务同时 4 路，留一路给别人；多的排队</td></tr>
          <tr><td>子代理最多查几轮</td><td>{num('assistant.delegate.rounds', '子代理最多查几轮')} 轮</td><td className="hint">超过就停，算没查完，主助手自己补查</td></tr>
        </tbody>
      </table>
      <div className="lv-row">
        <span>近 7 天拆了 <b className="m">{d?.runs ?? 0}</b> 次</span>
        <span>平均 <b className="m">{d?.avgTasks ?? 0}</b> 个子任务</span>
        <span>没查完 <b className="m">{d?.failedPct ?? 0}%</b></span>
        <span className="hint">改完点页底「保存」</span>
      </div>
    </div>
  );
}
