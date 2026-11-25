// 小人按天气穿的衣服。在原来的像素图上改格子（换颜色、加几格），不另画一套人，所以走路、坐着、举手每个姿势都穿着。
// 衣服按「一件一件」拆开：每一件是相对上一步变了的那些格子，穿衣动画就是把这些层按顺序放出来（见 DressedSprite）。
// 四周留了白（左右各 2 格、上面 3 格）：草帽帽檐、毛线帽绒球、羽绒服鼓出来的地方会伸出原来的框。
import { H, POSES, W } from './sprite';
import type { Pose } from './sprite';

export type Outfit = 'normal' | 'cool' | 'cold' | 'freezing' | 'hot' | 'rain' | 'mask';
/** 这一件怎么穿上：从左往右绕、从下往上拉、从上往下套、从上面掉下来扣上、弹出来、往下滑、往上拉。 */
export type Anim = 'wipeR' | 'wipeU' | 'wipeD' | 'drop' | 'pop' | 'slide' | 'slideUp';

export const OX = 2;
export const OY = 3;
const GW = W + OX * 2;
const GH = H + OY;
type Grid = string[][];

const at = (g: Grid, x: number, y: number) => g[y + OY]?.[x + OX] ?? '.';
const put = (g: Grid, x: number, y: number, c: string) => {
  if (y + OY >= 0 && y + OY < GH && x + OX >= 0 && x + OX < GW) g[y + OY][x + OX] = c;
};
const hline = (g: Grid, y: number, x0: number, x1: number, c: string) => { for (let x = x0; x <= x1; x++) put(g, x, y, c); };
const CLOTH = 'KkR';
const isCloth = (c: string) => CLOTH.includes(c);
/** 胳膊（举起来的时候在头两边）：只认 4–7、24–27 两列，别把耳机当成袖子。 */
const isArm = (x: number, y: number) => y >= 20 && ((x >= 4 && x <= 7) || (x >= 24 && x <= 27));

function grid(pose: Pose): Grid {
  const g: Grid = Array.from({ length: GH }, () => Array<string>(GW).fill('.'));
  POSES[pose].forEach((r, y) => { for (let x = 0; x < r.length; x++) g[y + OY][x + OX] = r[x]; });
  return g;
}

// ── 围巾：红白条纹针织，打个结，两条尾巴垂在胸前，末端有流苏
function scarf(g: Grid) {
  for (const y of [24, 25]) for (let x = 7; x <= 24; x++) put(g, x, y, x === 7 || x === 24 ? 'O' : Math.floor(x / 2) % 2 === 0 ? 'R' : 'W');
  hline(g, 23, 8, 23, 'O'); hline(g, 26, 8, 23, 'O');
  for (let y = 26; y <= 28; y++) for (let x = 17; x <= 21; x++) put(g, x, y, x === 17 || x === 21 || y === 28 ? 'O' : 'r');
  for (let y = 28; y <= 33; y++) for (let x = 17; x <= 20; x++) put(g, x, y, x === 17 || x === 20 ? 'O' : Math.floor(y / 2) % 2 === 0 ? 'R' : 'W');
  hline(g, 34, 17, 20, 'O'); put(g, 18, 35, 'R'); put(g, 19, 35, 'W');
  for (let y = 28; y <= 31; y++) for (let x = 21; x <= 23; x++) put(g, x, y, (x === 21 || x === 23) && y > 28 ? 'O' : Math.floor(y / 2) % 2 ? 'W' : 'R');
  hline(g, 32, 21, 23, 'O'); put(g, 22, 33, 'R');
}

// ── 羽绒服：衣服和袖子换成藏青，三道鼓起来的绗缝（上亮下暗），两边往外鼓一格，中间拉链，立领
function puffer(g: Grid) {
  const band = (y: number) => (y < 28 ? 'N' : (y - 28) % 3 === 0 ? 'L' : (y - 28) % 3 === 2 ? 'n' : 'N');
  for (let y = 20; y <= 36; y++) {
    for (let x = 0; x < W; x++) {
      if (!isCloth(at(g, x, y))) continue;
      if (y < 24 && !isArm(x, y)) continue;
      put(g, x, y, band(y));
    }
  }
  for (let y = 28; y <= 36; y++) {
    let l = -1, r = -1;
    for (let x = 0; x < W; x++) if (at(g, x, y) !== '.') { if (l < 0) l = x; r = x; }
    if (l > 0 && at(g, l, y) === 'O') { put(g, l - 1, y, 'O'); put(g, l, y, band(y)); }
    if (r >= 0 && r < W - 1 && at(g, r, y) === 'O') { put(g, r + 1, y, 'O'); put(g, r, y, band(y)); }
  }
  for (let y = 28; y <= 36; y++) if (!'.O'.includes(at(g, 15, y))) { put(g, 15, y, 'c'); put(g, 16, y, 'c'); }
  put(g, 15, 29, 'W'); put(g, 16, 29, 'W');
  for (let y = 26; y <= 27; y++) for (let x = 10; x <= 21; x++) if (at(g, x, y) !== '.') put(g, x, y, x === 10 || x === 21 ? 'O' : y === 26 ? 'L' : 'N');
}

// ── 毛线帽：罩住头盔上半（竖着的针织纹、翻边一圈），顶上一个红绒球
function beanie(g: Grid) {
  for (let y = 0; y <= 5; y++) for (let x = 4; x <= 27; x++) if (isCloth(at(g, x, y))) put(g, x, y, x % 3 === 0 ? 'q' : 'Q');
  for (let x = 6; x <= 25; x++) {
    if (at(g, x, 4) !== '.') put(g, x, 4, x % 2 ? 'q' : 'Q');
    if (at(g, x, 5) !== '.') put(g, x, 5, x % 2 ? 'Q' : 'q');
  }
  ['..OOOO..', '.ORRRWO.', 'ORRRRRWO', 'ORRRRRRO', '.ORRRRO.', '..OOOO..'].forEach((r, dy) => {
    for (let dx = 0; dx < r.length; dx++) if (r[dx] !== '.') put(g, 12 + dx, -3 + dy, r[dx]);
  });
}

// ── 手套：手换成红色，手腕一圈白
function mittens(g: Grid) {
  const hands: [number, number][] = [];
  for (let y = 20; y < H; y++) for (let x = 0; x < W; x++) if (at(g, x, y) === 'G' && isArm(x, y)) hands.push([x, y]);
  for (const [x, y] of hands) {
    put(g, x, y, 'R');
    const up = at(g, x, y - 1);
    if (up !== 'G' && up !== 'R' && up !== '.' && up !== 'O') put(g, x, y - 1, 'W');
  }
}

// ── 夏天：白短袖、胳膊和腿露出来、深蓝短裤、墨镜
function summer(g: Grid) {
  for (let y = 24; y <= 27; y++) for (let x = 0; x < W; x++) if (isCloth(at(g, x, y)) && !isArm(x, y)) put(g, x, y, y < 26 ? 'S' : 'W');
  for (let y = 20; y <= 36; y++) {
    for (let x = 0; x < W; x++) {
      const c = at(g, x, y);
      if (!isCloth(c) && c !== 'G') continue;
      if (isArm(x, y)) { if (y >= 28 && y <= 30 && c !== 'G') put(g, x, y, 'W'); else if (y >= 24 || c === 'G') put(g, x, y, 'S'); }
      else if (y >= 28 && c !== 'G') put(g, x, y, c === 'R' && (y === 33 || y === 34) ? 'c' : 'W');
    }
  }
  for (let y = 37; y <= 40; y++) for (let x = 0; x < W; x++) if ('Kk'.includes(at(g, x, y))) put(g, x, y, y < 39 ? 'U' : 'S');
  for (const y of [15, 16, 17]) for (let x = 8; x <= 23; x++) if (x <= 13 || x >= 18) put(g, x, y, 'O');
  hline(g, 15, 14, 17, 'O');
  put(g, 9, 15, 'B'); put(g, 10, 16, 'B'); put(g, 19, 15, 'B'); put(g, 20, 16, 'B');
}

// ── 草帽：宽帽檐、红丝带
function straw(g: Grid) {
  ['....OOOOOOOOOO....', '...OhhhhhhhhhhO...', '..OhhjhhhhhhjhhO..', '..ORRRRRRRRRRRRO..'].forEach((r, dy) => {
    for (let dx = 0; dx < r.length; dx++) if (r[dx] !== '.') put(g, 7 + dx, -2 + dy, r[dx]);
  });
  hline(g, 2, -1, W, 'O'); hline(g, 3, -2, W + 1, 'h'); put(g, -2, 3, 'O'); put(g, W + 1, 3, 'O');
  for (let x = -1; x <= W; x += 4) put(g, x, 3, 'j');
  hline(g, 4, -1, W, 'O');
}

// ── 雨衣：黄色，帽兜把耳机一圈包住，两排扣子，黄雨靴
function raincoat(g: Grid) {
  for (let y = 0; y < 24; y++) {
    for (let x = 0; x < W; x++) {
      const c = at(g, x, y);
      if ((y <= 5 || y >= 11) && isCloth(c) && !(x >= 6 && x <= 25 && y >= 13)) put(g, x, y, c === 'k' || x <= 1 || x >= 30 ? 'v' : 'V');
    }
  }
  for (let y = 24; y <= 37; y++) for (let x = 0; x < W; x++) if (isCloth(at(g, x, y))) put(g, x, y, y === 24 || y === 27 || y === 33 ? 'v' : 'V');
  for (const y of [29, 31, 33, 35]) { if (at(g, 13, y) !== '.') put(g, 13, y, 'O'); if (at(g, 18, y) !== '.') put(g, 18, y, 'O'); }
  for (let y = 38; y < H; y++) for (let x = 0; x < W; x++) if ('KkRr'.includes(at(g, x, y))) put(g, x, y, y === 38 ? 'v' : 'V');
}

// ── 口罩：浅蓝医用口罩，三道褶，挂绳挂在耳机上
function mask(g: Grid) {
  for (let y = 18; y <= 23; y++) for (let x = 7; x <= 24; x++) if ('SsPEW'.includes(at(g, x, y))) put(g, x, y, (y === 20 || y === 22) && x >= 9 && x <= 22 ? 'x' : 'X');
  hline(g, 18, 8, 23, 'O'); put(g, 7, 19, 'O'); put(g, 24, 19, 'O');
  for (let x = 4; x <= 7; x++) put(g, x, x < 6 ? 18 : 19, 'c');
  for (let x = 24; x <= 27; x++) put(g, x, x < 26 ? 19 : 18, 'c');
}

interface Part { from: number; to: number; anim: Anim; dur: number; label: string }
interface Step { fn: (g: Grid) => void; parts: Part[] }
const ALL = (anim: Anim, dur: number, label: string): Part => ({ from: -9, to: 99, anim, dur, label });

/** 每一套怎么一件件穿：哪一步、拆成哪几层（按行分）、怎么动、多久。 */
const PLAN: Record<Outfit, Step[]> = {
  normal: [],
  cool: [{ fn: scarf, parts: [{ from: -9, to: 26, anim: 'wipeR', dur: 0.6, label: '围巾绕上脖子' }, { from: 27, to: 99, anim: 'drop', dur: 0.4, label: '打结' }] }],
  cold: [
    { fn: puffer, parts: [ALL('wipeU', 0.8, '穿羽绒服、拉拉链')] },
    { fn: scarf, parts: [{ from: -9, to: 26, anim: 'wipeR', dur: 0.5, label: '绕围巾' }, { from: 27, to: 99, anim: 'drop', dur: 0.35, label: '打结' }] },
  ],
  freezing: [
    { fn: puffer, parts: [ALL('wipeU', 0.8, '穿羽绒服、拉拉链')] },
    { fn: scarf, parts: [{ from: -9, to: 26, anim: 'wipeR', dur: 0.45, label: '绕围巾' }, { from: 27, to: 99, anim: 'drop', dur: 0.3, label: '打结' }] },
    { fn: beanie, parts: [{ from: 0, to: 99, anim: 'drop', dur: 0.45, label: '戴毛线帽' }, { from: -9, to: -1, anim: 'pop', dur: 0.35, label: '绒球' }] },
    { fn: mittens, parts: [ALL('pop', 0.3, '戴手套')] },
  ],
  hot: [
    { fn: summer, parts: [{ from: 19, to: 99, anim: 'wipeD', dur: 0.6, label: '换短袖短裤' }, { from: -9, to: 18, anim: 'slide', dur: 0.4, label: '戴墨镜' }] },
    { fn: straw, parts: [ALL('drop', 0.5, '扣上草帽')] },
  ],
  rain: [{
    fn: raincoat, parts: [
      { from: 24, to: 37, anim: 'wipeD', dur: 0.6, label: '套上雨衣' },
      { from: -9, to: 23, anim: 'wipeD', dur: 0.45, label: '戴帽兜' },
      { from: 38, to: 99, anim: 'pop', dur: 0.35, label: '穿雨靴' },
    ],
  }],
  mask: [{ fn: mask, parts: [ALL('slideUp', 0.5, '戴口罩')] }],
};

/** 一层：在原图坐标里的位置（x、y 可以是负数）、宽高、字符画、怎么动、什么时候开始。 */
export interface Layer { x: number; y: number; w: number; h: number; rows: string[]; anim: Anim; delay: number; dur: number; key: string }
export interface Dressed { layers: Layer[]; total: number }

const GAP = 0.15;
const cache = new Map<string, Dressed>();

/** 这个姿势穿上这一套：一层层的衣服和穿完一共要几秒。 */
export function dress(pose: Pose, outfit: Outfit): Dressed {
  const key = pose + ':' + outfit;
  const hit = cache.get(key);
  if (hit) return hit;
  let cur = grid(pose);
  const layers: Layer[] = [];
  let t = 0;
  for (const step of PLAN[outfit]) {
    const next = cur.map((r) => r.slice());
    step.fn(next);
    for (const part of step.parts) {
      const cells: [number, number, string][] = [];
      for (let gy = 0; gy < GH; gy++) {
        const y = gy - OY;
        if (y < part.from || y > part.to) continue;
        for (let gx = 0; gx < GW; gx++) if (next[gy][gx] !== cur[gy][gx] && next[gy][gx] !== '.') cells.push([gx, gy, next[gy][gx]]);
      }
      if (cells.length === 0) continue;
      const xs = cells.map((c) => c[0]), ys = cells.map((c) => c[1]);
      const x0 = Math.min(...xs), y0 = Math.min(...ys), w = Math.max(...xs) - x0 + 1, h = Math.max(...ys) - y0 + 1;
      const rows = Array.from({ length: h }, () => Array<string>(w).fill('.'));
      for (const [gx, gy, c] of cells) rows[gy - y0][gx - x0] = c;
      layers.push({ x: x0 - OX, y: y0 - OY, w, h, rows: rows.map((r) => r.join('')), anim: part.anim, delay: t, dur: part.dur, key: `${key}:${layers.length}` });
      t += part.dur + GAP;
    }
    cur = next;
  }
  const out = { layers, total: t };
  cache.set(key, out);
  return out;
}

/** 天气接口说的那套，认不得的当平常。 */
export const outfitOf = (s: string | null | undefined): Outfit =>
  s === 'cool' || s === 'cold' || s === 'freezing' || s === 'hot' || s === 'rain' || s === 'mask' ? s : 'normal';
