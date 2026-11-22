using System.Text.Json;
using System.Text.Json.Nodes;
using HT.Agent.Application.Logic;
using HT.Agent.Infrastructure.Persistence;
using HT.Agent.Infrastructure.Services;

namespace HT.Agent.Tests;

/// <summary>国家目录（内置数据）和查找规则、反应热分级。</summary>
public class HazardTests
{
    static JsonElement Data(string name)
    {
        using var st = typeof(Seeder).Assembly.GetManifestResourceStream($"hazard/{name}.json")!;
        return JsonDocument.Parse(st).RootElement.Clone();
    }

    static List<HazardMatch.ProcessRow> Processes() => Data("processes").GetProperty("items").EnumerateArray()
        .Select(x => new HazardMatch.ProcessRow(Guid.NewGuid(), x.GetProperty("no").GetInt32(), x.GetProperty("name").GetString()!,
            x.GetProperty("typical").GetString()!, x.GetProperty("intro").GetString()!)).ToList();

    static readonly Lazy<List<HazardMatch.ChemRow>> Chems = new(() => Data("chemicals").GetProperty("items").EnumerateArray()
        .Select(x => new HazardMatch.ChemRow(Guid.NewGuid(), x.GetProperty("seq").GetInt32(), x.GetProperty("sub").GetBoolean(), x.GetProperty("name").GetString()!,
            string.Join("；", x.GetProperty("aliases").EnumerateArray().Select(a => a.GetString())),
            x.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String ? en.GetString() : null,
            x.TryGetProperty("cas", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null)).ToList());

    [Fact]
    public void Built_in_data_is_complete()
    {
        var p = Data("processes").GetProperty("items");
        Assert.Equal(18, p.GetArrayLength());
        var h = p.EnumerateArray().Single(x => x.GetProperty("name").GetString() == "加氢工艺");
        Assert.Contains("氢气紧急切断系统", h.GetProperty("requirements").GetString());
        Assert.Contains("循环氢压缩机停机报警和联锁", h.GetProperty("requirements").GetString());
        var c = Data("chemicals").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2828, c.Where(x => !x.GetProperty("sub").GetBoolean()).Select(x => x.GetProperty("seq").GetInt32()).Distinct().Count());
        var methanol = c.Single(x => x.GetProperty("name").GetString() == "甲醇");
        Assert.Equal("67-56-1", methanol.GetProperty("cas").GetString());
        Assert.Contains("易燃液体，类别2", methanol.GetProperty("classes").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("柴油", c.Single(x => x.GetProperty("seq").GetInt32() == 1674 && !x.GetProperty("sub").GetBoolean()).GetProperty("name").GetString());
        var pre = Data("precursors").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(74, pre.Count(x => x.GetProperty("list").GetString() == "yzb"));
        Assert.Equal(38, pre.Count(x => x.GetProperty("list").GetString() == "yzd"));
    }

    [Theory]
    [InlineData("催化加氢", "加氢工艺")]
    [InlineData("硝基苯加氢制苯胺", "加氢工艺")]
    [InlineData("乙烯聚合", "聚合工艺")]
    [InlineData("对甲苯磺化", "磺化工艺")]
    [InlineData("重氮化反应", "重氮化工艺")]
    [InlineData("蒽醌法生产过氧化氢", "过氧化工艺")]
    [InlineData("苯加氢生成环己烷", "加氢工艺")]
    public void Processes_match_by_name_or_typical(string q, string expected)
        => Assert.Equal(expected, HazardMatch.MatchProcesses(q, Processes()).First().Name);

    [Fact]
    public void Unrelated_process_matches_nothing()
        => Assert.Empty(HazardMatch.MatchProcesses("蒸馏提纯", Processes()));

    [Theory]
    [InlineData("氢气", "氢")]
    [InlineData("1333-74-0", "氢")]
    [InlineData("甲醇", "甲醇")]
    [InlineData("木精", "甲醇")]
    [InlineData("methanol", "甲醇")]
    [InlineData("液氨", "氨")]
    [InlineData("氧气", "氧[压缩的或液化的]")]
    public void Chemicals_match_by_name_alias_cas_english(string q, string expected)
        => Assert.Equal(expected, HazardMatch.FindChemical(q, Chems.Value).First(r => !r.Sub).Name);

    [Fact]
    public void Unknown_chemical_is_not_guessed()
    {
        Assert.Empty(HazardMatch.FindChemical("雷尼镍", Chems.Value));
        Assert.Empty(HazardMatch.FindChemical("水", Chems.Value));
    }

    [Fact]
    public void Generic_entry_does_not_drag_in_all_sub_items()
        => Assert.True(HazardMatch.FindChemical("醇酸树脂涂料", Chems.Value).Count <= 3);

    [Theory]
    [InlineData(150, 250, 300, 1)]
    [InlineData(150, 300, 250, 2)]
    [InlineData(260, 250, 300, 3)]
    [InlineData(320, 250, 300, 4)]
    [InlineData(373, 250, 210, 5)]
    [InlineData(220, 250, 210, 5)]
    public void Stoessel_classes(double mtsr, double mtt, double td24, int expected)
        => Assert.Equal(expected, ThermalRisk.Class(mtsr, mtt, td24));

    [Fact]
    public void Thermal_block_is_computed_by_the_system()
    {
        var d = JsonNode.Parse("""{"dH":-250,"conc":2,"density":0.79,"cp":2.5,"Tp":120,"TD24":210,"MTT":250,"accumulation":20}""")!.AsObject();
        var checks = new List<string>();
        AssistantService.ChemChecks("thermal", d, checks);
        var c = d["computed"]!;
        Assert.Equal(253.2, c["dTad"]!.GetValue<double>(), 1);
        Assert.Equal(5, c["batch"]!["class"]!.GetValue<int>());
        Assert.Equal(170.6, c["semi"]!["mtsr"]!.GetValue<double>(), 1);
        Assert.Equal(2, c["semi"]!["class"]!.GetValue<int>());
        Assert.Empty(checks);

        var noTd = JsonNode.Parse("""{"q":500,"cp":2,"Tp":80,"MTT":120}""")!.AsObject();
        AssistantService.ChemChecks("thermal", noTd, checks);
        Assert.Equal(330, noTd["computed"]!["batch"]!["mtsr"]!.GetValue<double>());
        Assert.Null(noTd["computed"]!["batch"]!["class"]);
        Assert.Contains(checks, x => x.Contains("TD24"));
    }
}
