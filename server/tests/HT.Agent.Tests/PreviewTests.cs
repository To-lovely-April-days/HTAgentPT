using System.Text.Json.Nodes;
using HT.Agent.Application.Logic;
using HT.Agent.Infrastructure.Services;

namespace HT.Agent.Tests;

public class ChemMathTests
{
    [Theory]
    [InlineData("H2O", "H2O", 18.015)]
    [InlineData("H2SO4", "H2O4S", 98.072)]
    [InlineData("Ca(OH)2", "CaH2O2", 74.092)]
    [InlineData("CuSO4·5H2O", "CuH10O9S", 249.67)]
    [InlineData("C₆H₁₂O₆", "C6H12O6", 180.156)]
    [InlineData("SO4^2-", "O4S", 96.056)]
    public void Formulas_are_split_into_elements(string formula, string hill, double mass)
    {
        var c = ChemMath.ParseFormula(formula);
        Assert.NotNull(c);
        Assert.Equal(hill, ChemMath.Hill(c!));
        Assert.Equal(mass, ChemMath.Mass(c!), 1);
    }

    [Theory]
    [InlineData("Q235")]
    [InlineData("h2o")]
    [InlineData("Ca(OH")]
    [InlineData("")]
    public void Bad_formulas_are_rejected(string formula) => Assert.Null(ChemMath.ParseFormula(formula));

    [Theory]
    [InlineData("CCO", "C2H6O", 46.07)]                         // 乙醇
    [InlineData("c1ccccc1", "C6H6", 78.11)]                      // 苯
    [InlineData("CC(=O)Oc1ccccc1C(=O)O", "C9H8O4", 180.16)]      // 阿司匹林
    [InlineData("c1ccncc1", "C5H5N", 79.10)]                     // 吡啶
    [InlineData("c1ccsc1", "C4H4S", 84.14)]                      // 噻吩
    [InlineData("c1cc[nH]c1", "C4H5N", 67.09)]                   // 吡咯
    [InlineData("[Na+].[Cl-]", "ClNa", 58.44)]
    [InlineData("C1CCCCC1", "C6H12", 84.16)]                     // 环己烷
    [InlineData("OS(=O)(=O)O", "H2O4S", 98.07)]                  // 硫酸
    [InlineData("C#N", "CHN", 27.03)]
    [InlineData("ClC(Cl)Cl", "CHCl3", 119.37)]
    public void Smiles_give_formula_and_weight(string smiles, string formula, double weight)
    {
        var r = ChemMath.FromSmiles(smiles);
        Assert.Equal(formula, r.Formula);
        Assert.Equal(weight, r.Weight, 1);
    }

    [Theory]
    [InlineData("C1CCC")]
    [InlineData("CC(C")]
    [InlineData("CC)C")]
    [InlineData("CXC")]
    public void Broken_smiles_say_what_is_wrong(string smiles)
        => Assert.Throws<FormatException>(() => ChemMath.FromSmiles(smiles));

    [Fact]
    public void Balanced_reaction_has_no_difference()
    {
        var (diff, err) = ChemMath.Balance([new(1, "N2"), new(3, "H2")], [new(2, "NH3")]);
        Assert.Null(err);
        Assert.Empty(diff!);
    }

    [Fact]
    public void Unbalanced_reaction_names_the_element()
    {
        var (diff, _) = ChemMath.Balance([new(1, "N2"), new(1, "H2")], [new(2, "NH3")]);
        Assert.Equal(-4, diff!["H"]);
        Assert.False(diff.ContainsKey("N"));
    }

    [Fact]
    public void Mass_balance_checks_each_unit_and_the_whole()
    {
        var streams = new List<ChemMath.Stream>
        {
            new("1", null, "R-101", 1000), new("2", "R-101", "T-101", 1000),
            new("3", "T-101", null, 600), new("4", "T-101", null, 380),
        };
        var (units, overall) = ChemMath.MassBalance(streams);
        Assert.True(units.Single(u => u.Unit == "R-101").Ok);
        Assert.False(units.Single(u => u.Unit == "T-101").Ok);
        Assert.False(overall!.Ok);
    }

    [Fact]
    public void Show_block_writes_computed_values_and_reports_problems()
    {
        var mol = new JsonObject { ["smiles"] = "CCO" };
        var checks = new List<string>();
        AssistantService.ChemChecks("molecule", mol, checks);
        Assert.Equal("C2H6O", mol["formula"]!.ToString());
        Assert.Empty(checks);

        var rx = JsonNode.Parse("""{"reactants":[{"coef":1,"formula":"CH4"},{"coef":1,"formula":"O2"}],"products":[{"coef":1,"formula":"CO2"},{"coef":2,"formula":"H2O"}]}""")!.AsObject();
        AssistantService.ChemChecks("reaction", rx, checks);
        Assert.False(rx["balance"]!["ok"]!.GetValue<bool>());
        Assert.Contains(checks, c => c.Contains("O"));
    }
}

public class WebReaderTests
{
    private static readonly Uri Base = new("https://example.org/news/a.html");

    [Fact]
    public void Picks_the_article_and_drops_chrome()
    {
        var html = """
            <html><head><title>镍基合金对比 - 腐蚀网</title><meta property="og:site_name" content="腐蚀网"></head>
            <body><nav><a href="/">首页</a><a href="/x">栏目</a></nav>
            <div class="sidebar">热门推荐 很多很多链接文字 很多很多链接文字</div>
            <div class="article-content">
              <h1>镍基合金对比</h1>
              <p>盐酸是最难对付的还原性酸之一，大多数不锈钢在其中都会快速腐蚀，这里对比几种常用镍基合金的表现。</p>
              <p>C276 在稀盐酸、温度不超过 60 ℃ 时腐蚀速率低于 0.1 mm/a，属于耐蚀级别。<script>alert(1)</script></p>
              <h2>腐蚀速率</h2>
              <table><tr><th>合金</th><th>5% 60℃</th></tr><tr><td>C276</td><td>0.08</td></tr></table>
              <img src="/img/chart.png" alt="速率图">
              <img src="/pixel.gif" width="1" height="1">
              <ul><li>B-3 更耐浓盐酸</li><li>钽材最贵</li></ul>
              <div style="display:none">隐藏的广告</div>
            </div>
            <footer>版权所有</footer></body></html>
            """;
        var a = WebReader.Read(html, Base);
        Assert.Equal("镍基合金对比", a.Title);
        Assert.Equal("腐蚀网", a.Site);
        Assert.DoesNotContain(a.Blocks, b => (b.Text ?? "").Contains("首页") || (b.Text ?? "").Contains("版权") || (b.Text ?? "").Contains("alert") || (b.Text ?? "").Contains("隐藏"));
        Assert.Contains(a.Blocks, b => b.Type == "heading" && b.Text == "腐蚀速率");
        var table = Assert.Single(a.Blocks, b => b.Type == "table");
        Assert.Equal(["C276", "0.08"], table.Rows![1]);
        var img = Assert.Single(a.Blocks, b => b.Type == "image");
        Assert.Equal("https://example.org/img/chart.png", img.Src);
        Assert.Equal(2, a.Blocks.Count(b => b.Type == "li"));
        Assert.Contains("| 合金 | 5% 60℃ |", WebReader.ToMarkdown(a, Base.ToString()));
    }

    [Fact]
    public void Falls_back_to_body_when_nothing_looks_like_an_article()
    {
        var a = WebReader.Read("<html><body><p>只有一段话，没有正文容器。</p></body></html>", Base);
        Assert.Equal("example.org", a.Title);
        Assert.Single(a.Blocks);
    }
}
