using System.Net;
using System.Text;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Infrastructure.Clients;
using HT.Agent.Infrastructure.Services;

namespace HT.Agent.Tests;

public class ToolKitTests
{
    [Fact]
    public void Extracts_title_and_readable_text_from_html()
    {
        var (title, text) = WebText.Extract("<html><head><title>哈氏合金 C276</title><style>p{}</style></head><body><nav>菜单</nav><h1>耐蚀性</h1><p>在盐酸中&nbsp;耐蚀性好。</p><script>alert(1)</script><table><tr><td>温度</td><td>80℃</td></tr></table></body></html>");
        Assert.Equal("哈氏合金 C276", title);
        Assert.Contains("在盐酸中 耐蚀性好。", text);
        Assert.Contains("温度 | 80℃", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("菜单", text);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("114.114.114.114", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.17.0.1", false)]
    [InlineData("192.168.28.160", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    public void Only_public_addresses_are_allowed(string ip, bool ok) => Assert.Equal(ok, NetGuard.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("http://example.com:8080/", false)]
    [InlineData("ftp://example.com/", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("http://user:pw@example.com/", false)]
    public void Only_plain_http_urls_are_allowed(string url, bool ok) => Assert.Equal(ok, NetGuard.IsAllowedUrl(new Uri(url)));

    [Fact]
    public void Redacts_customer_names_project_numbers_and_amounts()
    {
        var (t, changed) = Redactor.Redact("华东理工 P-2024-018 反应釜报价 25.8 万元 哈氏合金", ["华东理工"], [], @"P-\d{4}-\d+");
        Assert.True(changed);
        Assert.Equal("某客户 某项目 反应釜报价 某金额 哈氏合金", t);
        Assert.False(Redactor.Redact("哈氏合金 耐盐酸", ["华东理工"], [], null).Changed);
    }

    [Fact]
    public void Pairs_requirement_clauses_with_the_closest_passage()
    {
        var a = DocCompare.SplitClauses([("3.1 设计压力不低于 2.5 MPa。\n3.4 釜体材质采用哈氏合金 C276。\n5.1 防爆等级 Ex d IIB T4。", "第 2 页")]);
        var b = DocCompare.SplitClauses([("釜体材质为 316L 不锈钢。", "第 7 页"), ("设计压力 3.0 MPa，设计温度 320℃。", "第 6 页")], 400);
        var pairs = DocCompare.Match(a, b);
        Assert.Equal(3, pairs.Count);
        Assert.Contains("设计压力 3.0", pairs[0].B!.Text);
        Assert.Contains("316L", pairs[1].B!.Text);
        Assert.Null(pairs[2].B);
    }

    [Fact]
    public void Exports_markdown_to_a_word_file()
    {
        var bytes = MarkdownDocx.Build("选型对比", "# 结论\n**推荐** 50L 加氢釜[1]\n\n| 项目 | 压力 |\n|---|---|\n| A | 3 MPa |\n\n- 第一点\n> 行业通用知识", ["[1] 尼龙66合成装置 技术协议 第 6 页"]);
        Assert.True(bytes.Length > 1000);
        Assert.Equal((byte)'P', bytes[0]); // docx 是 zip
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var text = doc.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains("推荐", text);
        Assert.Contains("3 MPa", text);
        Assert.Contains("尼龙66合成装置", text);
    }

    [Fact]
    public void Decodes_gbk_pages()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding("GBK").GetBytes("<meta charset=\"gbk\"><p>反应釜</p>");
        Assert.Contains("反应釜", WebFetcher.Decode(gbk, null));
    }
}

public class AssistantPauseTests
{
    private sealed class OneShot : IToolChatClient
    {
        public Task<ToolRoundResult> RunRoundAsync(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, ToolRoundOptions options,
            Func<string, Task>? onText, CancellationToken ct = default)
            => Task.FromResult(new ToolRoundResult("", [new ToolCall("c9", "ask_user", "{\"question\":\"哪个？\"}")], "tool_calls"));
    }

    [Fact]
    public async Task Loop_pauses_when_a_tool_waits_for_the_user()
    {
        var msgs = new List<AgentMessage> { new("user", "q") };
        var r = await new AssistantLoop(new OneShot(), new AssistantLoopOptions()).RunAsync(msgs, [new("ask_user", "", new { })],
            (_, _) => Task.FromResult(new ToolOutcome("", "", "请你确认", "哪个？", "等你选", Pause: true)), _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("c9", r.PausedCallId);
        // 等用户填的那次调用没有结果消息；恢复时再补上
        Assert.DoesNotContain(msgs, m => m.Role == "tool");
        Assert.Contains(msgs, m => m.Role == "assistant" && m.ToolCalls!.Any(c => c.Id == "c9"));
    }
}
