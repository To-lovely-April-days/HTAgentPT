using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Services;
using HT.Agent.Infrastructure.Templates;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace HT.Agent.Tests;

public class TemplateExtractorTests
{
    private static byte[] TemplateDocx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            // 行内控件：customer_name（占位文字含「客户」→ 默认禁继承 FR-5.7；
            // 控件标题「章节/名称」→ 显示名称与章节自动带出）
            body.AppendChild(new Paragraph(
                new Run(new Text("客户名称：")),
                MakeSdtRun("customer_name", "请填写客户全称", "基本信息/客户名称")));
            // 行内控件：design_pressure（标题只写名称 → 章节留空）
            body.AppendChild(new Paragraph(MakeSdtRun("design_pressure", "设计压力，如 10 MPa", "设计压力")));
            // 块级控件：tech_overview → 长段落
            body.AppendChild(MakeSdtBlock("tech_overview", "本节描述技术方案总体思路"));
            // 无 Tag 控件 → 警告并跳过
            var noTag = new SdtRun(new SdtProperties(), new SdtContentRun(new Run(new Text("孤儿控件"))));
            body.AppendChild(new Paragraph(noTag));
            // 重复 Tag → 警告
            body.AppendChild(new Paragraph(MakeSdtRun("design_pressure", "重复出现")));
            main.Document = new W.Document(body);
        }
        return ms.ToArray();
    }

    internal static SdtRun MakeSdtRun(string tag, string placeholder, string? alias = null) => new(
        alias is null
            ? new SdtProperties(new Tag { Val = tag })
            : new SdtProperties(new SdtAlias { Val = alias }, new Tag { Val = tag }),
        new SdtContentRun(new Run(new Text(placeholder))));

    internal static SdtBlock MakeSdtBlock(string tag, string placeholder) => new(
        new SdtProperties(new Tag { Val = tag }),
        new SdtContentBlock(new Paragraph(new Run(new Text(placeholder)))));

    [Fact]
    public void 抽取控件Tag_占位文字作话术_默认禁继承命中()
    {
        var r = TemplateSlotExtractor.Extract(new MemoryStream(TemplateDocx()));
        Assert.Equal(3, r.Slots.Count); // 无 Tag 与重复各被跳过
        Assert.Equal(2, r.Warnings.Count);
        var customer = r.Slots.Single(s => s.Tag == "customer_name");
        Assert.Equal("请填写客户全称", customer.Prompt);
        Assert.True(customer.ForbidInherit); // 占位文字含「客户」
        Assert.Equal("客户名称", customer.Name);   // 控件标题「章节/名称」
        Assert.Equal("基本信息", customer.Section);
        var overview = r.Slots.Single(s => s.Tag == "tech_overview");
        Assert.Equal(SlotDataType.LongText, overview.DataType); // 块级 → 长段落
        Assert.Null(overview.Name); // 没写控件标题 → 显示名称待管理员维护
        var pressure = r.Slots.Single(s => s.Tag == "design_pressure");
        Assert.False(pressure.ForbidInherit);
        Assert.Equal("设计压力", pressure.Name); // 标题只写名称
        Assert.Null(pressure.Section);
    }

    [Fact]
    public async Task 回填_留空清占位_页眉盖待复核()
    {
        var filled = await DocxSlotFiller.FillAsync(new MemoryStream(TemplateDocx()),
        [
            new DocxSlotFiller.FillInput("customer_name", "华东理工", SlotDataType.Text),
            new DocxSlotFiller.FillInput("design_pressure", null, SlotDataType.Text), // 留空
            new DocxSlotFiller.FillInput("tech_overview", "第一行\n第二行", SlotDataType.LongText)
        ]);
        Assert.Equal(2, filled.Filled);
        // 夹具中 design_pressure 有两个同 Tag 控件（重复在抽取期已警告）：
        // 回填器对同 Tag 的每个控件都按同一取值处理，两处都清空 → 留空计 2
        Assert.Equal(2, filled.LeftBlank);
        using var doc = WordprocessingDocument.Open(new MemoryStream(filled.Output), false);
        var text = doc.MainDocumentPart!.Document.InnerText;
        Assert.Contains("华东理工", text);
        Assert.DoesNotContain("设计压力，如 10 MPa", text);  // 占位文字不进正式文件
        Assert.DoesNotContain("重复出现", text);
        Assert.Contains("第一行", text);
        var header = doc.MainDocumentPart.HeaderParts.Single().Header.InnerText;
        Assert.Contains("待复核", header); // FR-5.15
    }

    [Fact]
    public async Task 填好的草稿能整篇译出_填进去的值本身也译_版式仍在()
    {
        // 「英文版」走的就是这条链：草稿（与正式产出同一条回填路径）→ 整篇翻译 → 按原位置回填。
        // 这里锁住两件事：填进控件里的取值也在可译范围内（不是只译模板里的固定文字），
        // 页眉的「待复核」同样译得到——译文一样是待复核件。
        var filled = await DocxSlotFiller.FillAsync(new MemoryStream(TemplateDocx()),
        [
            new DocxSlotFiller.FillInput("customer_name", "华东理工", SlotDataType.Text),
            new DocxSlotFiller.FillInput("tech_overview", "采用夹套油浴控温", SlotDataType.LongText)
        ]);

        var result = await Infrastructure.Translation.DocxTranslator.TranslateAsync(
            new MemoryStream(filled.Output),
            (batch, _) => Task.FromResult<IReadOnlyList<string>>(batch.Select(t => "[EN] " + t).ToList()),
            3000);

        Assert.True(result.Translated > 0);
        Assert.Equal(result.Paragraphs, result.Translated);   // 这份夹具里没有译不了的元素
        using var doc = WordprocessingDocument.Open(new MemoryStream(result.Output), false);
        var text = doc.MainDocumentPart!.Document.InnerText;
        Assert.Contains("[EN] ", text);
        Assert.Contains("华东理工", text);                     // 填进去的取值进了译文的输入
        Assert.Contains("采用夹套油浴控温", text);
        Assert.Contains("[EN] ", doc.MainDocumentPart.HeaderParts.Single().Header.InnerText);
    }
}

public class CompletenessTests
{
    private static Template T(params TemplateSlot[] slots)
    {
        var t = new Template { Name = "t", DocType = "方案", FileKey = "k" };
        t.Slots.AddRange(slots);
        return t;
    }

    private static TemplateSlot Slot(string tag, bool required = true, SlotStage stage = SlotStage.Current)
        => new() { Tag = tag, Name = tag, Section = "s", Required = required, Stage = stage };

    [Fact]
    public void AI建议未确认按未完成计()
    {
        var view = GenerationService.Completeness(
            T(Slot("a"), Slot("b")),
            [
                new SlotState("a", "值", SlotFillSource.AiSuggested, Confirmed: false, false, null, null),
                new SlotState("b", "值", SlotFillSource.Inherited, Confirmed: false, false, null, null)
            ]);
        Assert.False(view.CanRender);
        var item = Assert.Single(view.Incomplete);
        Assert.Equal("a", item.Tag);
        Assert.Equal("AI 建议待确认", item.Reason); // FR-5.13：待确认也算未完成；继承不算
    }

    [Fact]
    public void 后续阶段槽位不参与校验()
    {
        var view = GenerationService.Completeness(
            T(Slot("later", stage: SlotStage.Later)),
            [new SlotState("later", null, null, false, false, null, null)]);
        Assert.True(view.CanRender);
        Assert.Equal(0, view.Total);
    }

    [Fact]
    public void 非必填空槽位不拦生成但计入未完成分母()
    {
        var view = GenerationService.Completeness(
            T(Slot("opt", required: false)),
            [new SlotState("opt", null, null, false, false, null, null)]);
        Assert.True(view.CanRender);
        Assert.Equal(0, view.Done);
    }
}
