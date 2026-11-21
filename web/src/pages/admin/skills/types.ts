// 后台「技能管理」用到的数据形状和文案。

export type SkillStatus = 'Draft' | 'Review' | 'Enabled';

export interface SkillRow {
  id: string; code: string; name: string; category: string; description: string; source: string; status: SkillStatus;
  version: number; updatedAt: string; roles: string[]; usedWeek: number; notHelpfulWeek: number;
}
export interface SkillList { answersWeek: number; answersWithSkillWeek: number; items: SkillRow[] }

export interface SkillDetail extends Omit<SkillRow, 'usedWeek' | 'notHelpfulWeek'> {
  body: string; license: string | null; reviewedAt: string | null; reviewer: string | null;
  refDocs: { id: string; title: string }[]; usedWeek: number; notHelpfulWeek: number;
  length: number; unknownTools: string[];
  versions: { version: number; note: string | null; editedAt: string; editor: string | null }[];
}

export interface SkillMeta { categories: string[]; tools: { code: string; label: string }[]; maxBody: number; licenses: string[] }

export interface ImportChecks {
  name: string; code: string; description: string; license: string | null; licenseOk: boolean;
  length: number; maxBody: number; unknownTools: string[]; scripts: string[]; references: string[]; exists: boolean;
}

export const STATUS: Record<SkillStatus, { label: string; cls: string }> = {
  Enabled: { label: '已启用', cls: 'sk-ok' }, Review: { label: '待审核', cls: 'sk-pend' }, Draft: { label: '草稿', cls: 'sk-draft' },
};
export const SOURCE: Record<string, string> = { own: '自写', adapted: '改编开源', library: '开源库' };

/** 和服务端 SkillFiles 同一个口径：字数不算空白。 */
export const bodyLength = (s: string) => s.replace(/\s+/g, '').length;

/** 正文里像工具名、但助手没有的词（和服务端 SkillFiles.UnknownTools 一致）。 */
export function unknownTools(body: string, known: string[]): string[] {
  const ok = new Set([...known, 'fault_tree', 'risk_matrix', 'signal_to', 'drawing_no']);
  body = body.replace(/```[\s\S]*?```/g, '');   // 代码块里的函数名、变量名不算
  const words = [...body.matchAll(/(?<![\w./-])([a-z]+(?:_[a-z]+)+)(?![\w./-])/g)].map((m) => m[1])
    .concat([...body.matchAll(/`(bash|python|web_fetch|websearch|read|write|edit|grep|glob)`/g)].map((m) => m[1]));
  return [...new Set(words)].filter((w) => !ok.has(w));
}

export function shortDate(iso: string): string {
  const d = new Date(iso);
  return `${d.getMonth() + 1}-${String(d.getDate()).padStart(2, '0')} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}
