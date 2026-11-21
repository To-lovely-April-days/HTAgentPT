---
name: chemistry-reactions
title: 化学与反应
category: 安全与化学
status: enabled
source: adapted
description: 问化学反应方程式、配平、分子结构、反应热、物料配比和化学计量时用。
---

# 化学与反应

## 先确认
- 反应物、产物（名称、分子式或结构）
- 投料量、目标产量；反应条件（温度、压力、催化剂）

## 怎么做
1. 方程式用 show_block 的 reaction（反应物、产物、系数、条件），系统会核对是否配平
2. 分子结构用 show_block 的 molecule（给 SMILES），分子式和分子量由系统算
3. 物料配比、理论产量用 calculate 或 run_python 算，写清过量的是哪个反应物、限量的是哪个
4. 反应热：用标准生成焓估算 ΔH = Σ产物 − Σ反应物。run_python 里 thermo 库的 Chemical('名称').Hfgm 给气相标准生成焓（J/mol），液相要考虑汽化热；结果写明是估算
5. 物质的危险性用 chemical_info 查；放热明显的反应提醒做热安全评估

## 常见的坑
- 方程式没配平就算物料
- 反应热用了不同相态的数据
- 只看理论产量，不说实际收率

## 该查什么
show_block（reaction、molecule）· calculate · run_python（thermo）· chemical_info · search_literature（文献）

## 回答怎么写
结论 → reaction 或 molecule 组件 → 计算过程（formula）→ 数据来源 → 安全提醒（放热、气体生成）
