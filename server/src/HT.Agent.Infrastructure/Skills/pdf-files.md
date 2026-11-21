---
name: pdf-files
title: PDF 合并拆分水印
category: 图纸与文档
status: enabled
source: adapted
license: Apache-2.0
description: 要处理 PDF 文件时用：合并、拆分、旋转、加水印、提取文字，或者把内容做成 PDF。
---

# PDF 合并拆分水印

<!-- 改编自 anthropics/skills 的 pdf 技能（Apache-2.0），用 pypdf、reportlab 和 htfiles -->

## 先确认
- 处理哪几个附件（文件名写进 run_python 的 inputs）、顺序、要哪几页
- 水印写什么（如「霍桐仪器 仅供某研究院参考」）；发给谁
- 是扫描件吗：扫描件提不出文字，要文字先用 ocr_image

## 怎么判断
1. run_python 从 in/ 读，结果存到 out/，文件名写清楚做了什么
2. 合并、拆分、旋转用 pypdf；水印用 htfiles 的 watermark；新做 PDF 用 reportlab
3. 打印页数核对；系统会检查文件能不能打开

```python
from pypdf import PdfReader, PdfWriter
from htfiles import watermark
w = PdfWriter()
for f in ["in/方案.pdf", "in/资质.pdf"]:
    for p in PdfReader(f).pages:
        w.add_page(p)
w.write("out/方案和资质.pdf")
r = PdfReader("out/方案和资质.pdf")
part = PdfWriter()
for i in range(0, 3):
    part.add_page(r.pages[i])
part.write("out/前3页.pdf")
print(len(r.pages), "页；水印", watermark("out/方案和资质.pdf", "out/方案和资质_水印.pdf", "霍桐仪器 仅供参考"))
```

新做 PDF（中文用内置字体 STSong-Light，不用另装）：

```python
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.cidfonts import UnicodeCIDFont
from reportlab.platypus import SimpleDocTemplate, Paragraph, Table, TableStyle
pdfmetrics.registerFont(UnicodeCIDFont("STSong-Light"))
st = ParagraphStyle("b", fontName="STSong-Light", fontSize=11, leading=18)
cell = lambda s: Paragraph(s, st)
t = Table([[cell("介质"), cell("温度")], [cell("H<sub>2</sub>O"), cell("80 ℃")]])
t.setStyle(TableStyle([("GRID", (0, 0), (-1, -1), 0.5, "#999999")]))
SimpleDocTemplate("out/说明.pdf", pagesize=A4).build([Paragraph("生成 H<sub>2</sub>O 和 CO<sub>2</sub>。", st), t])
```

## 常见的坑（必须避开）
- 化学式下标用 &lt;sub&gt;2&lt;/sub&gt; 写在 Paragraph 里，不要用 ₂ 这类下标字符（内置字体显示成黑块）
- 表格里的字也要包成 Paragraph，否则中文和下标显示不出来
- 只处理员工传上来的文件，不改原件；加了密码的 PDF 先请员工给能打开的版本
- 页码从 0 数：第 1 页是 pages[0]

## 该查什么
read_attachments（先看内容和页数）· ocr_image（扫描件）

## 回答怎么写
说明做了什么（合并了哪几份、各几页、水印文字），结果几页；文件在卡片上预览、下载。
