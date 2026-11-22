// 系统设置 · 工具按需加载：查资料、查项目、计算这些常用工具每次都带；流程模拟、做 PPT、浏览器这类专用工具
// 平时只在提示词里列一行，用得上才把完整说明交给模型（系统按问题先挑，模型也能自己找）。工具少了，模型挑得准、占的上下文也少。
import { useQuery } from '@tanstack/react-query';
import { get } from '../../lib/api';
import '../../styles/quality.css';   // q-chip

interface Stats { tools?: { tool: string; calls: number }[]; catalog?: { code: string; label: string; tools: string[]; summary: string }[] }

const DEFAULTS: Record<string, string> = { 'assistant.tool_loading': 'on_demand', 'assistant.tools_pinned': '', 'assistant.tool_preload_score': '0.6' };

export function ToolLoadingCard({ values, onChange }: { values: Record<string, string>; onChange: (key: string, value: string) => void }) {
  const stats = useQuery({ queryKey: ['tool-stats'], queryFn: () => get<Stats>('/api/ops/tools/stats') });
  const v = (k: string) => values[k] ?? DEFAULTS[k];
  const on = v('assistant.tool_loading') !== 'all';
  const pinned = v('assistant.tools_pinned').split(',').map((x) => x.trim()).filter(Boolean);
  const toggle = (code: string) => onChange('assistant.tools_pinned', (pinned.includes(code) ? pinned.filter((x) => x !== code) : [...pinned, code]).join(','));
  const finds = stats.data?.tools?.find((t) => t.tool === 'find_tools')?.calls ?? 0;
  return (
    <div className="card lv-card">
      <div className="lv-hd">
        <b>工具按需加载</b>
        <span className="hint">查资料、查项目、计算、联网搜索这些常用工具每次都带；流程模拟、做 PPT、浏览器这类专用工具平时只列一行用途，
          用得上才把完整说明交给模型——系统按问题先挑，模型查到一半发现要用也能自己加载。工具少了，模型挑得更准，交给模型的工具说明少一半多。</span>
      </div>
      <table className="lv-t">
        <tbody>
          <tr>
            <td style={{ width: 170 }}>按需加载</td>
            <td style={{ width: 120 }}>
              <button type="button" role="switch" aria-checked={on} aria-label="工具按需加载开关" className={'tl-sw' + (on ? ' on' : '')}
                onClick={() => onChange('assistant.tool_loading', on ? 'all' : 'on_demand')}><i /></button>
            </td>
            <td className="hint">关掉就和以前一样，每次把所有工具都交给模型</td>
          </tr>
          <tr>
            <td>按问题先挑的门槛</td>
            <td><input className="m lv-in" style={{ width: 56 }} inputMode="decimal" value={v('assistant.tool_preload_score')} aria-label="按问题先挑的门槛"
              disabled={!on} onChange={(e) => onChange('assistant.tool_preload_score', e.target.value.trim())} /></td>
            <td className="hint">问题和工具用途的相似度到这个数就提前加载（0.3–0.95）。调低多带、调高少带；模型自己也能补加载</td>
          </tr>
          <tr>
            <td style={{ verticalAlign: 'top', paddingTop: 10 }}>常驻的专用工具</td>
            <td colSpan={2}>
              <div className="tl-pins">
                {(stats.data?.catalog ?? []).map((g) => (
                  <button key={g.code} type="button" disabled={!on} aria-pressed={pinned.includes(g.code)} title={g.summary}
                    className={'q-chip' + (pinned.includes(g.code) ? ' on' : '')} onClick={() => toggle(g.code)}>{g.label}</button>
                ))}
              </div>
              <div className="hint" style={{ marginTop: 6 }}>选中的每次都带。比如觉得回答里的图表、流程图变少了，就把「回答组件」设成常驻</div>
            </td>
          </tr>
        </tbody>
      </table>
      <div className="lv-row">
        <span>近 7 天模型自己加载工具 <b className="m">{finds}</b> 次</span>
        <span className="hint">改完点页底「保存」</span>
      </div>
    </div>
  );
}
