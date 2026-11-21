// 系统设置 · 思考档位：三档各自的参数（思考强度、最多查几轮、回答最长、温度），默认档位，员工能不能自己选，模型服务的上下文长度。
// 思考强度是 Qwen3.8 自带的三级；温度以外的采样参数按官方推荐跟着「思考 / 不思考」自动带，不放到界面上。
import { Icon } from '../../components/Icons';

const LEVELS: { code: 'fast' | 'standard' | 'deep'; name: string }[] = [
  { code: 'fast', name: '快速' }, { code: 'standard', name: '标准' }, { code: 'deep', name: '深度' },
];
const EFFORTS: [string, string][] = [['off', '不思考'], ['low', '低 low'], ['medium', '中 medium'], ['xhigh', '最高 xhigh']];
const DEFAULTS: Record<string, string> = {
  'assistant.level.default': 'auto', 'assistant.level.user_choice': 'true', 'model.chat.context_tokens': '8192',
  'assistant.level.fast.effort': 'off', 'assistant.level.fast.rounds': '3', 'assistant.level.fast.max_tokens': '2000', 'assistant.level.fast.temperature': '0.7',
  'assistant.level.standard.effort': 'medium', 'assistant.level.standard.rounds': '6', 'assistant.level.standard.max_tokens': '6000', 'assistant.level.standard.temperature': '1.0',
  'assistant.level.deep.effort': 'xhigh', 'assistant.level.deep.rounds': '10', 'assistant.level.deep.max_tokens': '12000', 'assistant.level.deep.temperature': '1.0',
};
const DEEP_MIN = 32768;

export function LevelsCard({ values, onChange }: { values: Record<string, string>; onChange: (key: string, value: string) => void }) {
  const v = (k: string) => values[k] ?? DEFAULTS[k] ?? '';
  const context = Number(v('model.chat.context_tokens')) || 0;
  const deepOk = context >= DEEP_MIN;
  const userChoice = v('assistant.level.user_choice') !== 'false';
  const num = (k: string, w = 72) => (
    <input className="m lv-in" style={{ width: w }} inputMode="decimal" value={v(k)} onChange={(e) => onChange(k, e.target.value.trim())} aria-label={k} />
  );

  return (
    <div className="card lv-card">
      <div className="lv-hd">
        <b>思考档位</b>
        <span className="hint">员工在输入框里选「自动 / 快速 / 标准 / 深度」。思考强度是模型自带的三级，温度以外的采样参数按官方推荐自动带。改完点页底「保存」，马上生效。</span>
      </div>
      <table className="lv-t">
        <thead><tr><th>档位</th><th>思考强度</th><th>最多查几轮</th><th>回答最长（token）</th><th>温度</th><th>状态</th></tr></thead>
        <tbody>
          {LEVELS.map((l) => (
            <tr key={l.code}>
              <td><b>{l.name}</b></td>
              <td>
                <select className="lv-in" value={v(`assistant.level.${l.code}.effort`)} onChange={(e) => onChange(`assistant.level.${l.code}.effort`, e.target.value)} aria-label={`${l.name}档思考强度`}>
                  {EFFORTS.map(([c, t]) => <option key={c} value={c}>{t}</option>)}
                </select>
              </td>
              <td>{num(`assistant.level.${l.code}.rounds`, 56)} 轮</td>
              <td>{num(`assistant.level.${l.code}.max_tokens`, 88)}</td>
              <td>{num(`assistant.level.${l.code}.temperature`, 56)}</td>
              <td>{l.code === 'deep' && !deepOk ? <span className="lv-pill warn">要 32K 上下文</span> : <span className="lv-pill ok">开放</span>}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="lv-row">
        <label>默认档位
          <select className="lv-in" value={v('assistant.level.default')} onChange={(e) => onChange('assistant.level.default', e.target.value)}>
            <option value="auto">自动</option><option value="fast">快速</option><option value="standard">标准</option><option value="deep">深度</option>
          </select>
        </label>
        <button type="button" className="lv-check" aria-pressed={userChoice} onClick={() => onChange('assistant.level.user_choice', userChoice ? 'false' : 'true')}>
          <Icon name={userChoice ? 'boxOn' : 'boxOff'} size={15} />员工可以自己选
        </button>
        <label>模型服务的上下文 {num('model.chat.context_tokens', 88)} <span className="hint">token，即 vLLM 的 --max-model-len</span></label>
      </div>
      {!deepOk && (
        <div className="lv-warn">
          <Icon name="warn" size={14} stroke={2} />
          <div><b>上下文现在是 {context.toLocaleString()}，深度档放不下</b>
            <span>资料、历史对话、思考和回答都要装进去。换成官方 FP8 权重后显存够把上下文开到 32K（32768），在模型服务上调好后把这里也改成一样的数，深度档就开放；之前输入框里的「深度」是灰的。</span></div>
        </div>
      )}
    </div>
  );
}
