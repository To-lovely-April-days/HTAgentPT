using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class SkillFilesTests
{
    [Fact]
    public void ParsesFrontmatterAndBody()
    {
        const string md = "---\nname: reaction-balancing\ndescription: >\n  Balance chemical equations\n  and check atoms.\nlicense: Apache-2.0\n---\n\n# Body\nUse `python` to run.\n";
        var p = SkillFiles.Parse(md);
        Assert.Equal("reaction-balancing", p.Name);
        Assert.Equal("Balance chemical equations and check atoms.", p.Description);
        Assert.Equal("Apache-2.0", p.License);
        Assert.StartsWith("# Body", p.Body);
    }

    [Fact]
    public void NoFrontmatterIsAllBody()
    {
        var p = SkillFiles.Parse("# 只有正文\n内容");
        Assert.Equal("", p.Name);
        Assert.Null(p.License);
        Assert.Contains("内容", p.Body);
    }

    [Fact]
    public void WriteRoundTrips()
    {
        var text = SkillFiles.Write("material-selection", "客户问选材时用", "# 选材\n先确认介质", "MIT");
        var p = SkillFiles.Parse(text);
        Assert.Equal("material-selection", p.Name);
        Assert.Equal("客户问选材时用", p.Description);
        Assert.Equal("MIT", p.License);
        Assert.Equal("# 选材\n先确认介质", p.Body);
    }

    [Theory]
    [InlineData("Apache-2.0", true)]
    [InlineData("MIT", true)]
    [InlineData("BSD-3-Clause", true)]
    [InlineData("Proprietary. LICENSE.txt has complete terms", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void ChecksLicense(string? lic, bool ok) => Assert.Equal(ok, SkillFiles.LicenseOk(lic));

    [Fact]
    public void FlagsUnknownToolNames()
    {
        var unknown = SkillFiles.UnknownTools("先用 search_documents 查，再用 web_fetch 打开；用 `bash` 跑脚本。文件 references/data_table.md 不算。");
        Assert.Contains("web_fetch", unknown);
        Assert.Contains("bash", unknown);
        Assert.DoesNotContain("search_documents", unknown);
        Assert.DoesNotContain("data_table", unknown);
    }

    [Theory]
    [InlineData("Material Selection", "material-selection")]
    [InlineData("  Reaction_Balancing!! ", "reaction-balancing")]
    [InlineData("选材", "")]
    public void NormalizesCode(string input, string code) => Assert.Equal(code, SkillFiles.NormalizeCode(input));
}

public class SkillSeedTests
{
    private static List<(string Name, SkillFiles.Parsed P)> All()
    {
        var asm = typeof(HT.Agent.Infrastructure.Persistence.AppDbContext).Assembly;
        return asm.GetManifestResourceNames().Where(n => n.StartsWith("skills/")).Select(n =>
        {
            using var r = new StreamReader(asm.GetManifestResourceStream(n)!);
            return (n, SkillFiles.Parse(r.ReadToEnd()));
        }).ToList();
    }

    [Fact]
    public void AllSeedSkillsAreWellFormed()
    {
        var all = All();
        Assert.Equal(35, all.Count);
        foreach (var (n, p) in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name), n);
            Assert.Equal(p.Name, SkillFiles.NormalizeCode(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Description), n);
            Assert.True(p.Meta.ContainsKey("title") && p.Meta.ContainsKey("category"), n);
            Assert.True(SkillFiles.Length(p.Body) <= SkillFiles.MaxBody, n);
        }
        Assert.Equal(35, all.Select(a => a.P.Name).Distinct().Count());
    }

    [Fact]
    public void AllSeedSkillsAreFinished()
    {
        foreach (var (n, p) in All())
        {
            Assert.Equal("enabled", p.Meta.GetValueOrDefault("status"));
            Assert.DoesNotContain("[待写]", p.Body);
            Assert.DoesNotContain("[待填]", p.Body);
            Assert.True(SkillFiles.UnknownTools(p.Body).Count == 0, n + "：" + string.Join("、", SkillFiles.UnknownTools(p.Body)));
            Assert.Contains("## 先确认", p.Body);
            Assert.Contains("## 回答怎么写", p.Body);
            Assert.Contains("## 该查什么", p.Body);
        }
    }

    [Fact]
    public void CalculationSkillsUseSandboxLibraries()
    {
        var all = All().ToDictionary(a => a.P.Name, a => a.P.Body);
        foreach (var code in new[] { "piping-flow", "control-valve", "pressure-relief", "physical-properties", "heat-transfer-jacket" })
            Assert.Contains("```python", all[code]);
        Assert.Contains("fluids", all["pressure-relief"]);
        Assert.Contains("CoolProp", all["physical-properties"]);
    }
}
