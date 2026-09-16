using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HT.Agent.Application.Abstractions;
using HT.Agent.Infrastructure.Clients;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace HT.Agent.Tests;

/// <summary>Word 本地解析：标题层级、表格行与表头判定、损坏文件的报错口径。</summary>
public class OfficeDocxParserTests
{
    private static byte[] Docx(Action<Body> build, bool withStyles = true)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            if (withStyles)
            {
                var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
                stylesPart.Styles = new Styles(
                    new Style(new StyleName { Val = "heading 1" }) { StyleId = "Heading1", Type = StyleValues.Paragraph },
                    new Style(new StyleName { Val = "heading 2" }) { StyleId = "Heading2", Type = StyleValues.Paragraph });
            }
            var body = new Body();
            build(body);
            main.Document = new W.Document(body);
        }
        return ms.ToArray();
    }

    private static Paragraph P(string text, string? styleId = null)
        => styleId is null
            ? new Paragraph(new Run(new Text(text)))
            : new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = styleId }), new Run(new Text(text)));

    private static TableRow Row(params string[] cells)
        => new(cells.Select(c => new TableCell(new Paragraph(new Run(new Text(c))))).ToArray());

    private static Task<ParsedDocument> Parse(byte[] bytes, string name = "t.docx")
        => new OfficeDocxParser().ParseAsync(new MemoryStream(bytes), name, "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

    [Fact]
    public async Task 标题按样式分级_正文成段_空段落跳过()
    {
        var r = await Parse(Docx(b =>
        {
            b.AppendChild(P("技术参数", "Heading1"));
            b.AppendChild(P("2.1 压力", "Heading2"));
            b.AppendChild(P("使用压力 10 MPa，设计压力 12 MPa。"));
            b.AppendChild(P("   "));                       // 空白段落不出块
        }));

        Assert.Equal(3, r.Blocks.Count);
        Assert.Equal(("heading", 1), (r.Blocks[0].Kind, r.Blocks[0].Level));
        Assert.Equal(("heading", 2), (r.Blocks[1].Kind, r.Blocks[1].Level));
        Assert.Equal("paragraph", r.Blocks[2].Kind);
        // docx 的页码由渲染决定，文件里没有——宁可不标也不标错
        Assert.All(r.Blocks, x => Assert.Null(x.PageNo));
    }

    [Fact]
    public async Task 大纲级别也认作标题()
    {
        var r = await Parse(Docx(b => b.AppendChild(new Paragraph(
            new ParagraphProperties(new OutlineLevel { Val = 0 }), new Run(new Text("一、总则")))), withStyles: false));
        Assert.Equal(("heading", 1), (r.Blocks[0].Kind, r.Blocks[0].Level));
    }

    [Fact]
    public async Task 规整表格_首行作表头且不重复出块()
    {
        var r = await Parse(Docx(b => b.AppendChild(new Table(
            Row("名称", "数量", "单价"),
            Row("反应釜", "1", "58000"),
            Row("搅拌器", "2", "3200")))));

        Assert.Equal(2, r.Blocks.Count);                       // 表头行不单独出块
        Assert.All(r.Blocks, x => Assert.Equal("table_row", x.Kind));
        Assert.Equal("| 名称 | 数量 | 单价 |", r.Blocks[0].TableHeader);
        Assert.Equal("| 反应釜 | 1 | 58000 |", r.Blocks[0].Text);
    }

    [Fact]
    public async Task 表单式表格_首行不当表头()
    {
        // 任务单那种「标签|值|标签|值」：首行是数据不是表头，硬当表头会给每行挂错前缀
        var r = await Parse(Docx(b => b.AppendChild(new Table(
            Row("合同编号", "HT-2023-C091", "下单日期", "2023-04-11"),
            Row("客户名称", "华东理工大学化学工程学院", "紧急程度", "一般")))));

        Assert.Equal(2, r.Blocks.Count);                       // 首行也出块，没被当表头吃掉
        Assert.All(r.Blocks, x => Assert.Null(x.TableHeader));
        Assert.Contains("HT-2023-C091", r.Blocks[0].Text);
        Assert.Contains("华东理工大学化学工程学院", r.Blocks[1].Text);
    }

    [Fact]
    public async Task 空文档与损坏文件报内容性错误()
    {
        var empty = await Assert.ThrowsAsync<ParseContentException>(() => Parse(Docx(_ => { })));
        Assert.Contains("未解析出任何内容块", empty.Reason);

        var broken = await Assert.ThrowsAsync<ParseContentException>(
            () => Parse("这不是 docx"u8.ToArray(), "坏文件.docx"));
        Assert.Contains("坏文件.docx", broken.Reason);

        var none = await Assert.ThrowsAsync<ParseContentException>(() => Parse([], "空.docx"));
        Assert.Equal("文件为空", none.Reason);
    }

    [Fact]
    public void 只接管docx_其余格式仍走所选引擎()
    {
        Assert.True(OfficeDocxParser.Handles("方案.docx"));
        Assert.True(OfficeDocxParser.Handles("方案.DOCX"));
        Assert.False(OfficeDocxParser.Handles("方案.doc"));     // 老二进制格式交给引擎
        Assert.False(OfficeDocxParser.Handles("说明书.pdf"));
    }
}
