---
name: ppt-files
title: PPT 方案汇报
category: 图纸与文档
status: enabled
source: adapted
license: Apache-2.0
description: 要做 PPT 文件时用：方案汇报、产品介绍、项目总结，要能直接拿去讲的。
---

# PPT 方案汇报

<!-- 改编自 anthropics/skills 的 pptx 技能（Apache-2.0），换成 python-pptx -->

## 先确认
- 给谁讲、讲多久（10 分钟约 8–12 页）、要不要放价格
- 内容来源：方案、参数、案例先查清楚，PPT 里的数字都要有出处
- 公司名写「上海霍桐实验仪器有限公司」，不要编 Logo 和口号

## 怎么判断
1. 先列提纲：封面 → 需求 → 方案要点 → 配置对比 → 案例 → 交付与售后，一页一个意思
2. run_python 用 python-pptx 做，16:9，存到 out/
3. 字多就拆页：一页最多 6 条，每条不超过 25 个字；表格最多 6 行
4. 系统会检查文件能不能打开、有几页

```python
from pptx import Presentation
from pptx.util import Inches, Pt
prs = Presentation()
prs.slide_width, prs.slide_height = Inches(13.333), Inches(7.5)
def bullets(title, items):
    s = prs.slides.add_slide(prs.slide_layouts[1])
    s.shapes.title.text = title
    tf = s.placeholders[1].text_frame
    tf.text = items[0]
    for t in items[1:]:
        tf.add_paragraph().text = t
    for p in tf.paragraphs:
        p.font.size = Pt(20)
def table(title, rows):
    s = prs.slides.add_slide(prs.slide_layouts[5])
    s.shapes.title.text = title
    t = s.shapes.add_table(len(rows), len(rows[0]), Inches(0.6), Inches(1.6), Inches(12), Inches(0.5) * len(rows)).table
    for i, row in enumerate(rows):
        for j, v in enumerate(row):
            t.cell(i, j).text = str(v)
            t.cell(i, j).text_frame.paragraphs[0].font.size = Pt(16)
c = prs.slides.add_slide(prs.slide_layouts[0])
c.shapes.title.text = "30L 加氢反应釜方案"
c.placeholders[1].text = "上海霍桐实验仪器有限公司"
bullets("设计要点", ["设计压力 10 MPa，设计温度 250 ℃", "釜体 316L，磁力耦合密封"])
table("配置对比", [["项目", "方案 A", "方案 B"], ["材质", "316L", "C276"]])
prs.save("out/30L加氢釜方案.pptx")
```

## 常见的坑（必须避开）
- 字太多溢出文本框：宁可拆页，正文不小于 18 号
- 不要每页换颜色、加花哨形状；版式统一，用默认版式
- 图表先用 matplotlib 画成 PNG 存到本地再插入（slide.shapes.add_picture），不要用截图
- 价格、承诺类内容发客户前要人工确认，在回答里提醒

## 该查什么
search_documents（方案、产品资料）· query_projects（案例）· list_catalog（产品规格）· search_prices（要放价格时）

## 回答怎么写
说明一共几页、每页讲什么（一行一页），哪些数字需要员工核对；文件在卡片上预览、下载。
