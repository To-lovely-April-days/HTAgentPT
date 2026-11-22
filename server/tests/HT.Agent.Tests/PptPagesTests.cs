using System.Text.Json.Nodes;
using HT.Agent.Application.Logic;
using Xunit;

namespace HT.Agent.Tests;

public class PptPagesTests
{
    [Fact]
    public void Good_pages_are_cleaned_up()
    {
        var input = JsonNode.Parse("""
        [
          {"type":"cover","title":"30L 加氢反应釜方案","subtitle":"上海霍桐实验仪器有限公司","extra":"不要"},
          {"type":"two","title":"方案要点","items":[{"title":"磁力密封","text":"零泄漏"},"316L 釜体"]},
          {"type":"KPI","title":"主要参数","numbers":[{"value":"10 MPa","label":"设计压力"},{"label":"没写数值的不要"}]},
          {"type":"table","title":"配置对比","rows":[["项目","A","B"],["材质","316L","C276"]]},
          {"type":"end"}
        ]
        """);
        var (pages, errors) = PptPages.Validate(input);
        Assert.Empty(errors);
        Assert.NotNull(pages);
        Assert.Equal(5, pages!.Count);
        Assert.Null(pages[0]!["extra"]);
        Assert.Equal("items", pages[1]!["type"]!.ToString());          // two → items
        Assert.Equal("316L 釜体", pages[1]!["items"]![1]!["title"]!.ToString());
        Assert.Equal("num", pages[2]!["type"]!.ToString());            // KPI → num
        Assert.Single(pages[2]!["numbers"]!.AsArray());
        Assert.Equal(2, pages[3]!["rows"]!.AsArray().Count);
    }

    [Fact]
    public void Bad_pages_say_what_to_fix()
    {
        var input = JsonNode.Parse("""
        [
          {"type":"chart","title":"销量"},
          {"type":"list","title":"","bullets":["1","2","3","4","5","6","7","8"]},
          {"type":"table","title":"对比","rows":[["只有表头"]]},
          {"type":"items","title":"几栏"}
        ]
        """);
        var (pages, errors) = PptPages.Validate(input);
        Assert.Null(pages);
        Assert.Contains(errors, e => e.Contains("第 1 页") && e.Contains("不认识"));
        Assert.Contains(errors, e => e.Contains("第 2 页") && e.Contains("title"));
        Assert.Contains(errors, e => e.Contains("第 2 页") && e.Contains("拆成两页"));
        Assert.Contains(errors, e => e.Contains("第 3 页") && e.Contains("至少两行"));
        Assert.Contains(errors, e => e.Contains("第 4 页") && e.Contains("没有 items"));
        Assert.Single(PptPages.Validate(JsonNode.Parse("{}")).Errors);
    }

    [Fact]
    public void Too_many_pages_refused()
    {
        var arr = new JsonArray();
        for (var i = 0; i < 31; i++) arr.Add(new JsonObject { ["type"] = "list", ["title"] = $"第{i}页", ["bullets"] = new JsonArray("a") });
        var (pages, errors) = PptPages.Validate(arr);
        Assert.Null(pages);
        Assert.Contains(errors, e => e.Contains("最多 30 页"));
    }

    [Fact]
    public void Template_pick_order()
    {
        var a = (Id: Guid.NewGuid(), Name: "极简白", Tags: "简约");
        var b = (Id: Guid.NewGuid(), Name: "浅蓝企业", Tags: "商务");
        var c = (Id: Guid.NewGuid(), Name: "深蓝网络", Tags: "科技,深色");
        var all = new List<(Guid Id, string Name, string Tags)> { a, b, c };
        Assert.Equal(c, PptPages.Pick(all, t => t, "深蓝网络", null));
        Assert.Equal(c, PptPages.Pick(all, t => t, "「深蓝」", null));       // 名字的一部分
        Assert.Equal(c, PptPages.Pick(all, t => t, "深色", null));           // 风格标签
        Assert.Equal(a, PptPages.Pick(all, t => t, a.Id.ToString(), null));
        Assert.Equal(a, PptPages.Pick(all, t => t, null, a.Id));             // 用户在输入框选的
        Assert.Equal(b, PptPages.Pick(all, t => t, "没有这套", null));       // 都对不上：第一套商务
        Assert.Equal(default, PptPages.Pick(new List<(Guid Id, string Name, string Tags)>(), t => t, null, null));
    }

    [Fact]
    public void Blocked_sources()
    {
        Assert.Equal("slidesgo.com", PptPages.Blocked("https://slidesgo.com/theme/abc", PptPages.DefaultBlocked));
        Assert.Null(PptPages.Blocked("https://www.slidescarnival.com/template/x/1", PptPages.DefaultBlocked));
        Assert.Null(PptPages.Blocked("公司自有", PptPages.DefaultBlocked));
        Assert.Equal("example.org", PptPages.Blocked("http://Example.org/a", "foo.com， example.org"));
    }

    [Fact]
    public void File_names_are_safe()
    {
        Assert.Equal("30L加氢釜方案.pptx", PptPages.SafeFileName("30L加氢釜方案.pptx", "方案"));
        Assert.Equal("ab.pptx", PptPages.SafeFileName("a/b", "方案"));
        Assert.Equal("方案.pptx", PptPages.SafeFileName("  ../  ", "方案"));
        Assert.Equal("x'y.pptx".Replace("'", ""), PptPages.SafeFileName("x'y", "方案"));
        Assert.Equal(65, PptPages.SafeFileName(new string('长', 100), "方案").Length);
    }

    [Fact]
    public void Report_for_model()
    {
        var rep = JsonNode.Parse("""
        {"slides":9,"cleared":12,"photos":true,
         "overflow":["第 3 页正文：字太多，缩到 12 号还放不下"],
         "shrunk":["第 2 页标题：从 40 号缩到 34 号"],
         "fallback":["第 6 页：模板里没有「table」这种页，用了「list」页"]}
        """);
        var (text, checks) = PptPages.Report(rep, "深蓝网络", credit: true);
        Assert.Contains("9 页", checks);
        Assert.Contains("1 处放不下", checks);
        Assert.Contains("模板原文都换掉了", checks);
        Assert.Contains("拆开或删减", text);
        Assert.Contains("深蓝网络", text);
        Assert.Contains("署名", text);
        Assert.Contains("致谢页", text);
        var (ok, okChecks) = PptPages.Report(JsonNode.Parse("""{"slides":5,"cleared":0}"""), "极简白", credit: false);
        Assert.Contains("没有字超出框", okChecks);
        Assert.DoesNotContain("署名", ok);
    }
}
