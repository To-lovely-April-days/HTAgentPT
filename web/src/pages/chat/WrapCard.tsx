// 多步任务答完后的交付说明：自查结论、按什么假设做的、还没做的（左右两栏），最后问一句，配两三个回复按钮（点了直接发给助手）。
// 计划没做完的（轮数用完等）也收在这张卡里给「接着做」。只有最后一条回答的按钮能点。
import type { WrapData } from '../../lib/types';
import { Icon } from '../../components/Icons';

export function WrapCard({ wrap, unfinished, active, onSend }: {
  wrap: WrapData; unfinished?: string[]; active: boolean; onSend: (text: string) => void;
}) {
  const left = wrap.left ?? [];
  const assumptions = wrap.assumptions ?? [];
  const options = wrap.options ?? [];
  const more = unfinished && unfinished.length > 0 ? unfinished : null;
  return (
    <section className="wrap-card" aria-label="交付说明">
      <div className="wrap-hd">
        <b>交付说明</b>
        {wrap.check && <span className="wrap-check"><Icon name="check" size={12} stroke={3} />自查：{wrap.check}</span>}
      </div>
      {(assumptions.length > 0 || left.length > 0) && (
        <div className="wrap-cols">
          {assumptions.length > 0 && (
            <div className="wrap-sec">
              <b>按这些假设做的</b>
              <ol>{assumptions.map((a) => <li key={a}>{a}</li>)}</ol>
            </div>
          )}
          {left.length > 0 && (
            <div className="wrap-sec">
              <b>还没做 / 没核实的</b>
              <ol>{left.map((a) => <li key={a}>{a}</li>)}</ol>
            </div>
          )}
        </div>
      )}
      {(wrap.ask || more || options.length > 0) && (
        <div className="wrap-ask">
          {wrap.ask && <span>{wrap.ask}</span>}
          {more && (
            <button type="button" className="primary" disabled={!active}
              onClick={() => onSend(`接着做没做完的步骤：${more.join('、')}。前面查到的直接用，不用重新查。`)}>
              <Icon name="play" size={12} />接着做
            </button>
          )}
          {options.map((o, i) => (
            <button key={o} type="button" className={!more && i === 0 ? 'primary' : undefined} disabled={!active} onClick={() => onSend(o)}>{o}</button>
          ))}
        </div>
      )}
    </section>
  );
}
