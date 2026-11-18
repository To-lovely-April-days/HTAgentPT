using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class PriceSheetTests
{
    private static SheetRow R(int no, params string[] cells) => new(no, cells);

    [Fact]
    public void Two_row_supplier_header_is_unpivoted_one_offer_per_supplier()
    {
        var g = new SheetGrid("Sheet1",
        [
            R(1, "物料代码", "物料名称", "规格", "OSK", "", "析钛", "", "世伟洛克"),
            R(2, "", "", "", "单价", "型号", "单价", "型号", ""),
            R(3, "08.01.12.0200", "卡套直通接头", "1/4卡套直通，材质：316L", "30", "SS-SF-F4", "26", "SS-U-K4F", "48"),
            R(4, "08.01.12.0300", "卡套直通接头", "3/8卡套直通，材质：316L", "", "", "41", "SS-U-K6F", ""),
        ],
        [new CellRange(1, 0, 2, 0), new CellRange(1, 1, 2, 1), new CellRange(1, 2, 2, 2), new CellRange(1, 3, 1, 4), new CellRange(1, 5, 1, 6), new CellRange(1, 7, 2, 7)]);
        var (grid, map, di, header) = PriceSheet.Guess(g);
        Assert.Equal(1, header);
        Assert.Contains(map, m => m.Field == "price" && m.Supplier == "OSK");
        Assert.Contains(map, m => m.Field == "model" && m.Supplier == "析钛");
        Assert.Contains(map, m => m.Field == "price" && m.Supplier == "世伟洛克");
        var (items, issues) = PriceSheet.Extract(grid, map, di, "标准品");
        Assert.Equal(4, items.Count); // 第 3 行三家，第 4 行只有析钛
        var xt = items.Single(i => i.RowNo == 3 && i.Supplier == "析钛");
        Assert.Equal(26m, xt.TaxLow);
        Assert.Equal("SS-U-K4F", xt.Model);
        Assert.Equal("316L", xt.Material);
        Assert.Equal("08.01.12.0200", xt.Code);
        Assert.Single(items, i => i.RowNo == 4);
        Assert.Empty(issues);
    }

    [Fact]
    public void Merged_price_unit_fills_down_and_conflicts_ranges_and_derived_tax_are_reported()
    {
        var g = new SheetGrid("Sheet1",
        [
            R(1, "序号", "产品名称", "产品规格（外径）", "单位", "含税单价（元）", "未税单价（元）", "", "备注"),
            R(3, "1", "316L棒", "Ф8", "米", "26.1", "23.1", "元/kg", ""),
            R(4, "2", "316L管", "φ158*5", "米", "36-40", "", "", "管径薄价格高"),
            R(5, "3", "铝型材", "80*80", "米", "", "693.81", "", ""),
            R(6, "4", "C276棒", "φ30", "米", "无", "", "", ""),
        ],
        [new CellRange(3, 6, 5, 6)]);
        var (grid, map, di, _) = PriceSheet.Guess(g);
        Assert.Equal("price_unit", map[6].Field);
        var (items, issues) = PriceSheet.Extract(grid, map, di, "原材料");
        Assert.Equal("元/kg", items[1].PriceUnit); // 合并单元格往下补
        Assert.Equal((36m, 40m), (items[1].TaxLow!.Value, items[1].TaxHigh!.Value));
        Assert.True(items[2].TaxDerived);
        Assert.Equal(Math.Round(693.81m * 1.13m, 2), items[2].TaxLow);
        Assert.Null(items[3].TaxLow);
        Assert.Equal("元/米", items[3].PriceUnit); // 没写计价单位：按数量单位
        Assert.Contains(issues, i => i.Kind == "unit" && i.Count == 3);
        Assert.Contains(issues, i => i.Kind == "range" && i.Rows.SequenceEqual([4]));
        Assert.Contains(issues, i => i.Kind == "derived");
        Assert.Contains(issues, i => i.Kind == "missing" && i.Rows.SequenceEqual([6]));
    }

    [Fact]
    public void Price_column_without_header_is_found_by_its_numbers_and_name_falls_back_to_table()
    {
        var g = new SheetGrid("Sheet1",
        [
            R(1, "序号", "名称", "描述", "", ""),
            R(2, "1", "热电偶", "长度300mm直径1/2", "", ""),
            R(3, "2", "热电偶(标准常用)", "长度121mm直径1mm", "", "55"),
            R(4, "3", "热电偶(标准常用)", "长度137mm直径1mm", "", "60"),
        ], []);
        var (grid, map, di, _) = PriceSheet.Guess(g);
        Assert.Equal("price", map[4].Field);
        var (items, issues) = PriceSheet.Extract(grid, map, di, "热电偶");
        Assert.Equal(3, items.Count);
        Assert.Equal(55m, items[1].TaxLow);
        Assert.Contains(issues, i => i.Kind == "noheader");
        Assert.Contains(issues, i => i.Kind == "missing" && i.Count == 1);

        var noName = new SheetGrid("球阀类", [R(1, "分组名称", "品牌", "接口尺寸", "型号", "单价(元)"), R(2, "1/4英寸", "世奥", "1/4英寸", "SS-2BV3-F4", "190")], []);
        var (g2, m2, d2, _) = PriceSheet.Guess(noName);
        var (i2, _) = PriceSheet.Extract(g2, m2, d2, "球阀");
        Assert.Equal("球阀", i2[0].Name);
        Assert.Equal("世奥", i2[0].Supplier);
        Assert.Equal("1/4英寸", i2[0].Group);
    }

    [Theory]
    [InlineData("26.1", 26.1, 26.1)]
    [InlineData("¥1,120", 1120, 1120)]
    [InlineData("36-40", 36, 40)]
    [InlineData("40~36", 36, 40)]
    [InlineData("约 19 元", 19, 19)]
    public void Prices_parse(string text, double low, double high)
    {
        var (l, h) = PriceSheet.ParsePrice(text);
        Assert.Equal((decimal)low, l);
        Assert.Equal((decimal)high, h);
    }

    [Theory]
    [InlineData("无")]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("面议")]
    public void Non_prices_are_empty(string text) => Assert.Null(PriceSheet.ParsePrice(text).Low);

    [Fact]
    public void Diff_counts_up_down_added_removed()
    {
        var old = new[] { ("a", "A", (string?)null, (string?)null, (decimal?)100m), ("b", "B", null, null, 50m), ("c", "C", null, null, 10m) };
        var now = new[] { ("a", "A", (string?)null, (string?)null, (decimal?)110m), ("b", "B", null, null, 40m), ("d", "D", null, null, 5m) };
        var d = PriceSheet.Diff(old, now);
        Assert.Equal((1, 1, 1, 1), (d.Up, d.Down, d.Added, d.Removed));
        Assert.Equal(-20, d.Top[0].Pct);
    }

    [Fact]
    public void Version_date_is_read_from_file_name()
    {
        Assert.Equal(new DateOnly(2024, 4, 9), PriceSheet.DateInName("原材料单价表-HT20240409.xlsx"));
        Assert.Equal(new DateOnly(2026, 11, 18), PriceSheet.DateInName("球阀 2026-11-18.xlsx"));
        Assert.Null(PriceSheet.DateInName("球阀.xlsx"));
    }

    [Fact]
    public void Cost_lines_convert_units_and_refuse_mismatches()
    {
        var kg = CostMath.Line(new("C276 板", "δ12", 86, "kg", 410, 410, "元/kg"));
        Assert.Equal(("ok", 35260m), (kg.Status, kg.SubLow!.Value));
        var range = CostMath.Line(new("C276 棒", "φ60", 14, "公斤", 380, 420, "元/kg"));
        Assert.Equal((5320m, 5880m), (range.SubLow!.Value, range.SubHigh!.Value));
        var perFive = CostMath.Line(new("焊丝", null, 10, "kg", 300, 300, "元/5kg"));
        Assert.Equal(600m, perFive.SubLow);
        var mm = CostMath.Line(new("TA2 棒", "φ35", 0.5m, "米", 0.2m, 0.2m, "元/mm"));
        Assert.Equal(100m, mm.SubLow);
        Assert.Equal("unit", CostMath.Line(new("316L 管", null, 3, "米", 26, 26, "元/kg")).Status);
        Assert.Equal("missing", CostMath.Line(new("搅拌器", null, 1, "套", null, null, null)).Status);
        Assert.Equal("ok", CostMath.Line(new("热电偶", null, 1, "支", 70, 70, null)).Status);
    }
}
