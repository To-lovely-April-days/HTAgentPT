// 住在输入框上沿的 8-bit 小人。放在输入框（position: relative）里面，自己找同一个框里的文本框来听打字。
// 空闲时沿上沿走来走去、走累了坐下；打字时走到光标上方低头看；发送时跳一下就离开（跳进回答里干活，见 ThinkingStage），答完跳回来坐下；
// 很久没人动就打瞌睡。顺手碰到的几种反应：
// · 点它一下：按时段说句生活上的话（喝口水、该吃饭了）；连着戳 5 下它晕头转向冒星星
// · 鼠标凑过去：停下抬头看你
// · 往页面上拖文件：跑到中间举手接
// · 首页「需要你看的」都看完了：欢呼（首页发 htpet 事件）
// · 连着忙了两个小时：端着咖啡提醒起来喝口水、走两步（一天一次）
// · 18 点以后打开：挥手说辛苦啦；22 点以后：坐着打瞌睡，点醒了叫你早点休息
// · 过节：旁边多一样道具（灯笼、月亮……）
// · 按天气穿衣服（围巾、羽绒服、毛线帽、草帽、雨衣、口罩）：天气一到，它停下来一件件穿上，穿好跳一下；一天穿一次，天气变了再换
// 系统设置了「减少动态效果」的人，它只换姿势不走动。
import { useCallback, useEffect, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get } from '../../lib/api';
import { PixelIcon, PixelProp, PixelSprite } from './PixelSprite';
import { DressedSprite } from './DressedSprite';
import { dress, outfitOf } from './outfit';
import type { Outfit } from './outfit';
import { useWeather } from './weather';
import type { Icon, Pose } from './sprite';
import { byeLine, festivalOf, isLate, talkLine } from './days';

type Mode = 'walk' | 'sit' | 'look' | 'jump' | 'away' | 'sleepy' | 'talk' | 'dizzy' | 'peek' | 'catch' | 'cheer' | 'bye' | 'dress';

// 今天已经穿上的那套（这台电脑的浏览器里记着；存不了就每次打开都穿一遍）
const WORN_KEY = 'htpet-outfit';
function wornToday(): Outfit | null {
  try {
    const s = JSON.parse(localStorage.getItem(WORN_KEY) ?? 'null') as { day?: string; outfit?: string } | null;
    if (s?.day === new Date().toDateString()) return outfitOf(s.outfit);
  } catch { /* 读不了 */ }
  return null;
}
function saveWorn(o: Outfit) { try { localStorage.setItem(WORN_KEY, JSON.stringify({ day: new Date().toDateString(), outfit: o })); } catch { /* 存不了 */ } }

/** 首页让小人做个反应：window.dispatchEvent(new CustomEvent('htpet', { detail: 'cheer' }))。 */
export type PetCue = 'cheer';

interface Today { name?: string }

const SPEED = 26;          // 像素 / 秒
const SLEEP_AFTER = 120_000;
/** 连着忙多久提醒歇一下；中间停过 10 分钟以上就重新算。 */
const WORK_LONG = 2 * 60 * 60_000;
const BREAK_GAP = 10 * 60_000;

const reduced = () => typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;

/** 文本框里光标前那一行文字有多宽（用同样的字体量），换算成相对输入框左边的位置。 */
function caretX(ta: HTMLTextAreaElement, host: HTMLElement): number {
  const before = ta.value.slice(0, ta.selectionStart ?? ta.value.length);
  const line = before.split('\n').pop() ?? '';
  const cs = getComputedStyle(ta);
  const c = document.createElement('canvas').getContext('2d');
  if (!c) return 0;
  c.font = `${cs.fontWeight} ${cs.fontSize} ${cs.fontFamily}`;
  const w = Math.min(c.measureText(line).width, ta.clientWidth - 20);
  const a = ta.getBoundingClientRect(), b = host.getBoundingClientRect();
  return a.left - b.left + parseFloat(cs.paddingLeft || '0') + w;
}

export function InputPet({ busy, size = 32 }: { busy: boolean; size?: number }) {
  const ref = useRef<HTMLDivElement>(null);
  const [mode, setMode] = useState<Mode>(() => (isLate(new Date()) ? 'sleepy' : 'walk'));
  const [x, setX] = useState(24);
  const [frame, setFrame] = useState(0);
  const [blink, setBlink] = useState(false);
  const [smooth, setSmooth] = useState(false);
  const [say, setSay] = useState<{ label?: string; text: string } | null>(null);
  const state = useRef({ mode: (isLate(new Date()) ? 'sleepy' : 'walk') as Mode, x: 24, dir: 1, width: 600, lastInput: 0, walkUntil: 0, pokes: [] as number[] });
  const [tipLeft, setTipLeft] = useState(false);
  const today = useQuery({ queryKey: ['home-today'], queryFn: () => get<Today>('/api/home/today'), staleTime: 30 * 60_000, retry: false });
  const name = today.data?.name ?? '';
  const still = reduced();
  const fest = festivalOf(new Date());
  const unit = size / 32;
  const weather = useWeather();
  const outfit: Outfit = weather.data?.ok ? outfitOf(weather.data.outfit) : 'normal';
  const [worn, setWorn] = useState<Outfit>(() => wornToday() ?? 'normal');
  const [dressing, setDressing] = useState<number | null>(null);

  const go = useCallback((m: Mode) => { state.current.mode = m; setMode(m); }, []);
  const moveTo = useCallback((nx: number, glide: boolean) => {
    const max = Math.max(8, state.current.width - size - 8);
    const v = Math.max(8, Math.min(max, nx));
    state.current.x = v; setSmooth(glide); setX(v);
  }, [size]);

  // 说完一句、晕完、欢呼完：过一会儿自己收起，坐下歇会儿再溜达
  const back = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const settle = useCallback((ms: number) => {
    clearTimeout(back.current);
    back.current = setTimeout(() => {
      setSay(null);
      const s = state.current;
      if (s.mode === 'away' || s.mode === 'jump' || s.mode === 'dress') return;
      go('sit');
      s.walkUntil = Date.now() + 9000;
      setTimeout(() => { if (state.current.mode === 'sit' && !still) go('walk'); }, 2500);
    }, ms);
  }, [go, still]);
  useEffect(() => () => clearTimeout(back.current), []);
  const speak = useCallback((m: Mode, text: string | null, ms: number, label?: string) => {
    if (state.current.mode === 'dress') return;
    setTipLeft(state.current.x > state.current.width / 2);
    setSay(text ? { label, text } : null);
    go(m);
    settle(ms);
  }, [go, settle]);

  // 量输入框宽度；记下开始的时间
  useEffect(() => {
    state.current.lastInput = Date.now();
    state.current.walkUntil = Date.now() + 9000;
    const host = ref.current?.parentElement;
    if (!host) return;
    const ro = new ResizeObserver(() => { state.current.width = host.clientWidth; });
    ro.observe(host);
    state.current.width = host.clientWidth;
    return () => ro.disconnect();
  }, []);

  // 打字：走到光标上方低头看；停下 4 秒后接着溜达
  useEffect(() => {
    const host = ref.current?.parentElement;
    const ta = host?.querySelector('textarea');
    if (!host || !ta) return;
    let t: ReturnType<typeof setTimeout> | undefined;
    const onInput = () => {
      state.current.lastInput = Date.now();
      if (state.current.mode === 'away' || state.current.mode === 'jump') return;
      setSay(null); clearTimeout(back.current);
      if (!ta.value.trim()) { go('sit'); return; }
      go('look');
      moveTo(caretX(ta, host) - size / 2, true);
      clearTimeout(t);
      t = setTimeout(() => { if (state.current.mode === 'look') { state.current.walkUntil = Date.now() + 8000; go(still ? 'sit' : 'walk'); } }, 4000);
    };
    ta.addEventListener('input', onInput);
    ta.addEventListener('click', onInput);
    return () => { ta.removeEventListener('input', onInput); ta.removeEventListener('click', onInput); clearTimeout(t); };
  }, [go, moveTo, size, still]);

  // 有人动就醒（深夜除外：它就打着瞌睡，点它才醒）；鼠标凑近就停下抬头看
  useEffect(() => {
    const onMove = (e: PointerEvent) => {
      const s = state.current;
      s.lastInput = Date.now();
      if (s.mode === 'sleepy' && !isLate(new Date())) go('sit');
      const r = ref.current?.getBoundingClientRect();
      if (!r) return;
      const d = Math.hypot(e.clientX - (r.left + r.width / 2), e.clientY - (r.top + r.height / 2));
      if (d < 90 && (s.mode === 'walk' || s.mode === 'sit')) go('peek');
      else if (d > 140 && s.mode === 'peek') { s.walkUntil = Date.now() + 6000; go(still ? 'sit' : 'walk'); }
    };
    const onKey = () => {
      state.current.lastInput = Date.now();
      if (state.current.mode === 'sleepy') go('sit');
    };
    window.addEventListener('pointermove', onMove, { passive: true });
    window.addEventListener('keydown', onKey);
    return () => { window.removeEventListener('pointermove', onMove); window.removeEventListener('keydown', onKey); };
  }, [go, still]);

  // 往页面上拖文件：跑到中间举手接；拖走了或放下了就回去
  useEffect(() => {
    let depth = 0;
    const isFile = (e: DragEvent) => !!e.dataTransfer?.types.includes('Files');
    const free = () => state.current.mode !== 'away' && state.current.mode !== 'jump' && state.current.mode !== 'dress';
    const onEnter = (e: DragEvent) => {
      if (!isFile(e)) return;
      depth++;
      if (depth === 1 && free()) {
        clearTimeout(back.current);
        moveTo(state.current.width / 2 - size / 2, true);
        setTipLeft(false);
        setSay({ text: '放到输入框上，松手就行' });
        go('catch');
      }
    };
    const onLeave = (e: DragEvent) => {
      if (!isFile(e)) return;
      depth = Math.max(0, depth - 1);
      if (depth === 0 && state.current.mode === 'catch') settle(0);
    };
    // 页面别处也接住拖动，松手时不至于让浏览器把文件直接打开、离开这个页面
    const onOver = (e: DragEvent) => { if (isFile(e)) e.preventDefault(); };
    const onDrop = (e: DragEvent) => {
      if (depth === 0) return;
      depth = 0;
      const took = e.defaultPrevented;   // 输入框接住了会先处理掉
      if (!took) e.preventDefault();
      if (state.current.mode === 'catch') speak(took ? 'cheer' : 'sit', took ? '收到，我先读一遍' : '要放到输入框上哦', 2500);
    };
    window.addEventListener('dragenter', onEnter);
    window.addEventListener('dragleave', onLeave);
    window.addEventListener('dragover', onOver);
    window.addEventListener('drop', onDrop);
    return () => {
      window.removeEventListener('dragenter', onEnter); window.removeEventListener('dragleave', onLeave);
      window.removeEventListener('dragover', onOver); window.removeEventListener('drop', onDrop);
    };
  }, [go, moveTo, settle, size, speak]);

  // 首页叫它欢呼（「需要你看的」都看完了）
  useEffect(() => {
    const on = (e: Event) => {
      if ((e as CustomEvent<PetCue>).detail !== 'cheer' || state.current.mode === 'away') return;
      speak('cheer', '今天要看的都看完了', 4000);
    };
    window.addEventListener('htpet', on);
    return () => window.removeEventListener('htpet', on);
  }, [speak]);

  // 连着忙了两个小时（一直有鼠标键盘操作，中间没停过 10 分钟）：提醒歇一下，一天一次
  const nameRef = useRef(name);
  nameRef.current = name;
  useEffect(() => {
    let lastSeen = Date.now(), since = Date.now();
    const mark = () => { const t = Date.now(); if (t - lastSeen > BREAK_GAP) since = t; lastSeen = t; };
    window.addEventListener('pointermove', mark, { passive: true });
    window.addEventListener('keydown', mark);
    const timer = setInterval(() => {
      const t = Date.now();
      if (document.visibilityState !== 'visible' || t - lastSeen > 2 * 60_000 || t - since < WORK_LONG) return;
      if (!['walk', 'sit', 'peek', 'look'].includes(state.current.mode)) return;
      const day = new Date().toDateString();
      try { if (localStorage.getItem('htpet-break') === day) return; localStorage.setItem('htpet-break', day); } catch { /* 存不了就照常提醒 */ }
      since = t;
      speak('talk', '连着忙两个小时了，起来喝口水、走两步吧。', 10_000, nameRef.current || undefined);
    }, 60_000);
    return () => { window.removeEventListener('pointermove', mark); window.removeEventListener('keydown', mark); clearInterval(timer); };
  }, [speak]);

  // 18 点以后头一回打开：挥挥手（一天一次）
  useEffect(() => {
    const line = byeLine(new Date());
    if (!line) return;
    const key = 'htpet-bye';
    const day = new Date().toDateString();
    try { if (localStorage.getItem(key) === day) return; localStorage.setItem(key, day); } catch { /* 存不了就每次都挥 */ }
    const t = setTimeout(() => { if (state.current.mode !== 'away') speak('bye', line, 5000); }, 1500);
    return () => clearTimeout(t);
  }, [speak]);

  // 按天气穿衣服：天气到了、和身上穿的不一样，就停下来一件件穿上（穿好跳一下）；平常的衣服、减少动态效果的直接换
  const dressTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(dressTimer.current), []);
  useEffect(() => {
    if (!weather.data || outfit === worn || dressing) return;
    if (outfit === 'normal' || still) { setWorn(outfit); saveWorn(outfit); return; }
    if (busy || state.current.mode === 'away' || state.current.mode === 'jump') return;
    setSay(null); clearTimeout(back.current);
    go('dress');
    setWorn(outfit);
    setDressing(Date.now());
    clearTimeout(dressTimer.current);
    dressTimer.current = setTimeout(() => {
      setDressing(null);
      saveWorn(outfit);
      if (state.current.mode !== 'dress') return;   // 穿到一半去回答里干活了：衣服照样穿好，不用跳
      go('jump');
      setTimeout(() => {
        if (state.current.mode !== 'jump') return;
        go('sit');
        state.current.walkUntil = Date.now() + 8000;
        setTimeout(() => { if (state.current.mode === 'sit' && !still) go('walk'); }, 2500);
      }, 600);
    }, dress('sit', outfit).total * 1000 + 300);
  }, [weather.data, outfit, worn, dressing, busy, still, go]);

  // 发送：跳一下就离开，去回答里干活；答完跳回来坐下，歇会儿再溜达
  // 从 false 起：刚发完问题、底部输入框才出现时也要先跳一下再走
  const wasBusy = useRef(false);
  useEffect(() => {
    const prev = wasBusy.current;
    wasBusy.current = busy;
    let t: ReturnType<typeof setTimeout> | undefined;
    let t2: ReturnType<typeof setTimeout> | undefined;
    // 跳完之后该到的状态；还没跳完就被清理（开发环境效果跑两遍、或状态很快又变）时直接落到这个状态，不会卡在半空
    let pending: (() => void) | null = null;
    if (busy && !prev) {
      setSay(null); clearTimeout(back.current);
      go('jump');
      pending = () => go('away');
      t = setTimeout(() => { pending = null; go('away'); }, 650);
    } else if (!busy && prev) {
      go('jump');
      pending = () => {
        go('sit');
        state.current.walkUntil = Date.now() + 9000;
        t2 = setTimeout(() => { if (state.current.mode === 'sit' && !still) go('walk'); }, 2500);
      };
      t = setTimeout(() => { const land = pending; pending = null; land?.(); }, 600);
    }
    return () => { clearTimeout(t); if (pending) pending(); clearTimeout(t2); };
  }, [busy, go, still]);

  // 主循环：走路、换步、坐下歇会儿、犯困
  useEffect(() => {
    if (still) return;
    let raf = 0, last = performance.now(), acc = 0;
    const tick = (now: number) => {
      const dt = Math.min(0.1, (now - last) / 1000); last = now;
      const s = state.current;
      if ((s.mode === 'walk' || s.mode === 'sit') && Date.now() - s.lastInput > SLEEP_AFTER) go('sleepy');
      acc += dt;
      if (s.mode === 'walk') {
        const max = Math.max(8, s.width - size - 8);
        let nx = s.x + s.dir * SPEED * dt;
        if (nx >= max) { nx = max; s.dir = -1; }
        if (nx <= 8) { nx = 8; s.dir = 1; }
        s.x = nx; setSmooth(false); setX(nx);
        if (acc > 0.25) { acc = 0; setFrame((f) => 1 - f); }
        if (Date.now() > s.walkUntil) {
          go('sit');
          const rest = 4000 + Math.random() * 5000;
          setTimeout(() => { if (state.current.mode === 'sit') { state.current.walkUntil = Date.now() + 7000 + Math.random() * 8000; go('walk'); } }, rest);
        }
      } else if (s.mode === 'dizzy' && acc > 0.3) { acc = 0; setFrame((f) => 1 - f); }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [go, size, still, busy]);

  // 偶尔眨眼
  useEffect(() => {
    const t = setInterval(() => { setBlink(true); setTimeout(() => setBlink(false), 160); }, 3800);
    return () => clearInterval(t);
  }, []);

  // 点它：说句生活上的话，再点收起；1.5 秒里连着戳 5 下就晕了
  const poke = () => {
    const s = state.current;
    s.lastInput = Date.now();
    if (s.mode === 'away' || s.mode === 'jump' || s.mode === 'dress') return;
    s.pokes = [...s.pokes.filter((t) => Date.now() - t < 1500), Date.now()];
    if (s.pokes.length >= 5) { s.pokes = []; speak('dizzy', '别戳啦…头晕', 2200); return; }
    if (s.mode === 'dizzy') return;
    if (s.mode === 'talk' && s.pokes.length === 1) { settle(0); return; }
    if (s.mode !== 'talk') speak('talk', talkLine(new Date()), 6000, name || undefined);
  };

  // 减少动态效果：只换姿势不走动（回答时不在）
  const shown: Mode = still && (mode === 'walk' || mode === 'away') ? (busy ? 'away' : 'sit') : mode;
  const pose: Pose = shown === 'walk' ? (frame ? 'walk2' : 'walk1')
    : shown === 'sit' ? (blink ? 'blink' : 'sit')
      : shown === 'dress' ? 'sit'
      : shown === 'look' || shown === 'peek' ? 'look'
        : shown === 'jump' ? 'jump'
          : shown === 'sleepy' ? 'sleepy'
            : shown === 'talk' ? 'coffee'
              : shown === 'dizzy' ? (frame ? 'oops2' : 'oops1')
                : shown === 'bye' ? (blink ? 'blink' : 'idle')
                  : 'cheer';
  const icon: Icon | null = shown === 'dizzy' || shown === 'cheer' ? 'spark' : shown === 'bye' ? 'wave' : null;
  const seated = shown === 'sit' || shown === 'look' || shown === 'peek' || shown === 'sleepy' || shown === 'dress';
  // 穿好以后手里的东西：下雨撑伞、很热拿冰棍、零下呵白气（过节的道具优先）
  const extra = dressing || shown === 'away' ? null
    : worn === 'rain' ? 'umbrella' : worn === 'hot' && weather.data?.veryHot && !fest ? 'icepop' : worn === 'freezing' ? 'breath' : null;

  return (
    <div ref={ref} className={'pet' + (seated ? ' seated' : '') + (shown === 'jump' ? ' jumping' : '') + (shown === 'away' ? ' away' : '') + (smooth ? ' glide' : '')}
      style={{ left: x, ['--pet-w' as string]: `${size}px` }}>
      <button type="button" className="pet-btn" onClick={poke} title="点我一下" aria-label="和小人打个招呼">
        {worn === 'normal' ? <PixelSprite pose={pose} width={size} /> : <DressedSprite pose={pose} outfit={worn} width={size} dressing={dressing} />}
      </button>
      {extra === 'umbrella' && <PixelProp prop="umbrella" unit={unit * 1.5} className="pet-umb" />}
      {extra === 'icepop' && <PixelProp prop="icepop" unit={unit} className="pet-pop" />}
      {extra === 'breath' && <PixelProp prop="breath" unit={unit} className="pet-breath" />}
      {icon && <PixelIcon icon={icon} unit={unit} className={'pet-ic pet-ic-' + icon} />}
      {fest && shown !== 'away' && <PixelProp prop={fest.prop} unit={unit} className="pet-prop" />}
      {shown === 'sleepy' && <span className="pet-z" aria-hidden="true">z<b>Z</b></span>}
      {say && shown !== 'away' && (
        <div className={'pet-tip' + (tipLeft ? ' left' : '')} role="status">
          {say.label && <b>{say.label}</b>}
          <span>{say.text}</span>
        </div>
      )}
    </div>
  );
}
