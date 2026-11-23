// 输入框「＋」菜单、项目页共用的类型和小工具。

/** 资料范围：二选一——限定一个知识库，或限定一类文档；都空就是全部。 */
export interface Scope { kbId: string | null; docCategory: string | null }
export interface ScopeOptions { kbs: { id: string; name: string }[]; cats: string[] }
export interface SkillRow { code: string; name: string; category: string; description: string }
/** 「＋ → 插件」里的一个开关：联网搜索或一组专用工具。 */
export interface PluginRow { code: string; label: string; summary: string }
export interface ProjectRow { id: string; name: string; ledgerNo: string | null; updatedAt: string; count: number }

export function scopeLabel(v: Scope, o: ScopeOptions): string | null {
  if (v.kbId) return o.kbs.find((k) => k.id === v.kbId)?.name ?? '某个知识库';
  return v.docCategory;
}

/** 项目的小方块颜色：按 id 固定挑一个，不用存。 */
const PJ_COLORS = ['#8A2E08', '#1D4A94', '#5C5C64', '#1C6B45', '#6B3FA0', '#9A6A00'];
export function projectColor(id: string): string {
  let h = 0;
  for (const c of id) h = (h * 31 + c.charCodeAt(0)) >>> 0;
  return PJ_COLORS[h % PJ_COLORS.length];
}
