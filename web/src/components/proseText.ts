// 正文排版前的文字处理：化学式下标、单位上标、公式写法统一。

// ── 化学式、单位排版 ─────────────────────────────────
const ELEMENTS = new Set(('H He Li Be B C N O F Ne Na Mg Al Si P S Cl Ar K Ca Sc Ti V Cr Mn Fe Co Ni Cu Zn Ga Ge As Se Br Kr Rb Sr Y Zr Nb Mo '
  + 'Ru Rh Pd Ag Cd In Sn Sb Te I Xe Cs Ba La Ce Nd Hf Ta W Re Os Ir Pt Au Hg Tl Pb Bi Th U').split(' '));
const SUB = '₀₁₂₃₄₅₆₇₈₉';
const FORMULA = /(?<![A-Za-z0-9_.-])((?:[A-Z][a-z]?\d{0,2}|\((?:[A-Z][a-z]?\d{0,2})+\)\d{0,2}){2,})(?![A-Za-z0-9_-])/g;
const UNIT = /(?<=[\d\s/(（]|^)(Nm|N?m|cm|mm|dm|km)([23])(?![\dA-Za-z])/g;

/** 像化学式的才改：每段都是元素符号、带数字、含 H/O/C/Cl/S/F 之一（排除 PN16、IP65 这类），铸钢牌号 CF8、CF3 不动。 */
export function chemText(s: string): string {
  let out = s.replace(FORMULA, (m) => {
    if (!/\d/.test(m) || /^CF\d/.test(m)) return m;
    const parts = m.match(/[A-Z][a-z]?/g) ?? [];
    if (!parts.every((p) => ELEMENTS.has(p))) return m;
    if (!parts.some((p) => ['H', 'O', 'C', 'Cl', 'S', 'F'].includes(p))) return m;
    return m.replace(/\d/g, (d) => SUB[Number(d)]);
  });
  out = out.replace(UNIT, (_m, u: string, p: string) => u + (p === '2' ? '²' : '³'));
  return out;
}

/** \( \) 和 \[ \] 写法换成 $ $、$$ $$（代码里的不动）。 */
export function normalizeMath(text: string): string {
  return text.split(/(```[\s\S]*?(?:```|$)|`[^`\n]*`)/g).map((part, i) => (i % 2 === 1 ? part
    : part.replace(/\\\[([\s\S]+?)\\\]/g, (_m, x: string) => `$$${x}$$`).replace(/\\\(([\s\S]+?)\\\)/g, (_m, x: string) => `$${x}$`))).join('');
}

