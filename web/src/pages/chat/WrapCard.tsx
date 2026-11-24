// 多步任务的收尾卡：自查结论、按什么假设做的、还有什么没做，最后问一句，配两三个按钮（点了直接发给助手）。
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
    <section className="wrap-card" aria-label="收尾">
      {wrap.check && (
        <div className="wrap-check"><Icon name="check" size={14} stroke={2.2} />自查：{wrap.check}</div>
      )}
      {assumptions.length > 0 && (
        <div className="wrap-sec">
          <b><Icon name="bulb" size={14} className="gi l" />按这些假设做的</b>
          <ul>{assumptions.map((a) => <li key={a}>{a}</li>)}</ul>
        </div>
      )}
      {left.length > 0 && (
        <div className="wrap-sec">
          <b><Icon name="warn" size={14} className="gi l" />还没做 / 没核实</b>
          <ul>{left.map((a) => <li key={a}>{a}</li>)}</ul>
        </div>
      )}
      {(wrap.ask || more) && (
        <div className="wrap-ask">
          {wrap.ask && <span>{wrap.ask}</span>}
          <div className="wrap-btns">
            {more && (
              <button type="button" className="pbtn" disabled={!active}
                onClick={() => onSend(`接着做没做完的步骤：${more.join('、')}。前面查到的直接用，不用重新查。`)}>
                <Icon name="play" size={13} className="gi l" />接着做
              </button>
            )}
            {options.map((o) => (
              <button key={o} type="button" className="gbtn" disabled={!active} onClick={() => onSend(o)}>{o}</button>
            ))}
          </div>
        </div>
      )}
    </section>
  );
}
