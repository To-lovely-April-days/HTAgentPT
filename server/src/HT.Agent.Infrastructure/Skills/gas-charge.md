---
name: gas-charge
title: 高压釜装气量
category: 设计与标准
status: enabled
source: own
description: 问釜里充多少气、充到多少压力、升温或降温后压力是多少、超不超设计压力时用（氢气、氮气、CO2、氧气等）。
---

# 高压釜装气量

## 先确认
- 釜的容积；里面有没有液体，有的话液体多少（只按气相空间算）
- 什么气体、充气时的温度和压力；问的是升温后、降温后，还是反算最多能充多少
- 压力是表压还是绝压（客户说的一般是表压，计算要换成绝压）
- 设计压力、设计温度（用户没说就问，或从台账、产品资料里查）

## 怎么做
1. 用 run_python 和 CoolProp 按实际气体算，不按理想气体（高压下差 5% 以上）
2. 定容升温或降温：气体量不变，密度不变，用新温度和这个密度求压力
3. 同时算出理想气体的结果对照，写出压缩因子 Z
4. 升温后压力超过设计压力就明确说超了，并反算两种办法：常温最多充到多少；或者要多高设计压力的釜
5. 有液体时提醒：溶剂蒸气压、气体溶解都会让实际压力和计算值有偏差，结果只按气相估算

计算模板（压力用绝压，单位 Pa、K、m3）：
```python
from CoolProp.CoolProp import PropsSI
gas, V = 'Hydrogen', 0.030              # 气相空间 30 L
Pa = 101325
T1, P1 = 25+273.15, 10e6 + Pa           # 充气：25 ℃、10 MPa(G)
T2, Pd = 250+273.15, 15e6 + Pa          # 升到 250 ℃；设计压力 15 MPa(G)
M = PropsSI('M', gas)                   # kg/mol
rho = PropsSI('D', 'T', T1, 'P', P1, gas)
n, Z1 = rho*V/M, P1*M/(rho*8.314462*T1)
P2 = PropsSI('P', 'T', T2, 'D', rho, gas)
n_ideal = P1*V/(8.314462*T1)
print(f"装气 {n:.1f} mol（{n*M*1000:.0f} g），Z={Z1:.3f}；理想气体 {n_ideal:.1f} mol")
print(f"升到 {T2-273.15:.0f} ℃：{(P2-Pa)/1e6:.2f} MPa(G)，理想气体 {(n_ideal*8.314462*T2/V-Pa)/1e6:.2f} MPa(G)")
if P2 > Pd:
    rho_max = PropsSI('D', 'T', T2, 'P', Pd, gas)
    print(f"超过设计压力；常温最多充到 {(PropsSI('P','T',T1,'D',rho_max,gas)-Pa)/1e6:.2f} MPa(G)")
```
CoolProp 的气体名：Hydrogen、Nitrogen、Oxygen、CarbonDioxide、Methane、Ammonia、Air、Argon、Helium、CarbonMonoxide、Ethylene。

## 常见的坑
- 表压绝压混用；结果要写清是 MPa(G) 还是 MPa(A)
- 按全容积算，忽略了液体和内件占的体积
- 氧气、氢气之外的混合气要按组分算，或者说明只能估算

## 该查什么
run_python · query_projects、search_documents（这台釜的设计压力、设计温度）· convert_unit

## 回答怎么写
结论（装了多少、升温后多大压力、超没超）→ show_block 的 metrics 列关键数（实际气体和理想气体对照）→ 超压时的两种办法 → 说明假设（气相空间、温度均匀）
