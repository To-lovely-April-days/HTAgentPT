using System.Text.Json;
using HT.Agent.Infrastructure.Clients;

namespace HT.Agent.Tests;

public class SearxTests
{
    [Fact]
    public void 认出没响应的引擎和原因_结果照常解析()
    {
        using var doc = JsonDocument.Parse("""
            {"results":[{"title":"高压釜","url":"https://a.example/1","content":"参数","engine":"bing"},
                        {"title":"重复","url":"https://a.example/1","content":"","engine":"360search"},
                        {"title":"坏链接","url":"javascript:void(0)","content":"","engine":"bing"}],
             "unresponsive_engines":[["baidu","Suspended: CAPTCHA"],["sogou","unexpected crash"]]}
            """);
        var r = SearxClient.Parse(doc.RootElement, 8);
        Assert.Single(r.Hits);
        Assert.Equal([("baidu", "Suspended: CAPTCHA"), ("sogou", "unexpected crash")], r.Failed);
    }

    [Fact]
    public void 没有结果也没有出错的引擎_就是真没搜到()
    {
        using var doc = JsonDocument.Parse("""{"results":[],"unresponsive_engines":[]}""");
        var r = SearxClient.Parse(doc.RootElement, 8);
        Assert.Empty(r.Hits);
        Assert.Empty(r.Failed);
    }
}
