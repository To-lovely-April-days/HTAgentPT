using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Services;
using HT.Agent.Infrastructure.Templates;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace HT.Agent.Tests;

/// <summary>按设备展开（技术协议第三、四章）：设备清单 → 槽位实例、配套行、Word 里复制删除与重编表号。
/// 夹具沿用 <see cref="TechAgreementTemplateTests"/> 的缩写版技术协议：表3-1 带搅拌反应釜 + 部件清单一行「反应釜（R-）」。</summary>
public class EquipmentExpansionTests
{
    private static (byte[] Docx, List<TemplateSlot> Slots, List<EquipmentCatalogEntry> Catalog) Prepare()
    {
        var norm = TemplateNormalizer.Normalize(new MemoryStream(TechAgreementTemplateTests.AgreementDocx()));
        var slots = TemplateSlotExtractor.Extract(new MemoryStream(norm.Output)).Slots
            .Select(s => new TemplateSlot
            {
                Tag = s.Tag, Name = s.Name ?? s.Tag, Section = s.Section ?? "", DataType = s.DataType,
                Choices = s.Choices, Required = s.Required, Prompt = s.Prompt, Unit = s.Unit, SortOrder = s.SortOrder
            }).ToList();
        return (norm.Output, slots, EquipmentBlocks.Catalog(slots, EquipmentDocExpander.RowTexts(norm.Output)));
    }

    private static List<EquipmentItem> TwoReactors(List<EquipmentCatalogEntry> catalog)
        => EquipmentBlocks.Normalize(
            [new(null, "t3_1", "R-101", "聚合反应釜"), new(null, "t3_1", "R-102", "预聚反应釜")], null, catalog);

    [Fact]
    public void 目录_参数表与部件行各一项()
    {
        var (_, _, catalog) = Prepare();
        var table = catalog.Single(c => c.Kind == "table");
        Assert.Equal("t3_1", table.Block);
        Assert.Equal("带搅拌反应釜", table.Name);
        var part = catalog.Single(c => c.Kind == "part");
        Assert.Equal("反应釜", part.Name);
        Assert.Equal("R-", part.TagPrefix);
    }

    [Fact]
    public void 清单整理_编号_自动配第四章行_删表连带删配套行_编号不复用()
    {
        var (_, _, catalog) = Prepare();
        var items = TwoReactors(catalog);
        Assert.Equal(4, items.Count); // 两张表 + 各配一行
        var r101 = items.Single(i => i.Block == "t3_1" && i.TagNo == "R-101");
        var companion = items.Single(i => i.LinkedTo == r101.Id);
        Assert.Equal("part01", companion.Block);
        Assert.Equal(("R-101", "聚合反应釜"), (companion.TagNo, companion.Name));

        // 改表的位号，配套行跟着改；删掉 R-102，它的配套行一起没
        var edited = items.Where(i => i.TagNo != "R-102" || i.LinkedTo is not null)
            .Select(i => i.Id == r101.Id ? i with { TagNo = "R-201" } : i).ToList();
        var again = EquipmentBlocks.Normalize(edited, items, catalog);
        Assert.Equal(2, again.Count);
        Assert.Equal("R-201", again.Single(i => i.LinkedTo == r101.Id).TagNo);

        // 新加的设备（连同配套行）不接手已删设备的编号——对话状态按 Tag 记，复用会串
        var added = EquipmentBlocks.Normalize([.. again, new(null, "t3_1", "R-301", "新釜")], again, catalog);
        var oldIds = items.Select(i => i.Id).ToHashSet();
        Assert.All(added.Where(i => i.TagNo == "R-301"), i => Assert.DoesNotContain(i.Id, oldIds));
    }

    [Fact]
    public void 槽位展开_每台一份_表号连续_没定清单时块不出现_配套行同名参数不再问()
    {
        var (_, slots, catalog) = Prepare();
        var undecided = EquipmentBlocks.Expand(slots, []);
        Assert.DoesNotContain(undecided, s => EquipmentBlocks.Parse(s.Tag) is not null);
        Assert.Contains(undecided, s => s.Tag == "customer_name");

        var items = TwoReactors(catalog);
        var expanded = EquipmentBlocks.Expand(slots, items);
        var r101 = items.Single(i => i.TagNo == "R-101" && i.LinkedTo is null);
        var r102 = items.Single(i => i.TagNo == "R-102" && i.LinkedTo is null);
        var first = expanded.Where(s => s.Tag.StartsWith($"t3_1#{r101.Id}.")).ToList();
        Assert.NotEmpty(first);
        Assert.All(first, s => Assert.Equal("表3-1 带搅拌反应釜 · R-101 聚合反应釜", s.Section));
        Assert.All(expanded.Where(s => s.Tag.StartsWith($"t3_1#{r102.Id}.")),
            s => Assert.StartsWith("表3-2 带搅拌反应釜 · R-102", s.Section));
        Assert.DoesNotContain(expanded, s => EquipmentBlocks.Parse(s.Tag) is { Instance: null });

        // 配套行的「内筒·工作压力」「内筒·材质」由参数表推出：不展开成槽位，生成时取表里的值
        var links = EquipmentBlocks.DerivedLinks(slots, items);
        var companion = items.Single(i => i.LinkedTo == r101.Id);
        string Name(string tag) => slots.Single(s => s.Tag == $"{EquipmentBlocks.Parse(tag)!.Value.Block}.{EquipmentBlocks.Parse(tag)!.Value.Local}").Name;
        var mine = links.Where(l => l.RowTag.StartsWith($"part01#{companion.Id}.")).ToList();
        Assert.Contains(mine, l => Name(l.RowTag) == "内筒·工作压力" && Name(l.TableTag) == "内筒·工作压力（MPa）");
        Assert.Contains(mine, l => Name(l.RowTag) == "内筒·材质" && Name(l.TableTag) == "内筒·材质");
        Assert.All(mine, l => Assert.DoesNotContain(expanded, s => s.Tag == l.RowTag));

        // 位号就是清单上写的
        var ids = EquipmentBlocks.IdentityValues(expanded, items);
        Assert.Contains(ids, x => x.Tag.StartsWith($"t3_1#{r101.Id}.") && x.Value == "R-101");
    }

    [Fact]
    public void Apply_同一模板对象可按新清单反复展开()
    {
        var (_, slots, catalog) = Prepare();
        var template = new Template { Name = "技术协议", DocType = "方案", FileKey = "k", Slots = slots };
        EquipmentBlocks.Apply(template, null);
        var baseCount = template.Slots.Count;
        EquipmentBlocks.Apply(template, TwoReactors(catalog));
        Assert.True(template.Slots.Count > baseCount);
        EquipmentBlocks.Apply(template, []);
        Assert.Equal(baseCount, template.Slots.Count);
        Assert.NotNull(EquipmentBlocks.OriginalSlots(template));
    }

    [Fact]
    public async Task Word展开_逐台复制_重编表号_说明段去掉_位号数量写进清单行_回填按实例Tag()
    {
        var (docx, slots, catalog) = Prepare();
        var items = TwoReactors(catalog);
        var r101 = items.Single(i => i.TagNo == "R-101" && i.LinkedTo is null);
        var expanded = EquipmentDocExpander.Expand(docx, items);

        var pressure = EquipmentBlocks.Expand(slots, items)
            .Single(s => s.Tag.StartsWith($"t3_1#{r101.Id}.") && s.Name == "内筒·工作压力（MPa）").Tag;
        var inputs = new List<DocxSlotFiller.FillInput> { new(pressure, "3.0", SlotDataType.Text) };
        foreach (var l in EquipmentBlocks.DerivedLinks(slots, items))
            inputs.Add(new(l.RowTag, l.TableTag == pressure ? "3.0" : null, SlotDataType.Text));
        var filled = await DocxSlotFiller.FillAsync(new MemoryStream(expanded), inputs);

        using var doc = WordprocessingDocument.Open(new MemoryStream(filled.Output), false);
        var body = doc.MainDocumentPart!.Document.Body!;
        var text = body.InnerText;
        Assert.DoesNotContain("此表适用于", text);
        Assert.Contains("表3-1", text);
        Assert.Contains("表3-2", text);

        var tags = body.Descendants<SdtElement>().Select(s => s.SdtProperties?.GetFirstChild<W.Tag>()?.Val?.Value).ToList();
        Assert.DoesNotContain(tags, t => t is not null && EquipmentBlocks.Parse(t) is { Instance: null }); // 原块已删
        Assert.Contains(tags, t => t == pressure);

        // 清单表：两行反应釜，位号、数量按清单写；配套行的工作压力取自参数表
        var listTable = body.Elements<Table>().Last();
        var rows = listTable.Elements<TableRow>().Skip(1).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("R-101", rows[0].Elements<TableCell>().ElementAt(1).InnerText);
        Assert.Equal("R-102", rows[1].Elements<TableCell>().ElementAt(1).InnerText);
        Assert.Contains("3.0", rows[0].Elements<TableCell>().ElementAt(3).InnerText);
        Assert.DoesNotContain("反应釜反应釜", rows[0].InnerText); // 名称列固定的「反应釜」不重复
    }

    [Fact]
    public void 未选的块整块删掉()
    {
        var (docx, _, _) = Prepare();
        var expanded = EquipmentDocExpander.Expand(docx, []);
        using var doc = WordprocessingDocument.Open(new MemoryStream(expanded), false);
        var body = doc.MainDocumentPart!.Document.Body!;
        Assert.DoesNotContain("表3-1", body.InnerText);
        Assert.Single(body.Elements<Table>().Last().Elements<TableRow>()); // 清单表只剩表头
    }

    [Fact]
    public void 派工单_设备动作_增删解析()
    {
        var plan = GenChatLogic.ParsePlan(
            "{\"actions\":[{\"type\":\"equipment\",\"items\":[{\"kind\":\"t3_1\",\"tagNo\":\"R-101\",\"name\":\"聚合反应釜\"}," +
            "{\"kind\":\"压力表\",\"qty\":\"5\"}],\"remove\":[\"E-101\"]}]}");
        var a = Assert.Single(plan);
        Assert.Equal("equipment", a.Type);
        Assert.Equal(2, a.Items!.Count);
        Assert.Equal(5, a.Items[1].Qty);
        Assert.Equal(["E-101"], a.Tags);
    }

    [Fact]
    public void 设备类别按编号或名称对回目录()
    {
        var (_, _, catalog) = Prepare();
        Assert.Equal("t3_1", GenerationChatService.ResolveBlock(catalog, "t3_1"));
        Assert.Equal("t3_1", GenerationChatService.ResolveBlock(catalog, "带搅拌反应釜"));
        Assert.Equal("part01", GenerationChatService.ResolveBlock(catalog, "反应釜"));
        Assert.Null(GenerationChatService.ResolveBlock(catalog, "离心机"));
    }
}
