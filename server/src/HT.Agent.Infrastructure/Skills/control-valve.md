---
name: control-valve
title: 调节阀选型
category: 设计与标准
menu: hidden
status: enabled
source: library
description: 问调节阀的 Kv / Cv 怎么算、口径怎么选、流量特性怎么定时用。
---

# 调节阀选型

## 先确认
- 介质、温度；阀前、阀后压力（绝压）
- 最小、正常、最大三个工况的流量
- 管道口径

## 怎么做（run_python，fluids 库，按 IEC 60534 / ISA-75.01）
1. 三个工况各算一个 Kv：液体用 fluids.size_control_valve_l，气体用 fluids.size_control_valve_g。Cv = 1.156 × Kv
2. 选阀的额定 Kv：最大流量时开度一般不超过 80%–90%，最小流量时不低于 10%
3. 流量特性：压降变化大、调节范围宽的一般用等百分比；压降基本不变的可用线性
4. 液体要核对闪蒸和气蚀（fluids 计算时会考虑 FL 系数），气体要核对是否阻塞流
5. 公司常用的阀门品牌和型号，用 search_documents 查以往项目

计算模板（单位：Pa、K、m³/s、m）：
```python
import fluids
# 液体：水，P1 6 bar(a)、P2 4 bar(a)，5 m³/h，阀和管道 DN40
Kv = fluids.size_control_valve_l(rho=998.2, Psat=2339., Pc=22.064e6, mu=1.0e-3, P1=6e5, P2=4e5, Q=5/3600,
                                 D1=0.041, D2=0.041, d=0.041, FL=0.9, Fd=0.46)
# 气体：氮气 300 K，P1 10 bar(a)、P2 6 bar(a)，工况流量 0.05 m³/s
Kvg = fluids.size_control_valve_g(T=300, MW=28.01, mu=1.78e-5, gamma=1.4, Z=1.0, P1=10e5, P2=6e5, Q=0.05,
                                  D1=0.05, D2=0.05, d=0.05, FL=0.9, Fd=0.46, xT=0.7)
print(f"液体 Kv {Kv:.2f}（Cv {Kv*1.156:.2f}），气体 Kv {Kvg:.2f}")
```

## 常见的坑
- 只按最大流量选阀，常用工况开度太小，调节不稳
- 阀的压降占系统总压降太小，调节效果差
- FL、xT、Fd 是阀门本身的参数，不同阀型不同，写明取值

## 该查什么
run_python · chemical_info（物性）· search_documents

## 回答怎么写
结论（计算 Kv、推荐额定 Kv 和口径、流量特性）→ show_table 三个工况的计算结果和开度 → 取值和假设 → 需要确认的参数
