---
name: pressure-relief
title: 安全泄放
category: 设计与标准
menu: hidden
status: enabled
source: library
description: 问安全阀、爆破片怎么选，泄放量和泄放面积怎么算时用。
---

# 安全泄放

## 先确认
- 被保护设备的设计压力、设计温度；介质（气体、液体还是蒸汽，分子量、绝热指数）
- 泄放工况和泄放量：超压的原因（加热失控、反应失控、外部火灾、进料阀全开……）
- 背压：出口排到大气还是火炬

## 怎么做
1. 整定压力不高于设计压力；泄放时允许的超压一般按 10%（外部火灾工况 21%），以设计标准为准
2. 泄放量先按最危险工况算；反应失控的泄放量要专门评估，不能只按进料量估
3. 面积用 fluids 库的 API 520 公式算（run_python），再圆整到 API 526 标准口径。国内设计按 GB/T 150 附录 B，公式思路一致，这里的结果用于估算和对比
4. 爆破片：介质有毒、易聚合堵塞、要求零泄漏时，可以用爆破片，或爆破片和安全阀串联
5. 安全阀型号、口径的公司选用习惯用 search_documents 查以往项目

计算模板（单位：Pa、K、kg/s，压力为绝压）：
```python
from fluids.safety_valve import API520_A_g, API520_A_l, API520_A_steam, API520_round_size, API526_letters, API526_A
# 气体：氢气 1000 kg/h，泄放温度 300 K，泄放压力 = 整定压力 1.0 MPa(G) × 1.1 + 大气压
P1 = 1.0e6*1.1 + 101325
A = API520_A_g(m=1000/3600, T=300, Z=1.0, MW=2.016, k=1.41, P1=P1, P2=101325, Kd=0.975, Kb=1, Kc=1)
std = API520_round_size(A)
print(f"所需面积 {A*1e6:.0f} mm²，选 API 526 的 {API526_letters[API526_A.index(std)]} 口径（{std*1e6:.0f} mm²）")
# 液体：2 kg/s 水；蒸汽：0.5 kg/s、200 ℃、1.2 MPa(a)
print(f"液体 {API520_A_l(m=2.0, rho=998.2, P1=1.1e6, P2=101325, overpressure=0.1, Kd=0.65, mu=1e-3)*1e6:.0f} mm²")
print(f"蒸汽 {API520_A_steam(m=0.5, T=473.15, P1=1.2e6)*1e6:.0f} mm²")
```

## 常见的坑
- 两相流（如反应失控）不能用单相公式，要专门计算
- 背压高时要选背压平衡式，或用修正系数 Kb
- 安全阀进出口管道压降要核算，出口要引到安全处
- 计算结果只是估算，最终以有资质单位的计算为准

## 该查什么
run_python · search_standards（GB/T 150、TSG 21）· search_documents（以往项目的安全阀选型）

## 回答怎么写
结论（所需面积、推荐口径）→ show_block 的 formula 计算过程（泄放工况、各参数取值）→ 选型建议（安全阀或爆破片）→ 提醒「以正式泄放计算为准」
