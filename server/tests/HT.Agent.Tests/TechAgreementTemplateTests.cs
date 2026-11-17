using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Services;
using HT.Agent.Infrastructure.Templates;
using W = DocumentFormat.OpenXml.Wordprocessing;
using W14 = DocumentFormat.OpenXml.Office2010.Word;

namespace HT.Agent.Tests;

/// <summary>技术协议这类「控件没设 Tag」的模板：上传时自动命名、可选可填、勾选框组、默认内容、文档属性绑定。
/// 夹具按真实模板的版式缩写：封面绑定字段、第三章设备参数表、第四章部件清单。</summary>
public class TechAgreementTemplateTests
{
    private const string CoreStore = "{6C3C8BC8-F283-45AE-878A-BAB7291924A1}";

    internal static byte[] AgreementDocx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            // 封面：公司名称、装置名称绑定文档属性；协议编号在单元格级控件里
            body.AppendChild(new Paragraph(new Run(new Text("甲方单位：")), Bound("keywords", "填写公司名称")));
            body.AppendChild(new Paragraph(Bound("title", "填写装置名称"), new Run(new Text("技术协议"))));
            body.AppendChild(new Table(new TableRow(
                Cell("协议编号："),
                new SdtCell(new SdtProperties(new ShowingPlaceholder()),
                    new SdtContentCell(Cell("单击此处输入文字。"))))));
            // 目录域
            body.AppendChild(new Paragraph(
                new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
                new Run(new FieldCode(" TOC \\o \"1-1\" \\h \\u ")),
                new Run(new FieldChar { FieldCharType = FieldCharValues.End })));

            body.AppendChild(Para("第一章 概述"));
            // 没设 Tag、但占位文字点名的公司名称 → 与封面共用槽位
            body.AppendChild(new Paragraph(new Run(new Text("根据“")), Plc("填写公司名称"), new Run(new Text("”的需求"))));
            // 写好默认内容（非占位状态）的控件：留空时保留原文
            body.AppendChild(new Paragraph(new Run(new Text("装置采用")), Real("西门子"), new Run(new Text("PLC控制系统。"))));

            body.AppendChild(Para("第三章 工艺流程"));
            body.AppendChild(Para("此表适用于带搅拌反应釜类，无内容处需输入“/”"));
            body.AppendChild(new Paragraph(new Run(new Text("表3-1  ")), Plc("填写位号"), new Run(new Text("设备参数表"))));
            body.AppendChild(new Table(
                new TableRow(Cell("（1）"), CellWith(new Run(new Text("位号：")), Plc("填写位号")), Cell("")),
                new TableRow(Cell("参数部位"), Cell("内筒"),
                    new TableCell(new Paragraph(Box(), new Run(new Text("夹套/")), Box(), new Run(new Text("盘管"))))),
                new TableRow(Cell("工作压力Mpa："), CellWith(Plc("填写工作压力")), CellWith(Plc("填写工作压力"))),
                new TableRow(Cell("材质："), CellWith(Combo("选择或填写", "304", "316L")), CellWith(Combo("选择或填写", "304", "316L"))),
                new TableRow(Cell("毒性危害程度："), CellWith(Drop("选择一项", "选择一项。", "高度危害", "轻度危害")), Cell(""))));

            body.AppendChild(Para("第四章 主要部件清单"));
            body.AppendChild(new Table(
                new TableRow(Cell("序号"), Cell("位号"), Cell("名称"), Cell("规格参数"), Cell("数量"), Cell("单位"), Cell("品牌")),
                new TableRow(Cell(""), Cell("R-"), CellWith(Plc("单击输入"), new Run(new Text("反应釜"))),
                    CellWith(new Run(new Text("内筒：工作压力")), Plc("单击输入"), new Run(new Text("MPa，材质：")),
                        Combo("选择/填写", "304", "316L"), new Run(new Text("；"))),
                    Cell("1"), Cell("台"), Cell("霍桐仪器"))));

            main.Document = new W.Document(body);
            doc.PackageProperties.Description = "高低压模块化装置";
        }
        return ms.ToArray();
    }

    private static Paragraph Para(string text) => new(new Run(new Text(text)));
    private static TableCell Cell(string text) => new(new Paragraph(new Run(new Text(text))));
    private static TableCell CellWith(params OpenXmlElement[] inline) => new(new Paragraph(inline));

    private static SdtRun Plc(string placeholder) => new(
        new SdtProperties(new ShowingPlaceholder()),
        new SdtContentRun(new Run(new RunProperties(new Color { Val = "808080" }), new Text(placeholder))));

    private static SdtRun Real(string content) => new(new SdtProperties(), new SdtContentRun(new Run(new Text(content))));

    private static SdtRun Bound(string prop, string placeholder)
    {
        var ns = prop is "keywords" ? "ns1" : "ns0";
        return new SdtRun(
            new SdtProperties(new ShowingPlaceholder(), new DataBinding
            {
                PrefixMappings = "xmlns:ns0='http://purl.org/dc/elements/1.1/' xmlns:ns1='http://schemas.openxmlformats.org/package/2006/metadata/core-properties' ",
                XPath = $"/ns1:coreProperties[1]/{ns}:{prop}[1]",
                StoreItemId = CoreStore
            }),
            new SdtContentRun(new Run(new Text(placeholder))));
    }

    private static SdtRun Combo(string placeholder, params string[] items)
    {
        var combo = new SdtContentComboBox();
        foreach (var i in items) combo.AppendChild(new ListItem { DisplayText = i, Value = i });
        return new SdtRun(new SdtProperties(new ShowingPlaceholder(), combo), new SdtContentRun(new Run(new Text(placeholder))));
    }

    private static SdtRun Drop(string placeholder, params string[] items)
    {
        var drop = new SdtContentDropDownList();
        foreach (var i in items) drop.AppendChild(new ListItem { DisplayText = i, Value = i });
        return new SdtRun(new SdtProperties(new ShowingPlaceholder(), drop), new SdtContentRun(new Run(new Text(placeholder))));
    }

    private static SdtRun Box() => new(
        new SdtProperties(new W14.SdtContentCheckBox(
            new W14.Checked { Val = W14.OnOffValues.Zero },
            new W14.CheckedState { Val = "0052", Font = "Wingdings 2" },
            new W14.UncheckedState { Val = "2610", Font = "MS Gothic" })),
        new SdtContentRun(new Run(new Text("☐"))));

    private static (TemplateNormalizer.Result Norm, TemplateSlotExtractor.ExtractResult Slots) Prepare()
    {
        var norm = TemplateNormalizer.Normalize(new MemoryStream(AgreementDocx()));
        return (norm, TemplateSlotExtractor.Extract(new MemoryStream(norm.Output)));
    }

    private static TemplateSlotExtractor.ExtractedSlot ByName(TemplateSlotExtractor.ExtractResult r, string section, string name)
        => r.Slots.Single(s => s.Section == section && s.Name == name);

    [Fact]
    public void 无Tag控件_按上下文自动命名_全部成槽位且无警告()
    {
        var (norm, r) = Prepare();
        Assert.True(norm.Tagged > 0);
        Assert.Empty(r.Warnings); // 目录不算槽位，也不报「无 Tag」

        // 封面绑定字段与正文里同占位文字的控件共用全局槽位
        var customer = r.Slots.Single(s => s.Tag == "customer_name");
        Assert.Equal("甲方单位名称", customer.Name);
        Assert.True(customer.ForbidInherit);
        Assert.Single(r.Slots, s => s.Tag == "device_name");
        Assert.Equal("协议编号", ByName(r, "封面", "协议编号").Name); // 单元格级控件取左侧行标题

        // 设备参数表：表题 + 表格算一个块，列名做分组，行标题里的单位带进名称
        const string t31 = "表3-1 带搅拌反应釜";
        var weihao = ByName(r, t31, "位号");
        Assert.StartsWith("t3_1.", weihao.Tag);
        Assert.Single(r.Slots, s => s.Section == t31 && s.Name == "位号"); // 表题与表头的「填写位号」是同一字段
        Assert.Equal(SlotDataType.Text, ByName(r, t31, "内筒·工作压力（MPa）").DataType);
        Assert.NotNull(ByName(r, t31, "夹套/盘管·工作压力（MPa）"));

        // 部件清单：每行一个块，块名取名称与位号；名称列按列名，规格参数按段内标签
        var part = r.Slots.Where(s => s.Tag.StartsWith("part01.")).ToList();
        Assert.All(part, s => Assert.Equal("第四章 主要部件清单 01 反应釜（R-）", s.Section));
        Assert.Contains(part, s => s.Name == "名称");
        var pressure = part.Single(s => s.Name == "内筒·工作压力");
        Assert.Equal("MPa", pressure.Unit);
    }

    [Fact]
    public void 组合框可选可填_下拉单选_提示项不进候选_勾选框组成单选()
    {
        var (_, r) = Prepare();
        const string t31 = "表3-1 带搅拌反应釜";
        var material = ByName(r, t31, "内筒·材质");
        Assert.Equal(SlotDataType.ChoiceOrText, material.DataType);
        Assert.Equal("[\"304\",\"316L\"]", material.Choices);

        var toxic = ByName(r, t31, "毒性危害程度");
        Assert.Equal(SlotDataType.SingleChoice, toxic.DataType);
        Assert.DoesNotContain("选择一项", toxic.Choices); // 「选择一项。」是提示，不是候选值

        var jacket = ByName(r, t31, "夹套或盘管");
        Assert.Equal(SlotDataType.SingleChoice, jacket.DataType);
        Assert.Equal("[\"夹套\",\"盘管\"]", jacket.Choices);
        Assert.False(jacket.Required);
    }

    [Fact]
    public void 模板里写好的默认内容不强制必填()
    {
        var (_, r) = Prepare();
        var plc = r.Slots.Single(s => s.Prompt == "西门子");
        Assert.False(plc.Required);
        Assert.True(ByName(r, "表3-1 带搅拌反应釜", "位号").Required);
    }

    [Fact]
    public void 已设Tag的模板原样返回()
    {
        var (norm, _) = Prepare();
        var again = TemplateNormalizer.Normalize(new MemoryStream(norm.Output));
        Assert.Equal(0, again.Tagged);
        Assert.Equal(norm.Output, again.Output);
    }

    [Fact]
    public async Task 回填_去掉占位状态_勾选对应框_默认内容保留_属性同步_目录更新()
    {
        var (norm, r) = Prepare();
        const string t31 = "表3-1 带搅拌反应釜";
        string Tag(string section, string name) => ByName(r, section, name).Tag;
        var plcTag = r.Slots.Single(s => s.Prompt == "西门子").Tag;

        var result = await DocxSlotFiller.FillAsync(new MemoryStream(norm.Output), new[]
        {
            new DocxSlotFiller.FillInput("customer_name", "华东理工大学", SlotDataType.Text),
            new DocxSlotFiller.FillInput("device_name", "尼龙66合成装置", SlotDataType.Text),
            new DocxSlotFiller.FillInput(Tag(t31, "夹套或盘管"), "盘管", SlotDataType.SingleChoice),
            new DocxSlotFiller.FillInput(Tag(t31, "内筒·材质"), "哈氏合金", SlotDataType.ChoiceOrText),
            new DocxSlotFiller.FillInput(Tag(t31, "内筒·工作压力（MPa）"), null, SlotDataType.Text),
            new DocxSlotFiller.FillInput(plcTag, null, SlotDataType.Text),
        });

        using var doc = WordprocessingDocument.Open(new MemoryStream(result.Output), false);
        var body = doc.MainDocumentPart!.Document.Body!;
        string TextOf(string tag) => string.Concat(body.Descendants<SdtElement>()
            .First(s => s.SdtProperties?.GetFirstChild<W.Tag>()?.Val?.Value == tag).Descendants<Text>().Select(t => t.Text));

        // 公司名称：封面与正文各处都填上，退出占位状态（不再发灰）
        var customers = body.Descendants<SdtElement>()
            .Where(s => s.SdtProperties?.GetFirstChild<W.Tag>()?.Val?.Value == "customer_name").ToList();
        Assert.Equal(2, customers.Count);
        Assert.All(customers, s =>
        {
            Assert.Equal("华东理工大学", string.Concat(s.Descendants<Text>().Select(t => t.Text)));
            Assert.Null(s.SdtProperties!.GetFirstChild<ShowingPlaceholder>());
            Assert.DoesNotContain(s.Descendants<Color>(), c => c.Val?.Value == "808080");
        });
        // 绑定的文档属性同步，Word 打开时不会被刷回
        Assert.Equal("华东理工大学", doc.PackageProperties.Keywords);
        Assert.Equal("尼龙66合成装置", doc.PackageProperties.Title);

        // 勾选框组：「盘管」那个框打勾，用 checkedState 的符号
        var boxes = body.Descendants<SdtElement>()
            .Where(s => s.SdtProperties?.GetFirstChild<W14.SdtContentCheckBox>() is not null).ToList();
        Assert.Equal(W14.OnOffValues.Zero, boxes[0].SdtProperties!.GetFirstChild<W14.SdtContentCheckBox>()!.GetFirstChild<W14.Checked>()!.Val!.Value);
        Assert.Equal(W14.OnOffValues.One, boxes[1].SdtProperties!.GetFirstChild<W14.SdtContentCheckBox>()!.GetFirstChild<W14.Checked>()!.Val!.Value);
        Assert.Equal("R", string.Concat(boxes[1].Descendants<Text>().Select(t => t.Text)));

        Assert.Equal("哈氏合金", TextOf(Tag(t31, "内筒·材质"))); // 候选项以外的值照填
        Assert.Equal("", TextOf(Tag(t31, "内筒·工作压力（MPa）"))); // 占位提示留空清掉
        Assert.Equal("西门子", TextOf(plcTag));                        // 写好的默认内容留空保留

        Assert.True(doc.MainDocumentPart!.DocumentSettingsPart!.Settings.GetFirstChild<UpdateFieldsOnOpen>()!.Val!.Value);
    }

    [Fact]
    public void 可选可填_候选项外的值照收_候选项按同义规整()
    {
        var def = new TemplateSlot
        {
            Tag = "t3_1.22", Name = "内筒·材质", Section = "表3-1",
            DataType = SlotDataType.ChoiceOrText, Choices = "[\"304\",\"316L\"]"
        };
        Assert.Equal((true, "哈氏合金", (string?)null), GenerationChatService.Validate(def, "哈氏合金"));
        Assert.Equal("316L", GenerationChatService.Validate(def, "316l").Value);

        var strict = new TemplateSlot
        {
            Tag = "t3_1.19", Name = "毒性危害程度", Section = "表3-1",
            DataType = SlotDataType.SingleChoice, Choices = "[\"高度危害\",\"轻度危害\"]"
        };
        Assert.False(GenerationChatService.Validate(strict, "随便").Ok);
    }
}
