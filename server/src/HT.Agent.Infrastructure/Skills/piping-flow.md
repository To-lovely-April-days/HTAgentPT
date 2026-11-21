---
name: piping-flow
title: 管道与流体
category: 设计与标准
status: enabled
source: library
description: 算管径、流速、管道压降、泵的扬程和功率时用。
---

# 管道与流体

## 先确认
- 流体、温度、压力（物性不知道就按「物性查询」查）
- 流量；管长、管件和阀门；高差
- 是吸入管还是出口管；有没有固体颗粒

## 怎么算（run_python，fluids 库）
1. 先按经验流速定管径（常用范围，供估算）：
   - 液体：一般 0.5–3 m/s；泵吸入管 0.5–1.5 m/s
   - 气体：一般 10–30 m/s；蒸汽：15–40 m/s
2. 用 fluids.nearest_pipe 找标准管，fluids.one_phase_dP 算直管压降；管件、阀门的局部阻力另加
3. 泵：扬程 = 静压差 + 高差 + 管路总压降，轴功率 = ρ·g·Q·H / η；电机留余量。吸入侧要核对汽蚀余量，装置的有效汽蚀余量大于泵的必需汽蚀余量并留余量

计算模板（单位：m、kg/s、Pa）：
```python
import math, fluids
rho, mu = 998.2, 1.0e-3      # 密度 kg/m³、粘度 Pa·s
Q = 5/3600                   # 流量 m³/s（5 m³/h）
NPS, Di, Do, t = fluids.nearest_pipe(Di=0.040, schedule='40')   # 按内径找标准管
V = Q/(math.pi*Di**2/4)
dP = fluids.one_phase_dP(m=Q*rho, rho=rho, mu=mu, D=Di, roughness=0.05e-3, L=100)
print(f"内径 {Di*1000:.1f} mm，流速 {V:.2f} m/s，每 100 m 压降 {dP/1000:.1f} kPa")
H, eta = 30.0, 0.6           # 扬程 m、泵效率
print(f"泵轴功率 {rho*9.81*Q*H/eta/1000:.2f} kW")
```

## 常见的坑
- 高粘度流体要按层流算，fluids 会自动判断，但要写明 Re
- 易汽化液体（沸点附近）吸入管要特别核对汽蚀
- 气体压降大于入口压力的 10% 左右时，要按可压缩流算

## 该查什么
run_python · chemical_info（物性）· search_documents（公司管道等级表、以往项目）

## 回答怎么写
结论（推荐管径、流速、压降、泵的扬程和功率）→ show_block 的 formula 计算过程 → 假设和取值 → 需要确认的参数
