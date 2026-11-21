---
name: solution-compare
title: 方案和竞品对比
category: 售前与客户
status: enabled
source: own
description: 客户在几个方案或几家供应商之间比较，要列优缺点、给推荐时用。
---

# 方案和竞品对比

## 先确认
- 要比的是哪几个方案（我方不同配置，还是我方和别家）
- 客户最看重什么：价格、交期、安全、维护、性能
- 比较的口径：同一工况、同一范围（含不含安装、备件）

## 怎么做
1. 统一对比维度：技术满足度、安全性、可靠性和维护、能耗、材料和成本、交期、业绩（公司同类项目）
2. 每一项写事实和出处：我方数据用 search_documents、query_projects 查；成本用 search_prices
3. 用 show_table 的 compare 表：一行一个指标，一列一个方案，highlight 填推荐方案的列号，格子开头写 ✓ ! ✕
4. 给推荐，写清理由和前提（什么情况下另一个方案更合适）

## 常见的坑
- 别贬低竞品，只说可以核实的事实；对方的参数没有出处就写「未核实」
- 只比价格不比范围，结论会错
- 推荐理由要对着客户最看重的那一项

## 该查什么
search_documents · query_projects · search_prices · web_search（竞品公开资料，标明来源，提醒核实）

## 回答怎么写
结论（推荐哪个、一句理由）→ show_table 对比表 → 各方案的适用场景 → 需要客户确认的前提
