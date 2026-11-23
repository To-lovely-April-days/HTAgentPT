---
name: drawings-charts
title: 示意图与图表
category: 图纸与文档
menu: hidden
status: enabled
source: own
description: 要画设备外形示意图、接管方位、流程示意、数据曲线、方案对比图时用（画在回答里，可下载）。
---

# 示意图与图表

## 先确认
- 画什么：设备外形尺寸、接管方位、工艺流程、管道仪表、数据曲线、对比图
- 尺寸和参数从哪来：用户给的、产品资料里的、台账里同类项目的；没有的才按常规比例估
- 给谁看：内部讨论、给客户的方案附图

## 怎么做
1. 设备外形和接管：show_block 的 equipment（直径、切线长、封头、接管位置和用途），接管表跟图一起出
2. 工艺流程：show_block 的 pfd；带阀门仪表的：pid（按「工艺流程图与管道仪表图」技能）
3. 简单流程、步骤关系：show_block 的 flow
4. 数据曲线、对比：数据已有就用 show_block 的 chart；要先算的（升温曲线、压降曲线、溶解度曲线）用 run_python 算完用 matplotlib 画
5. 要正式交付的图（放进方案、发给客户）：用 run_python 画好存成 PDF 或 PNG 交付
6. 尺寸查不到就写「按常规比例估」，不要编具体数；改图时说「把 N3 换到 90°」「加一个测温口」就重画一版

## 常见的坑
- 编造没有依据的尺寸、接管规格
- 图上的位号、接管号和文字、表格对不上
- 图表的坐标轴没写单位，数据没写来源

## 不做
不用 AI 生成效果图、渲染图：画不准结构和尺寸。宣传用的效果图请设计人员做。

## 该查什么
show_block（equipment、pfd、pid、flow、chart）· run_python（matplotlib）· query_projects、search_documents（同类产品的尺寸、接管）

## 回答怎么写
一句话说明画的是什么 → 图（组件）→ 尺寸和参数的来源，哪些是估的 → 提醒「示意图，正式图纸以设计出图为准」
