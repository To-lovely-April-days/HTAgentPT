---
name: explosion-purge
title: 爆炸极限与惰化置换
category: 安全与化学
menu: hidden
status: enabled
source: own
description: 问可燃气或混合气的爆炸极限、会不会进爆炸范围、开车前或开釜前用氮气置换几次、氧含量或可燃气要降到多少时用。
---

# 爆炸极限与惰化置换

## 先确认
- 釜里是什么气体、各占多少（混合气给组成）
- 置换方式：充压后放空（充到多少、放到多少），还是抽真空后充氮气
- 目标：氧含量降到多少（公司或客户有规定就按规定），开釜前可燃气降到多少
- 压力是表压还是绝压

## 怎么做
1. 爆炸下限、上限用 run_python 的 chemicals 库查（名称用英文名或 CAS 号），写明数据出处（库里给的标准，如 IEC 60079-20-1）
2. 混合气按勒夏特列公式算；上限的公式误差大，只作参考
3. 极限氧浓度（LOC，可燃气燃烧需要的最低氧含量）库里没有：用 search_documents、search_literature 或 web_search 查，查到写出处，查不到就说缺，不估
4. 压力置换：每次剩下的比例 = 低压 ÷ 高压（都用绝压）；抽真空置换同一个公式
5. 目标没给时：氧含量按 1% 以下，开釜前可燃气按爆炸下限的 25% 以下，并说明是常用做法，以公司或客户规定为准
6. 算出次数后建议多做 1 次，并用氧分析仪或可燃气检测仪确认

计算模板：
```python
import math
from chemicals import CAS_from_any
from chemicals.safety import LFL, UFL, LFL_methods, fire_mixing
gas = {'hydrogen': 0.30, 'methane': 0.70}
cas = {g: CAS_from_any(g) for g in gas}
lfl = {g: LFL(CASRN=c) for g, c in cas.items()}
ufl = {g: UFL(CASRN=c) for g, c in cas.items()}
for g in gas: print(f"{g} {lfl[g]:.1%}–{ufl[g]:.0%}（{LFL_methods(CASRN=cas[g])[0]}）")
ys = list(gas.values())
L, U = fire_mixing(ys, [lfl[g] for g in gas]), fire_mixing(ys, [ufl[g] for g in gas])
print(f"混合气 {L:.1%}–{U:.1%}")
Pa = 0.101325
r = (0 + Pa) / (0.5 + Pa)          # 放到常压 ÷ 充到 0.5 MPa(G)
def cycles(y0, target): return math.ceil(math.log(target / y0) / math.log(r))
n = cycles(0.21, 0.01)
print(f"氧含量 ≤1%：{n} 次", ' → '.join(f"{0.21 * r**i:.2%}" for i in range(n + 1)))
n2 = cycles(1.0, 0.25 * L)
print(f"可燃气 ≤{0.25 * L:.2%}：{n2} 次", ' → '.join(f"{r**i:.2%}" for i in range(n2 + 1)))
```

## 常见的坑
- 表压当绝压算，次数会少算
- 只置换了气相，忽略了溶剂挥发出的可燃气、液体里溶解的气体
- 釜里有死角（长管、仪表接管）时公式偏乐观，要多做几次再检测
- 开釜前直接放空气进去

## 该查什么
run_python（chemicals）· search_documents（公司操作规程、置换规定）· search_literature（LOC 数据）· hazard_lookup（是否重点监管工艺）

## 回答怎么写
结论（几次、降到多少）→ show_block 的 metrics 列爆炸极限和目标值 → 置换次数表或曲线 → 数据出处 → 提醒「以检测仪读数为准」
