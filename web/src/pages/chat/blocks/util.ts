// 组件共用的小工具：模型给的数据形状不保证完整，这里一律宽松读取，缺了就不显示。

export type D = Record<string, unknown>;

export const s = (v: unknown): string => (v == null ? '' : typeof v === 'string' ? v : typeof v === 'number' || typeof v === 'boolean' ? String(v) : '');
export const n = (v: unknown): number | null => {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  const m = /-?\d[\d,]*(\.\d+)?/.exec(s(v).replace(/−/g, '-'));
  return m ? Number(m[0].replace(/,/g, '')) : null;
};
export const list = (v: unknown): D[] => (Array.isArray(v) ? v.filter((x): x is D => !!x && typeof x === 'object' && !Array.isArray(x)) : []);
export const nums = (v: unknown): number[] => (Array.isArray(v) ? v.map((x) => n(x)).filter((x): x is number => x != null) : []);
export const strs = (v: unknown): string[] => (Array.isArray(v) ? v.map(s).filter(Boolean) : s(v) ? [s(v)] : []);
export const fmt = (v: number) => v.toLocaleString('zh-CN', { maximumFractionDigits: Math.abs(v) >= 100 ? 0 : Math.abs(v) >= 1 ? 2 : 4 });

export async function exportXlsx(name: string, rows: (string | number | null)[][]) {
  const XLSX = await import('xlsx');
  const wb = XLSX.utils.book_new();
  XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet(rows), '数据');
  XLSX.writeFile(wb, `${name || '导出'}.xlsx`);
}

export const PALETTE = ['#2A4A85', '#C26A00', '#1C6B45', '#5A3A8A', '#22606F', '#A00D25', '#6B6B6B'];

/** 坐标轴取整：0、1、2、2.5、5 × 10ⁿ 一档。 */
export function niceMax(v: number): number {
  if (v <= 0) return 1;
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  for (const m of [1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10]) if (m * p >= v) return m * p;
  return 10 * p;
}

export function followupsMail(d: D): string {
  const items = list(d.items);
  const ours = items.filter((x) => s(x.status) !== '客户方' && s(x.status) !== '已完成');
  const theirs = items.filter((x) => s(x.status) === '客户方');
  const line = (x: D) => `· ${s(x.task)}（${s(x.owner)}${s(x.due) ? `，${s(x.due)} 前` : ''}）`;
  return ['您好，今天沟通的待办整理如下：', '', theirs.length ? '需要贵方确认：' : '', ...theirs.map(line), '', ours.length ? '我方跟进：' : '', ...ours.map(line)]
    .filter((x, i, a) => !(x === '' && a[i - 1] === '')).join('\n').trim();
}


/** 坐标轴分几格：让每格的数是 1、2、2.5、5 × 10ⁿ 这样的整数档。 */
export function tickFractions(max: number): number[] {
  const nice = (v: number) => { const m = v / Math.pow(10, Math.floor(Math.log10(v))); return [1, 2, 2.5, 5, 10].some((x) => Math.abs(m - x) < 1e-9); };
  const div = [5, 4, 3, 2].find((d) => max > 0 && nice(max / d)) ?? 4;
  return Array.from({ length: div + 1 }, (_, i) => i / div);
}

/** 向上取到 1、2、2.5、5 × 10ⁿ。 */
export function niceStep(v: number): number {
  if (v <= 0) return 1;
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  for (const m of [1, 2, 2.5, 5, 10]) if (m * p >= v - 1e-12) return m * p;
  return 10 * p;
}
