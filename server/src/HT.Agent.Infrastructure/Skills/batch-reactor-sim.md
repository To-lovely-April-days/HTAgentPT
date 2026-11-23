---
name: batch-reactor-sim
title: 间歇釜升温与放热模拟
category: 设计与标准
menu: hidden
status: enabled
source: own
description: 问釜升温要多久、夹套换热够不够、放热反应滴加多快不会超温、冷却压不压得住时用。
---

# 间歇釜升温与放热模拟

## 先确认
- 釜的规格：换热面积（先用 query_projects、search_documents 查产品资料和台账，查不到请用户给）
- 物料质量、比热；初温、工艺温度、允许最高温度
- 夹套介质：加热最高温度（导热油、蒸汽）、冷却最低温度（冷却水、冷冻水）
- 放热反应：反应热、滴加量和滴加物料质量；有没有反应速率数据（半衰期、动力学）
- 传热系数：用户有实测值优先用；没有就按经验值估，写成「估算」

## 怎么做
1. 用 run_python 按热量平衡算；结果用 matplotlib 画釜温随时间的曲线
2. 至少比较 2–3 种滴加时间，给出不超温的最短滴加时间
3. 没有动力学数据时，反应半衰期是假设：再用慢 3 倍的半衰期算一遍，说明累积变多时最高温度怎么变
4. 传热系数给一个范围（如 200–400），报最不利的结果
5. 算出的最大累积度可以接着用「反应热安全评估」技能评级

计算模板（单位 kg、J、K、s）：
```python
import numpy as np
from scipy.integrate import solve_ivp
m0, cp, UA = 400, 2500, 2.5 * 300
T0, Tp, Tmax, Th, Tc = 25, 120, 130, 180, 25
n, dH, mf = 1000, 250e3, 100
t_half, Ea = 300, 60e3
k0 = np.log(2) / t_half
def k(T): return k0 * np.exp(-Ea / 8.314 * (1 / (T + 273.15) - 1 / (Tp + 273.15)))
print(f"升温约 {m0 * cp / UA * np.log((Th - T0) / (Th - Tp)) / 60:.0f} 分钟；最大撤热 {UA * (Tp - Tc) / 1000:.0f} kW")
def run(t_dose):
    F = n / t_dose
    def f(t, y):
        T, a = y
        feed = F if t < t_dose else 0
        m = m0 + mf * min(t, t_dose) / t_dose
        Tj = min(Th, max(Tc, Tp - 20 * (T - Tp)))
        r = k(T) * a
        return [(r * dH - UA * (T - Tj) + feed * mf / n * cp * (T0 - T)) / (m * cp), feed - r]
    s = solve_ivp(f, [0, t_dose + 3600], [Tp, 0], max_step=10)
    return s.y[0].max(), (s.y[1] / n).max()
for h in (0.5, 1, 2):
    Tpk, acc = run(h * 3600)
    print(f"滴加 {h} h：最高 {Tpk:.0f} ℃，最大累积 {acc:.0%}{' 超限' if Tpk > Tmax else ''}")
```
模板里夹套按温控在加热和冷却之间自动调；只有冷却水、不能调温时，把 Tj 固定成 Tc。

## 常见的坑
- 用全容积代替物料实际接触的换热面积
- 传热系数取得太乐观（粘稠物料、结垢时会低很多）
- 只算平均放热，没算反应滞后造成的累积
- 把估算结果说成确定值

## 该查什么
run_python（scipy、ht）· query_projects、search_documents（换热面积、夹套形式）· show_block（metrics、chart）

## 回答怎么写
结论（升温多久、最短滴加时间）→ show_block 的 metrics → 温度曲线 → 不同滴加时间对比表 → 假设和不确定项（传热系数、半衰期）
