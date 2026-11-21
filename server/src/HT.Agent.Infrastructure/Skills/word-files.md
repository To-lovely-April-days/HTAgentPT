---
name: word-files
title: Word 修订与批注
category: 图纸与文档
status: enabled
source: adapted
license: Apache-2.0
description: 要在客户发来的 Word 上直接改（出修订版、加批注、偏离答复），或要做版式复杂的 Word 文件时用。
---

# Word 修订与批注

<!-- 改编自 anthropics/skills 的 docx 技能（Apache-2.0），修订和批注用沙箱里的 htfiles 小工具 -->

## 先确认
- 是改客户的原文件，还是新做一份；改原文件要把附件名写进 run_python 的 inputs
- 按什么改：公司能做到的参数、偏离项和理由；不确定的条款先问，别替公司答应
- 改动用修订（客户能逐条接受），理由写批注；批注作者是「霍桐仪器」

## 怎么判断
1. 先用 read_attachments 读一遍原文，列出要改的条目和改成什么
2. run_python：从 in/ 打开原文件，只改要改的那几个字，存到 out/「原文件名_霍桐修订.docx」
3. 打印 revisions() 的结果核对修订、批注数量和你列的一致
4. 系统会检查文件能不能打开，没过会告诉你原因

```python
from docx import Document
from htfiles import track_replace, track_delete, track_insert, comment, revisions
doc = Document("in/技术协议.docx")
track_replace(doc, "12 MPa", "10 MPa", comment="12 MPa 超出 30L 标准釜范围，按 10 MPa 设计")
track_replace(doc, "机械密封", "磁力耦合密封")
track_insert(doc, "设计温度：250 ℃", "（最高 280 ℃）")
track_delete(doc, "，含现场安装调试")
comment(doc, "316L 不锈钢", "介质含 Cl⁻ 时有点蚀风险，请确认氯离子浓度")
doc.save("out/技术协议_霍桐修订.docx")
print(revisions("out/技术协议_霍桐修订.docx"))
```

## 常见的坑（必须避开）
- 不要重新生成整份文件、不要整段替换：只改那几个字，原格式、页眉页脚、编号才不会乱
- 找不到原文会报错：原文可能有全角半角、空格、换行不同，先 print 几段核对，再用原文的写法
- 同一段字出现多次，用 nth=2 指第几处
- 表格里的字也能找到；页眉页脚里的不改
- 新做文件时（不是改原文件）：中文字体要设东亚字体，标题用内置「Heading 1/2」样式，表格加边框；页面默认 A4

## 该查什么
read_attachments（原文）· search_documents（公司标准参数、以前的偏离答复）· query_projects（同类项目做到过什么）· search_prices（偏离要加钱时）

## 回答怎么写
先列改了哪几处（原来 → 改成，一句理由），再说文件在卡片上可以下载；提醒还要人工确认的条款。修订和批注的数量要和文件一致。
