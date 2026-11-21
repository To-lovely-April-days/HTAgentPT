---
name: pfd-pid
title: 工艺流程图与管道仪表图
category: 图纸与文档
status: enabled
source: own
description: 要画工艺流程图（PFD）或管道仪表图（PID），或把工艺描述整理成图时用。
---

# 工艺流程图与管道仪表图

## 先确认
- 要画哪一段：从哪里来、到哪里去；包括哪些设备
- 主要物流的温度、压力、流量（PFD 要）；管线、阀门、仪表（PID 要）

## 怎么画（只给结构化内容，图由系统按国标图例自动画、自动排版）
1. 工艺流程图用 show_block 的 pfd：units 设备（id 位号、type 类型、label 名称），streams 物流（from、to、温度、压力、流量、组成）。系统会核对物料衡算
2. 管道仪表图用 show_block 的 pid：
   - equipment：tag 位号、type 类型（reactor、vessel、column、tank、exchanger、heater、cooler、pump……）、label 名称
   - lines：id 管线号（介质代号-序号-公称直径，如 P-1001-50）、from、to、size、valves（type、tag）
   - 一头没接设备（来自上游、去火炬）的，留空那一头，用 label 写去向
   - instruments：tag、on（装在哪台设备或哪条管线）、location（field 就地、panel 盘面、dcs）、service、range、signal_to（控制的阀）
   - 可选 project 项目名、drawing_no 图号，会写进标题栏
3. 设备位号习惯：R 反应器、V 容器、T 塔、E 换热器、P 泵、C 压缩机、F 过滤器
4. 安全阀写在对应管线的 valves 里，type 用 safety

## 常见的坑
- 位号前后不一致（设备、管线、仪表互相引用对不上）
- 调节阀要有对应的控制仪表（signal_to 指向它）
- 图只是示意，正式图纸以设计出图为准

## 该查什么
show_block（pfd、pid）· query_projects、search_documents（公司以往项目的流程和图纸）

## 回答怎么写
先用一两句话说明流程 → show_block 出图（下面自动带仪表清单，可下载 draw.io、DXF、PDF）→ 说明做了哪些假设 → 提醒「示意图，正式图纸以设计出图为准」
