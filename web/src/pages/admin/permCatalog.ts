// 功能权限的中文说法和分组。键与服务端 PermissionKeys 一一对应；界面上不出现英文代码。
import type { Classification } from '../../lib/types';

export interface PermItem { key: string; label: string; desc?: string; admin?: boolean }
export interface PermGroup { title: string; items: PermItem[] }

export const PERM_GROUPS: PermGroup[] = [
  { title: '问答与查询', items: [
    { key: 'qa.internal', label: '内部资料问答', desc: '问公司内部资料' },
    { key: 'qa.public', label: '公开资料问答', desc: '问对外公开的资料' },
    { key: 'project.search', label: '查项目台账', desc: '做过的项目；金额要「机密」密级才看得到' },
  ] },
  { title: '资料', items: [
    { key: 'corpus.upload', label: '上传文件', desc: '经部门主管审核后入库' },
    { key: 'corpus.manage', label: '语料管理', desc: '上传、审核、解析全部资料', admin: true },
    { key: 'kb.manage', label: '知识库管理', desc: '建库、发布到公开库', admin: true },
    { key: 'meta.manage', label: '元数据与词表', desc: '客户名单、设备类型、台账', admin: true },
  ] },
  { title: '文档与翻译', items: [
    { key: 'generate.doc', label: '出文档', desc: '按模板生成技术协议等（暂停中）' },
    { key: 'generate.contract', label: '报价合同', desc: '组装合同条款' },
    { key: 'translate', label: '翻译', desc: '整篇或段落中英互译' },
    { key: 'template.manage', label: '模板管理', desc: '上传、启停模板', admin: true },
  ] },
  { title: '案例与工单', items: [
    { key: 'case.read', label: '看故障案例' },
    { key: 'case.write', label: '录入案例', desc: '新增故障处理经验' },
    { key: 'case.review', label: '审定案例' },
    { key: 'ticket.handle', label: '处理报修工单' },
  ] },
  { title: '系统', items: [
    { key: 'user.manage', label: '用户与权限', desc: '账号、角色、组织架构', admin: true },
    { key: 'system.config', label: '系统设置', desc: '模型、检索、备份', admin: true },
    { key: 'audit.read', label: '审计与统计', desc: '操作日志、使用统计', admin: true },
  ] },
  { title: '客户门户', items: [
    { key: 'customer.self', label: '客户自助', desc: '外部客户看自己的设备、报修' },
  ] },
];

const LABELS = new Map(PERM_GROUPS.flatMap((g) => g.items.map((i) => [i.key, i.label] as const)));
export const permLabel = (key: string) => LABELS.get(key) ?? key;

export const CLS_ORDER: Classification[] = ['Confidential', 'Internal', 'Public'];
export const CLS_DESC: Record<Classification, string> = { Confidential: '合同金额、报价', Internal: '公司内部资料', Public: '可对外的资料' };
export const CLS_TAG: Record<Classification, [string, string, string]> = {
  Confidential: ['机密', '#fde8ea', '#a00d25'], Internal: ['内部', '#fbf0d9', '#8a5a00'], Public: ['公开', '#e6f2ec', '#1c6b45'],
};
