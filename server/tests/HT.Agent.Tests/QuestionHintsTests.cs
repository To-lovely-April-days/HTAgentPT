using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class QuestionHintsTests
{
    private static readonly TermEntry[] Terms =
    [
        new("客户", "华东理工大学", ["华东理工", "ECUST"]),
        new("客户", "华南理工大学", []),
        new("客户", "北京化工大学", []),
        new("常用说法", "哈氏合金 C276", QuestionHints.SplitAliases("哈氏、HC276、C-276")),
        new("常用说法", "聚丙烯", QuestionHints.SplitAliases("PP")),
        new("设备", "反应釜", QuestionHints.SplitAliases("高压釜、釜")),
    ];

    [Fact]
    public void Aliases_map_to_standard_terms_and_skip_when_standard_is_written()
    {
        var hits = QuestionHints.AliasHits("那台哈氏的高压釜密封多久换", Terms);
        Assert.Contains(hits, h => h.Said == "哈氏" && h.Term.Value == "哈氏合金 C276");
        Assert.Contains(hits, h => h.Said == "高压釜" && h.Term.Value == "反应釜");
        Assert.DoesNotContain(QuestionHints.AliasHits("反应釜的密封", Terms), h => h.Term.Value == "反应釜");
        // 英文别名要整词：PPL 不算 PP
        Assert.DoesNotContain(QuestionHints.AliasHits("PPL 棒多少钱", Terms), h => h.Term.Value == "聚丙烯");
        Assert.Contains(QuestionHints.AliasHits("PP 棒多少钱", Terms), h => h.Term.Value == "聚丙烯");
    }

    [Fact]
    public void Customer_abbreviations_are_suggested_with_all_candidates()
    {
        var ab = QuestionHints.Abbreviations("华理那台釜什么时候发货", Terms.Where(t => t.Kind == "客户"));
        var hit = Assert.Single(ab, a => a.Said == "华理");
        Assert.Equal(2, hit.Names.Count); // 华东理工、华南理工都可能
        Assert.Contains(QuestionHints.Abbreviations("北化的项目", Terms.Where(t => t.Kind == "客户")), a => a.Said == "北化" && a.Names.SequenceEqual(["北京化工大学"]));
        // 写了全称或已登记别名就不再猜
        Assert.Empty(QuestionHints.Abbreviations("华东理工大学和华南理工大学的项目", Terms.Where(t => t.Kind == "客户")));
    }

    [Fact]
    public void Colloquial_pressure_units_get_a_hint()
    {
        var u = Assert.Single(QuestionHints.UnitHints("16 公斤的釜能做 200 度的氢化吗"));
        Assert.Contains("1.57 MPa", u);
        Assert.Empty(QuestionHints.UnitHints("设计压力 1.6 MPa"));
    }

    [Fact]
    public void Build_and_expand()
    {
        var text = QuestionHints.Build("北化那台哈氏釜，16公斤够不够", Terms);
        Assert.Contains("「哈氏」= 哈氏合金 C276", text);
        Assert.Contains("北京化工大学", text);
        Assert.Contains("kgf/cm²", text);
        Assert.Equal("", QuestionHints.Build("你好", Terms));
        var q = QuestionHints.Expand("北化 哈氏釜 密封", Terms);
        Assert.Contains("哈氏合金 C276", q);
        Assert.Contains("北京化工大学", q);
    }
}
