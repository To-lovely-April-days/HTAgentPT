---
name: excel-files
title: Excel 报价单与计算表
category: 图纸与文档
status: enabled
source: adapted
license: Apache-2.0
description: 要做 Excel 文件时用：报价单、计算表、选型对比表、设备或备件清单，要带公式、改数能重算的。
---

# Excel 报价单与计算表

<!-- 改编自 anthropics/skills 的 xlsx 技能（Apache-2.0），换成我们的工具和习惯 -->

## 先确认
- 做什么表、给谁用（内部核算还是发客户）；报价含不含税、税率多少
- 数据从哪来：价格用 search_prices 查价格库，配置、数量问清楚；缺的先问，不要编
- 列哪些项、要不要分工作表（报价单 / 材料明细 / 计算依据）

## 怎么判断
1. 用 run_python 写 openpyxl 代码，文件存到 out/，文件名写清楚是什么
2. 能算出来的格子一律写公式（小计、合计、税额、单位换算），不要在 Python 里算好填死数——员工改了数量单价要能自己重算
3. 手填的数（数量、单价、税率、假设参数）蓝色字，公式黑色字；单价旁边写来源和日期
4. 做完系统会重算公式检查，没过会告诉你哪一格错，改了再跑

```python
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill
wb = Workbook(); ws = wb.active; ws.title = "报价单"
ws.append(["序号", "项目", "数量", "单价（元）", "小计（元）"])
rows = [("30L 釜体 316L", 1, 68000), ("磁力搅拌器", 1, 21500)]
for i, (name, q, p) in enumerate(rows):
    r = i + 2
    ws.append([i + 1, name, q, p, f"=C{r}*D{r}"])
last = len(rows) + 1
ws.cell(last + 1, 2, "合计"); ws.cell(last + 1, 5, f"=SUM(E2:E{last})")
ws.cell(last + 2, 2, "税率"); ws.cell(last + 2, 5, 0.13)
ws.cell(last + 3, 2, "含税总价"); ws.cell(last + 3, 5, f"=E{last+1}*(1+E{last+2})")
for r in range(2, last + 1):
    ws.cell(r, 3).font = ws.cell(r, 4).font = Font(color="0000FF")
for c in ws[1]:
    c.font = Font(bold=True); c.fill = PatternFill("solid", fgColor="F2F2F2")
for col, w in zip("ABCDE", (6, 28, 8, 14, 16)):
    ws.column_dimensions[col].width = w
ws.freeze_panes = "A2"
wb.save("out/30L加氢釜报价单.xlsx")
```

## 常见的坑（必须避开）
- 不用新函数：XLOOKUP、FILTER、LET、TEXTJOIN、UNIQUE 老版 Excel 和 WPS 打不开或显示 #NAME?；用 VLOOKUP、INDEX/MATCH、SUMIF
- 公式里的行号随数据行变：用变量拼行号，别手写死；删行、插行后检查 SUM 范围
- 除数可能为 0 的用 IF 包一下；百分比格子存 0.13 再设成百分比格式，不要存 13
- 金额设千分位格式，列宽设够，表头加粗冻结；不要合并单元格做表头（排序筛选会坏）
- 一个文件最多几个工作表，别把计算过程全堆进报价单页

## 该查什么
search_prices（单价，写明是采购价还是售价、哪个月的）· query_projects（同类项目的配置和数量）· cost_estimate（材料成本）

## 回答怎么写
一两句说明做了什么、关键数字（合计、含税总价）、数据来源和日期；提醒哪些是假设、员工要核对哪几格（蓝色格）。不要把整张表再贴一遍。
