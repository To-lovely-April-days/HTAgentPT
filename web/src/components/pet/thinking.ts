// 助手思考时小人做什么：由系统正在做的事决定（调用哪个工具、有没有出字），不让模型自己演。
import type { AssistantStep } from '../../lib/types';
import type { Icon, Pose } from './sprite';

export type Act = 'scratch' | 'ponder' | 'read' | 'mag' | 'calc' | 'web' | 'oops' | 'idea' | 'write' | 'ask';

export interface ActDef { frames: [Pose, Pose]; speed: number; icon?: Icon; say: string }

export const ACTS: Record<Act, ActDef> = {
  scratch: { frames: ['scratch1', 'scratch2'], speed: 0.45, icon: 'q', say: '嗯…让我想想' },
  ponder: { frames: ['think', 'think'], speed: 0.5, icon: 'dots', say: '这个有点绕，再想想' },
  read: { frames: ['read1', 'read2'], speed: 0.6, say: '翻翻公司资料' },
  mag: { frames: ['mag1', 'mag2'], speed: 0.7, say: '查一下' },
  calc: { frames: ['calc1', 'calc2'], speed: 0.28, say: '算一算' },
  web: { frames: ['web1', 'web2'], speed: 0.5, icon: 'wave', say: '上网搜搜' },
  oops: { frames: ['oops1', 'oops2'], speed: 0.4, icon: 'sweat', say: '没找到，换个说法再查' },
  idea: { frames: ['idea', 'idea'], speed: 0.5, icon: 'bulb', say: '有了！' },
  write: { frames: ['write1', 'write2'], speed: 0.35, say: '边想边写' },
  ask: { frames: ['think', 'think'], speed: 0.5, icon: 'q', say: '这里得你定一下' },
};

const TOOL_ACT: Record<string, Act> = {
  search_documents: 'read', read_document: 'read', search_standards: 'read', search_literature: 'read',
  read_attachments: 'read', list_catalog: 'read', translate_file: 'read',
  search_prices: 'mag', query_projects: 'mag', view_image: 'mag', ocr_image: 'mag', compare_documents: 'mag',
  calculate: 'calc', convert_unit: 'calc', run_python: 'calc', cost_estimate: 'calc',
  web_search: 'web', fetch_url: 'web', chemical_info: 'web',
  ask_user: 'ask', inquiry_form: 'ask',
  use_skill: 'read',
};

/** 查价格、查项目这类「查一下」后面带上具体查什么，读起来更像人话。 */
const MAG_SAY: Record<string, string> = {
  search_prices: '查一下价格表', query_projects: '翻翻项目台账', view_image: '凑近看看这张图', ocr_image: '认认上面的字', compare_documents: '两份对着看',
};

export const HOLD_MS = 1500;      // 一个动作至少停这么久，工具切得太快时不来回闪
export const IDEA_MS = 1200;      // 第一个字出来时「有了！」停多久
export const PONDER_AFTER = 6000; // 想了这么久还没动静就改成托腮
export const OOPS_MS = 2200;      // 一步没查到，冒汗多久

export interface Live { steps: AssistantStep[]; hasText: boolean; startedAt: number; firstTextAt: number | null; quietSince: number }

// 「0 个项目」「没有相关规范」「没读到内容」「代号不对」这类结果都算没查到
const looksEmpty = (s: AssistantStep) => s.status === 'error' || /^(0\s|没|无|空)|不对/.test(s.result ?? '');

/** 现在该做的动作，以及对话框里的那句话和关键词。 */
export function pickAct(live: Live, now: number): { act: Act; say: string; sub: string } {
  if (live.hasText) {
    const act: Act = live.firstTextAt != null && now - live.firstTextAt < IDEA_MS ? 'idea' : 'write';
    return { act, say: ACTS[act].say, sub: '' };
  }
  const steps = live.steps;
  const running = [...steps].reverse().find((s) => s.status === 'running');
  if (running) {
    const act = TOOL_ACT[running.tool] ?? 'read';
    const say = act === 'mag' ? MAG_SAY[running.tool] ?? ACTS.mag.say
      : running.tool === 'read_document' ? '打开原文看看' : running.tool === 'use_skill' ? '翻翻经验' : ACTS[act].say;
    return { act, say, sub: short(running.detail) };
  }
  const last = steps[steps.length - 1];
  if (last && looksEmpty(last) && now - live.quietSince < OOPS_MS) return { act: 'oops', say: ACTS.oops.say, sub: '' };
  const act: Act = now - live.quietSince < PONDER_AFTER ? 'scratch' : 'ponder';
  return { act, say: ACTS[act].say, sub: '' };
}

function short(s: string | null | undefined) {
  const t = (s ?? '').replace(/\s+/g, ' ').trim();
  return t.length > 28 ? t.slice(0, 28) + '…' : t;
}
