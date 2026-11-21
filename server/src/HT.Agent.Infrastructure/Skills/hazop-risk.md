---
name: hazop-risk
title: HAZOP 与风险分析
category: 安全与化学
status: enabled
source: own
description: 要做 HAZOP 分析初稿、风险矩阵，或问风险评估怎么做时用。
---

# HAZOP 与风险分析

## 先确认
- 工艺和设备：最好有管道仪表图或流程描述
- 介质的危险性（用 chemical_info 查）、操作条件

## 怎么做（参照 AQ/T 3049 危险与可操作性分析导则）
1. 划分节点：按设备或管段划分，如进料管线、反应釜、冷凝系统、尾气系统
2. 每个节点用「引导词 + 参数」找偏差：引导词有无、多、少、反向、部分、伴随、异常、早、晚；参数有流量、压力、温度、液位、组成、时间
3. 每条偏差写：原因、后果、现有保护措施、建议措施
4. 风险用 show_block 的 risk_matrix：可能性 1–5、严重程度 1–5，高风险的必须有建议措施
5. 重点关注：反应失控、超压、泄漏、氢气和易燃物、冷却失效、误操作

## 常见的坑
- 偏差的原因只写「操作失误」，要写到具体哪一步
- 保护措施要是独立有效的（同一个测点不能既控制又联锁）
- 初稿只是准备材料，结论以正式 HAZOP 会议为准

## 该查什么
chemical_info · search_documents（公司以往 HAZOP 报告、事故案例）· show_block（risk_matrix）· show_table

## 回答怎么写
说明分析范围和节点 → show_table 的 HAZOP 记录表（节点、引导词、偏差、原因、后果、现有措施、建议）→ show_block 的 risk_matrix → 提醒「初稿，以正式 HAZOP 会议结论为准」
