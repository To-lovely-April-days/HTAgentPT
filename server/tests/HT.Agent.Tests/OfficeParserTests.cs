using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HT.Agent.Application.Abstractions;
using HT.Agent.Infrastructure.Clients;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace HT.Agent.Tests;

/// <summary>Office 本地解析：Word 标题层级与表格、Excel 工作表、PPT 每页，
/// 以及图片抽取的去重与过滤、损坏文件的报错口径。</summary>
public class OfficeParserTests
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
        => new OfficeParser().ParseAsync(new MemoryStream(bytes), name, "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

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
        Assert.Contains("未解析出任何内容", empty.Reason);

        var broken = await Assert.ThrowsAsync<ParseContentException>(
            () => Parse("这不是 docx"u8.ToArray(), "坏文件.docx"));
        Assert.Contains("坏文件.docx", broken.Reason);

        var none = await Assert.ThrowsAsync<ParseContentException>(() => Parse([], "空.docx"));
        Assert.Equal("文件为空", none.Reason);
    }

    [Fact]
    public void 接管OOXML全家族_老格式与PDF不接管()
    {
        foreach (var f in new[] { "方案.docx", "方案.DOCX", "带宏.docm", "模板.dotx", "模板.dotm",
                                  "清单.xlsx", "清单.xlsm", "讲稿.pptx", "讲稿.potx" })
            Assert.True(OfficeParser.Handles(f), f);
        foreach (var f in new[] { "方案.doc", "清单.xls", "讲稿.ppt", "稿子.wps", "说明书.pdf", "图.png" })
            Assert.False(OfficeParser.Handles(f), f);
        // 老版 Office / WPS 私有格式：本地读不了，但要认得出来才能给「另存为 .docx」的提示
        foreach (var f in new[] { "方案.doc", "清单.xls", "稿子.wps", "表.et", "文档.odt" })
            Assert.True(OfficeParser.IsLegacyOffice(f), f);
        Assert.False(OfficeParser.IsLegacyOffice("方案.docx"));
    }

    [Fact]
    public async Task 图片_新旧两种写法都取_同一张只取一次_小图标与不可渲染格式跳过()
    {
        var png = Png(360, 220);           // 正经插图，压缩后不到 1KB
        var bytes = DocxWithImages(png);
        var r = await Parse(bytes);

        // 文档里共 5 处图片引用：正文 DrawingML、表格内 DrawingML、老式 VML（同一张）、
        // 12×12 小图标、EMF 矢量图 —— 同一部件只入一次，图标与 EMF 被过滤
        Assert.Single(r.Images!);
        var img = r.Images![0];
        Assert.Equal("image/png", img.ContentType);
        Assert.Equal("图 1 反应釜结构示意", img.Caption);   // 题注取图片所在段落文字
        Assert.Null(img.PageNo);
    }

    [Fact]
    public async Task 只有图没有字的文档_也能入库()
    {
        var r = await Parse(DocxImageOnly(Png(640, 480)));
        Assert.Single(r.Images!);
        var block = Assert.Single(r.Blocks);
        Assert.Contains("没有可提取的文字", block.Text);
        Assert.Contains("1 张图片", block.Text);
    }

    [Fact]
    public async Task Excel_工作表成标题_行成表格行_稀疏列对齐()
    {
        var r = await Parse(Xlsx(), "清单.xlsx");
        Assert.Equal("heading", r.Blocks[0].Kind);
        Assert.Equal("配件清单", r.Blocks[0].Text);
        var rows = r.Blocks.Where(b => b.Kind == "table_row").ToList();
        Assert.Equal(2, rows.Count);                                   // 表头行不重复出块
        Assert.Equal("| 名称 | 数量 | 规格 |", rows[0].TableHeader);
        Assert.Equal("| 导热油 | 40 | YD-350 |", rows[0].Text);
        // B 列缺失的稀疏行：补空列，不让取值串到上一列去
        Assert.Equal("| 内衬杯 |  | PTFE |", rows[1].Text);
    }

    [Fact]
    public async Task PPT_每页标题成标题块_正文成段()
    {
        var r = await Parse(Pptx(), "讲稿.pptx");
        Assert.Equal(("heading", "产品概述"), (r.Blocks[0].Kind, r.Blocks[0].Text));
        Assert.Equal(("paragraph", "适用于高压加氢反应"), (r.Blocks[1].Kind, r.Blocks[1].Text));
    }

    // ── 夹具 ────────────────────────────────────────────

    /// <summary>带合法 IHDR 的 PNG 头：解析器按像素尺寸筛图标，所以夹具要能被读出宽高
    /// （线条图压缩后可能只有几百字节，单看体积会把正经插图误杀）。</summary>
    private static byte[] Png(int width, int height, int size = 900)
    {
        var b = new byte[Math.Max(size, 24)];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        "IHDR"u8.ToArray().CopyTo(b, 12);
        foreach (var (value, at) in new[] { (width, 16), (height, 20) })
        {
            b[at] = (byte)(value >> 24); b[at + 1] = (byte)(value >> 16);
            b[at + 2] = (byte)(value >> 8); b[at + 3] = (byte)value;
        }
        return b;
    }

    private static byte[] DocxWithImages(byte[] png)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var big = AddImage(main, png, "rBig");
            var icon = AddImage(main, Png(12, 12), "rIcon");   // 小图标：按像素尺寸筛掉
            var emf = main.AddNewPart<ImagePart>("image/x-emf", "rEmf");
            emf.FeedData(new MemoryStream(Png(400, 300)));    // EMF：浏览器显示不了，跳过

            var body = new Body();
            body.AppendChild(new Paragraph(new Run(new Text("图 1 反应釜结构示意")), Drawing(big)));
            body.AppendChild(new Paragraph(new Run(new Text("图 2 小图标")), Drawing(icon)));
            body.AppendChild(new Paragraph(new Run(new Text("图 3 矢量图")), Drawing("rEmf")));
            // 老式 VML 引用同一张大图：不能重复入库
            body.AppendChild(new Paragraph(new Run(new Picture(
                new DocumentFormat.OpenXml.Vml.Shape(
                    new DocumentFormat.OpenXml.Vml.ImageData { RelationshipId = big })))));
            body.AppendChild(new W.Table(new W.TableRow(new W.TableCell(
                new Paragraph(new Run(new Text("表内图")), Drawing(big))))));
            main.Document = new W.Document(body);
        }
        return ms.ToArray();
    }

    private static byte[] DocxImageOnly(byte[] png)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var id = AddImage(main, png, "rOnly");
            main.Document = new W.Document(new Body(new Paragraph(Drawing(id))));
        }
        return ms.ToArray();
    }

    private static string AddImage(MainDocumentPart main, byte[] bytes, string id)
    {
        var part = main.AddNewPart<ImagePart>("image/png", id);
        part.FeedData(new MemoryStream(bytes));
        return id;
    }

    /// <summary>最小可用的图片引用：解析器只看 a:blip 的 r:embed，不需要完整的尺寸与定位信息。</summary>
    private static W.Run Drawing(string relationshipId)
    {
        var pic = new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
            new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(new A.Blip { Embed = relationshipId }));
        var graphicData = new A.GraphicData(pic) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" };
        var inline = new DW.Inline(
            new DW.Extent { Cx = 100000L, Cy = 100000L },
            new DW.DocProperties { Id = 1U, Name = "img" },
            new A.Graphic(graphicData));
        return new W.Run(new W.Drawing(inline));
    }

    private static byte[] Xlsx()
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new S.Workbook();
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            wsPart.Worksheet = new S.Worksheet(new S.SheetData(
                Row(1, ("A", "名称"), ("B", "数量"), ("C", "规格")),
                Row(2, ("A", "导热油"), ("B", "40"), ("C", "YD-350")),
                Row(3, ("A", "内衬杯"), ("C", "PTFE"))));           // B 列缺失
            wbPart.Workbook.AppendChild(new S.Sheets(new S.Sheet
            {
                Id = wbPart.GetIdOfPart(wsPart), SheetId = 1U, Name = "配件清单"
            }));
        }
        return ms.ToArray();

        static S.Row Row(uint index, params (string Col, string Value)[] cells)
            => new(cells.Select(c => new S.Cell
            {
                CellReference = c.Col + index,
                DataType = S.CellValues.String,
                CellValue = new S.CellValue(c.Value)
            }).ToArray()) { RowIndex = index };
    }

    private static byte[] Pptx()
    {
        using var ms = new MemoryStream();
        using (var doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation))
        {
            var presPart = doc.AddPresentationPart();
            presPart.Presentation = new P.Presentation();
            var slidePart = presPart.AddNewPart<SlidePart>("rSlide1");
            slidePart.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(),
                new P.Shape(
                    new P.NonVisualShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 2U, Name = "标题" },
                        new P.NonVisualShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.ShapeProperties(),
                    new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                        new A.Paragraph(new A.Run(new A.Text("产品概述"))),
                        new A.Paragraph(new A.Run(new A.Text("适用于高压加氢反应"))))))));
            presPart.Presentation.AppendChild(new P.SlideIdList(
                new P.SlideId { Id = 256U, RelationshipId = "rSlide1" }));
        }
        return ms.ToArray();
    }
}
