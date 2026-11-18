import type { ReactNode } from 'react';
import { s } from './util';

/** 来源编号：[3]、3、[2,3] 都认。 */
export function Refs({ v }: { v: unknown }): ReactNode {
  const ids = s(v).split(/[,，、\s[\]]+/).filter((x) => /^\d{1,3}$/.test(x));
  if (ids.length === 0) return null;
  return <span className="refs-inline">{ids.map((r) => <span key={r} className="ref-chip">{r}</span>)}</span>;
}

