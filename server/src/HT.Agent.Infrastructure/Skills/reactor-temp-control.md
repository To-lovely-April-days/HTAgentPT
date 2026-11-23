---
name: reactor-temp-control
title: 温度控制与动态模拟
category: 设计与标准
menu: hidden
status: enabled
source: own
description: 问釜的温度控制会不会超调、PID 参数怎么调、温控单元或冷却停了多久会超温、加什么联锁时用。
---

# 温度控制与动态模拟

## 先确认
- 釜：物料量、比热、换热面积和传热系数（UA）、夹套内介质量
- 温控方式：加热冷却一体的温控单元（分程），还是只有冷却水
- 反应：反应热、滴加量和滴加时间、反应速率（半衰期，没有就是假设）
- 设定温度、允许最高温度；现在用的 PID 参数
- 连续装置（中试的液位、换热器出口温度）：用 process_sim 先算稳态，动态控制在第二步做

## 怎么做
1. 用 run_python 按下面的模板算（热量平衡 + 夹套 + 测温滞后 + PID）；用 matplotlib 画温度曲线
2. 比较 3 组 PID 参数：最高超出多少、后段波动多大、平均偏差
3. 先看冷却能力够不够：满开冷却能撤的热比放热小，调 PID 没用，要放慢滴加或加大换热
4. 故障工况：温控单元或冷却停了多久超过允许温度，据此建议联锁（如冷却中断就停加料）
5. 只有冷却水、没有加热时，开始阶段温度可能掉下去、物料累积后又冲上来，要提醒

计算模板（单位 kg、J、K、s）：
```python
import numpy as np
from scipy.integrate import solve_ivp
m, cp, UA = 400, 2500, 2.5 * 300          # 物料 kg、比热 J/(kg·K)、夹套 UA W/K
mj, cpj, Fw = 150, 2300, 2.0            # 夹套内导热油 kg、比热 J/(kg·K)、循环量 kg/s
Th, Tw = 180, 25                          # 温控单元能给的最高、最低进油温度 ℃（分程：加热 / 冷却）
tau_m = 60                                # 测温滞后 s（热电偶套管）
n, dH, t_dose = 1000, 250e3, 7200         # 滴加量 mol、反应热 J/mol、滴加时间 s
k0, Ea, Tsp, Tmax = np.log(2) / 300, 60e3, 120, 130
def k(T): return k0 * np.exp(-Ea / 8.314 * (1 / (T + 273.15) - 1 / (Tsp + 273.15)))
def run(Kp, Ti, t_fail=None, t_end=7200):
    def f(t, y):
        T, Tj, a, I, Tm = y
        e = Tm - Tsp                                  # 控制器看到的是测量值；冷却：温度高了开大阀
        u = np.clip(Kp * (e + I / Ti), 0, 1)          # 0 = 全加热，1 = 全冷却
        Tin = Th - u * (Th - Tw)
        F = 0 if t_fail is not None and t >= t_fail else Fw   # 温控单元停了（循环中断）
        feed = n / t_dose if t < t_dose else 0
        r = k(T) * a
        dT = (r * dH - UA * (T - Tj)) / (m * cp)
        dTj = (F * cpj * (Tin - Tj) + UA * (T - Tj)) / (mj * cpj)
        dI = e if 0 < Kp * (e + I / Ti) < 1 else 0    # 阀到头时不再积分（抗积分饱和）
        return [dT, dTj, feed - r, dI, (T - Tm) / tau_m]
    s = solve_ivp(f, [0, t_end], [Tsp, Tsp, 0, 0.5 * Ti / Kp, Tsp], max_step=5)
    return s.t, s.y[0]
def score(t, T):
    tail = T[t > t[-1] - 1800]                   # 最后 30 分钟
    return T.max() - Tsp, tail.max() - tail.min(), tail.mean() - Tsp
print(f"满开冷却能撤 {UA * (Tsp - Tw) / 1000:.0f} kW（夹套接近进水温度时），平均放热 {n * dH / t_dose / 1000:.0f} kW")
for Kp, Ti in [(2.0, 60), (0.8, 300), (0.3, 900)]:
    t, T = run(Kp, Ti); o, osc, off = score(t, T)
    print(f"Kp {Kp} Ti {Ti/60:.0f} min：最高超出 {o:.1f} ℃，后 30 分钟波动 {osc:.1f} ℃、平均偏差 {off:+.1f} ℃")
t, T = run(0.8, 360, t_fail=1800)
i = np.argmax(T > Tmax)
print(f"第 30 分钟温控单元停了：{(t[i] - 1800) / 60:.1f} 分钟后超过 {Tmax} ℃" if T.max() > Tmax else f"冷却中断后最高 {T.max():.0f} ℃，没超 {Tmax}")
```

## 常见的坑
- 不算测温滞后，整定出来的参数太激进
- 传热系数、反应速率是估的，却给出确定的参数
- 只看平均放热，不看冷却能力的上限

## 该查什么
run_python · process_sim · show_block（chart、metrics、interlock）

## 回答怎么写
结论（推荐的参数、会不会超温）→ 温度曲线 → 几组参数对比 → 故障工况和建议的联锁 → 说明哪些是估算，现场要按实际响应再整定
