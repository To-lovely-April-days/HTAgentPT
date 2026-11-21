// 住在输入框上沿的 8-bit 小人。放在输入框（position: relative）里面，自己找同一个框里的文本框来听打字。
// 空闲时沿上沿走来走去、走累了坐下；打字时走到光标上方低头看；发送时跳一下；等回答时坐在右端抱着笔记本；
// 很久没人动就打瞌睡；点它一下举手，冒出一条今日知识。系统设置了「减少动态效果」的人，它只坐着不走。
import { useCallback, useEffect, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get } from '../../lib/api';
import { PixelSprite } from './PixelSprite';
import type { Pose } from './sprite';

type Mode = 'walk' | 'sit' | 'look' | 'jump' | 'busy' | 'sleepy' | 'cheer';

interface Knowledge { title: string; body: string }
interface Today { fun?: { knowledge?: Knowledge | null } }

const SPEED = 26;          // 像素 / 秒
const SLEEP_AFTER = 120_000;

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
  const [mode, setMode] = useState<Mode>('walk');
  const [x, setX] = useState(24);
  const [frame, setFrame] = useState(0);
  const [blink, setBlink] = useState(false);
  const [smooth, setSmooth] = useState(false);
  const state = useRef({ mode: 'walk' as Mode, x: 24, dir: 1, width: 600, lastInput: 0, walkUntil: 0 });
  const [tipLeft, setTipLeft] = useState(false);
  const today = useQuery({ queryKey: ['home-today'], queryFn: () => get<Today>('/api/home/today'), staleTime: 30 * 60_000, retry: false });
  const fact = today.data?.fun?.knowledge ?? null;
  const still = reduced();

  const go = useCallback((m: Mode) => { state.current.mode = m; setMode(m); }, []);
  const moveTo = useCallback((nx: number, glide: boolean) => {
    const max = Math.max(8, state.current.width - size - 8);
    const v = Math.max(8, Math.min(max, nx));
    state.current.x = v; setSmooth(glide); setX(v);
  }, [size]);

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
      if (state.current.mode === 'busy' || state.current.mode === 'jump') return;
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

  // 有人动就醒
  useEffect(() => {
    const wake = () => {
      state.current.lastInput = Date.now();
      if (state.current.mode === 'sleepy') go('sit');
    };
    window.addEventListener('pointermove', wake, { passive: true });
    window.addEventListener('keydown', wake);
    return () => { window.removeEventListener('pointermove', wake); window.removeEventListener('keydown', wake); };
  }, [go]);

  // 发送：跳一下，然后坐到右端等回答
  // 从 false 起：刚发完问题、底部输入框才出现时也要跳一下再去等
  const wasBusy = useRef(false);
  useEffect(() => {
    const prev = wasBusy.current;
    wasBusy.current = busy;
    let t: ReturnType<typeof setTimeout> | undefined;
    if (busy && !prev) {
      go('jump');
      t = setTimeout(() => { go('busy'); moveTo(state.current.width - size - 110, true); }, 650);
    } else if (!busy && prev) {
      go('sit');
      state.current.walkUntil = Date.now() + 9000;
      t = setTimeout(() => { if (state.current.mode === 'sit' && !still) go('walk'); }, 2500);
    }
    // 清理时把记录退回去：开发环境下效果会跑两遍，退回后第二遍照样能接上
    return () => { clearTimeout(t); wasBusy.current = prev; };
  }, [busy, go, moveTo, size, still]);

  // 主循环：走路、换步、坐下歇会儿、犯困
  useEffect(() => {
    if (still) return;
    let raf = 0, last = performance.now(), acc = 0;
    const tick = (now: number) => {
      const dt = Math.min(0.1, (now - last) / 1000); last = now;
      const s = state.current;
      if (s.mode !== 'busy' && s.mode !== 'jump' && s.mode !== 'cheer' && Date.now() - s.lastInput > SLEEP_AFTER) {
        if (s.mode !== 'sleepy') go('sleepy');
      }
      if (s.mode === 'walk') {
        const max = Math.max(8, s.width - size - 8);
        let nx = s.x + s.dir * SPEED * dt;
        if (nx >= max) { nx = max; s.dir = -1; }
        if (nx <= 8) { nx = 8; s.dir = 1; }
        s.x = nx; setSmooth(false); setX(nx);
        acc += dt;
        if (acc > 0.25) { acc = 0; setFrame((f) => 1 - f); }
        if (Date.now() > s.walkUntil) {
          go('sit');
          const rest = 4000 + Math.random() * 5000;
          setTimeout(() => { if (state.current.mode === 'sit') { state.current.walkUntil = Date.now() + 7000 + Math.random() * 8000; go('walk'); } }, rest);
        }
      }
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

  // 点它：举手 + 今日知识，8 秒后自己收起
  useEffect(() => {
    if (mode !== 'cheer') return;
    const t = setTimeout(() => go('sit'), 8000);
    return () => clearTimeout(t);
  }, [mode, go]);
  const poke = () => {
    state.current.lastInput = Date.now();
    if (state.current.mode === 'busy') return;
    setTipLeft(state.current.x > state.current.width / 2);
    go(state.current.mode === 'cheer' ? 'sit' : 'cheer');
  };

  // 减少动态效果：只坐着（等回答时抱笔记本），点它照样能看今日知识
  const shown: Mode = still && mode !== 'cheer' ? (busy ? 'busy' : 'sit') : mode;
  const pose: Pose = shown === 'walk' ? (frame ? 'walk2' : 'walk1')
    : shown === 'sit' ? (blink ? 'blink' : 'sit')
      : shown === 'look' ? 'look'
        : shown === 'jump' ? 'jump'
          : shown === 'busy' ? 'busy'
            : shown === 'sleepy' ? 'sleepy'
              : 'cheer';
  const seated = shown === 'sit' || shown === 'look' || shown === 'busy' || shown === 'sleepy';

  return (
    <div ref={ref} className={'pet' + (seated ? ' seated' : '') + (shown === 'jump' ? ' jumping' : '') + (smooth ? ' glide' : '')}
      style={{ left: x, ['--pet-w' as string]: `${size}px` }}>
      <button type="button" className="pet-btn" onClick={poke} title={mode === 'cheer' ? '收起' : '点我看今日知识'}
        aria-label={mode === 'cheer' ? '收起今日知识' : '看今日知识'}>
        <PixelSprite pose={pose} width={size} />
      </button>
      {shown === 'sleepy' && <span className="pet-z" aria-hidden="true">z<b>Z</b></span>}
      {shown === 'busy' && <span className="pet-busy">正在翻资料<i>▍</i></span>}
      {mode === 'cheer' && (
        <div className={'pet-tip' + (tipLeft ? ' left' : '')} role="status">
          <b>今日知识</b>
          {fact ? <><span className="pet-tip-t">{fact.title}</span><span>{fact.body}</span></> : <span>今天的知识还在路上，等会儿再点我。</span>}
        </div>
      )}
    </div>
  );
}
