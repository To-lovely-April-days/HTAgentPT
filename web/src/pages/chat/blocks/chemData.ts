// 化工组件的纯数据处理：化学式上下标、导出 Excel 的行。
import type { D } from './util';
import { list, n, s } from './util';

export interface FormulaPart { t: string; k: 'n' | 'sub' | 'sup' }

/** 化学式拆成「正文 / 下标 / 上标」几段：元素后面的数字是下标，末尾电荷是上标，开头的系数和点号后的结晶水系数照常写。 */
export function chemParts(f: string): FormulaPart[] {
  const out: FormulaPart[] = [];
  const charge = /\^?(\d*[+-])$/.exec(f);
  const body = charge ? f.slice(0, charge.index) : f;
  const re = /(?<=[A-Za-z)\]])\d+/g;
  let last = 0; let m: RegExpExecArray | null;
  while ((m = re.exec(body)) !== null) {
    if (m.index > last) out.push({ t: body.slice(last, m.index), k: 'n' });
    out.push({ t: m[0], k: 'sub' });
    last = m.index + m[0].length;
  }
  if (last < body.length) out.push({ t: body.slice(last), k: 'n' });
  if (charge) out.push({ t: charge[1], k: 'sup' });
  return out;
}

/** 导出 Excel 用的行。 */
export function chemRows(kind: string, d: D): (string | number | null)[][] | null {
  switch (kind) {
    case 'pfd': return [['物流', '从', '到', '温度', '压力', '流量', '单位', '相态'], ...list(d.streams).map((x) => [s(x.id), s(x.from) || '进料', s(x.to) || '产品', s(x.T), s(x.P), n(x.flow), s(x.unit), s(x.phase)])];
    case 'pid': return [['仪表位号', '位置', '用途', '量程', '信号去向'], ...list(d.instruments).map((x) => [s(x.tag), s(x.on), s(x.service), s(x.range), s(x.signal_to)])];
    case 'equipment': return [['管口', '尺寸', '法兰', '用途', '方位'], ...list(d.nozzles).map((x) => [s(x.id), s(x.size), s(x.flange), s(x.service), s(x.position)])];
    case 'interlock': {
      const effects = list(d.effects);
      const links = new Set(list(d.links).map((x) => `${s(x.cause)}>${s(x.effect)}`));
      return [['触发条件', ...effects.map((e) => `${s(e.id)} ${s(e.tag)} ${s(e.action)}`)],
        ...list(d.causes).map((c) => [`${s(c.id)} ${s(c.tag)} ${s(c.condition)}`, ...effects.map((e) => (links.has(`${s(c.id)}>${s(e.id)}`) ? '●' : ''))])];
    }
    default: return null;
  }
}
