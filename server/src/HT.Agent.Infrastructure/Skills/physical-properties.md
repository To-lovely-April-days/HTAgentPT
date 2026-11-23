---
name: physical-properties
title: 物性查询
category: 设计与标准
menu: hidden
status: enabled
source: library
description: 问某种物质在某温度、压力下的密度、粘度、比热、导热系数、蒸气压、沸点等物性时用。
---

# 物性查询

## 先确认
- 物质名称或 CAS 号；纯物质还是混合物（组成）
- 温度、压力；是液相还是气相

## 怎么查
1. 常见纯物质（水、氮气、氢气、二氧化碳、制冷剂、常见烃类）用 CoolProp：精度高，适用范围宽
2. 其他化学品用 thermo 库的 Chemical，混合物用 Mixture（估算值，要写明）
3. 安全和危险性数据（闪点、爆炸极限、毒性）用 chemical_info
4. 公司资料或手册里有实测数据的，优先用实测，并写出处

计算模板（run_python，温度 K、压力 Pa）：
```python
from CoolProp.CoolProp import PropsSI
from thermo import Chemical, Mixture
T, P = 273.15+80, 3e5                      # K、Pa（绝压）
print(f"水 80 ℃：密度 {PropsSI('D','T',T,'P',P,'Water'):.1f} kg/m³，粘度 {PropsSI('V','T',T,'P',P,'Water')*1000:.3f} mPa·s，比热 {PropsSI('C','T',T,'P',P,'Water'):.0f} J/(kg·K)")
e = Chemical('ethanol', T=298.15, P=101325)
print(f"乙醇 25 ℃：密度 {e.rho:.1f} kg/m³，粘度 {e.mu*1000:.3f} mPa·s，饱和蒸气压 {e.Psat/1000:.2f} kPa，沸点 {e.Tb-273.15:.1f} ℃")
m = Mixture(['water', 'ethanol'], ws=[0.5, 0.5], T=298.15, P=101325)
print(f"50% 乙醇水溶液密度（估算）{m.rho:.1f} kg/m³")
```

## 常见的坑
- 压力写成表压会算错，CoolProp、thermo 都用绝压
- 混合物的物性是模型估算，非理想体系误差大
- 超出数据适用范围的结果不可靠，要说明

## 该查什么
run_python · chemical_info · search_documents（实测数据、物性手册）

## 回答怎么写
结论（物性数值和单位）→ show_block 的 properties 物性表（每项写数据来源：CoolProp、thermo 估算或资料）→ 适用条件和误差提醒
