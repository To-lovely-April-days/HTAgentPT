---
name: reaction-equilibrium
title: 反应平衡
category: 安全与化学
status: enabled
source: own
description: 问某个反应在给定温度压力下最多能转化多少、平衡组成、温度压力怎么影响平衡时用（加氢、脱氢、合成气、氧化等气相反应）。
---

# 反应平衡

## 先确认
- 反应物、产物（写分子式）；进料配比
- 温度范围、压力
- 用途：判断转化率给得合不合理（比如流程模拟里的转化率），还是选操作条件

## 怎么做
1. 用 run_python 的 cantera 算化学平衡；热力学数据用 Cantera 自带的 NASA 数据库
2. 物质名按分子式找，同分异构体带后缀（如 C6H12,cyclo- 是环己烷，C6H12,1-hexene 是 1-己烯）；找不到就说缺数据，不要换别的物质顶替
3. 只放参与反应的物质；多放了会算出意料外的副产物
4. 算几个温度点，说明温度、压力的影响

计算模板：
```python
import cantera as ct
allsp = {s.name: s for s in ct.Species.list_from_file('nasa_gas.yaml')}
print([n for n in allsp if n.startswith('C6H12')])          # 先看名字
gas = ct.Solution(thermo='ideal-gas', species=[allsp[n] for n in ['H2', 'C6H6', 'C6H12,cyclo-']])
for T in (180, 250, 300, 350):
    gas.TPX = T + 273.15, 3.1e6, {'H2': 3.5, 'C6H6': 1}
    gas.equilibrate('TP')
    x = dict(zip(gas.species_names, gas.X))
    print(T, '℃ 平衡转化率', round(x['C6H12,cyclo-'] / (x['C6H6'] + x['C6H12,cyclo-']) * 100, 3), '%')
```

## 常见的坑
- 平衡转化率当成实际转化率（实际还受催化剂、停留时间限制，只会更低）
- 高压下按理想气体算是近似，压力越高偏差越大，要写明
- 液相反应、溶液里的平衡不能直接用这个模板
- 流程模拟里给的转化率超过平衡值

## 该查什么
run_python（cantera）· chemical_info · search_literature（实验数据、动力学）

## 回答怎么写
结论（这个条件下最多能转化多少）→ 不同温度的对比表或曲线 → 对操作条件的建议 → 说明数据来源（NASA 热力学数据）和假设（理想气体）
