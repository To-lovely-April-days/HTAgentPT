using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Infrastructure.Clients;

namespace HT.Agent.Tests;

public class UnitCalcTests
{
    [Theory]
    [InlineData("1+2*3", 7)]
    [InlineData("(1+2)*3", 9)]
    [InlineData("2^3^2", 512)]
    [InlineData("-3+5", 2)]
    [InlineData("sqrt(16)+round(2.345, 2)", 6.35)]
    [InlineData("3.14*0.25^2/4", 0.0490625)]
    [InlineData("（2+3）×4", 20)]
    public void Evaluates(string expr, double expected) => Assert.Equal(expected, UnitCalc.Evaluate(expr), 6);

    [Fact]
    public void Rejects_garbage() => Assert.Throws<FormatException>(() => UnitCalc.Evaluate("2+rm -rf"));

    [Theory]
    [InlineData(3, "mbar", "Pa", 300)]
    [InlineData(2, "MPa", "bar", 20)]
    [InlineData(1, "atm", "kPa", 101.325)]
    [InlineData(50, "℃", "K", 323.15)]
    [InlineData(212, "°F", "C", 100)]
    [InlineData(20, "L/h", "m3/h", 0.02)]
    [InlineData(1, "m3/h", "L/min", 16.6666667)]
    [InlineData(100, "cP", "Pa·s", 0.1)]
    public void Converts(double v, string from, string to, double expected) => Assert.Equal(expected, UnitCalc.Convert(v, from, to), 5);

    [Fact]
    public void Refuses_mixed_dimensions() => Assert.Throws<FormatException>(() => UnitCalc.Convert(1, "MPa", "L"));
}

public class CitationCheckTests
{
    private static Dictionary<int, EvidenceItem> Ev(params (int N, string Text)[] xs)
        => xs.ToDictionary(x => x.N, x => new EvidenceItem(x.N, "doc", "协议", x.Text));

    [Fact]
    public void Collects_cited_numbers_in_all_styles()
        => Assert.Equal([1, 2, 3, 5], CitationCheck.Cited("A[1][2]，B[3,5]。C[1]").OrderBy(x => x));

    [Fact]
    public void Verifies_numbers_against_cited_text_only()
    {
        var ev = Ev((1, "高压聚合釜：设计压力 3.0 MPa，设计温度 320℃，容积 20L"), (2, "磁力耦合密封，转速 0–500 rpm"));
        var answer = "聚合釜设计压力 3 MPa、设计温度 320℃[1]。转速最高 600 rpm[2]。客户要 5L 的。";
        var r = CitationCheck.Verify(answer, ev, []);
        Assert.Equal(3, r.Checked);
        Assert.Equal(2, r.Verified);
        Assert.Single(r.Issues);
        Assert.Equal("600 rpm", r.Issues[0].Value);
    }

    [Fact]
    public void Ranges_need_both_ends_and_computed_values_count()
    {
        var ev = Ev((4, "合同金额 18.2 万元；合同金额 25.8 万元"));
        var r1 = CitationCheck.Verify("成交在 18–26 万元[4]。", ev, []);
        Assert.Single(r1.Issues);
        var r2 = CitationCheck.Verify("2 MPa 折合 20 bar[4]。", ev, [2, 20]);
        Assert.Empty(r2.Issues);
    }

    [Fact]
    public void Reports_citations_without_evidence()
        => Assert.Equal([9], CitationCheck.Verify("见 [9]。", Ev((1, "x")), []).MissingRefs);

    [Fact]
    public void Trailing_citation_after_punctuation_stays_with_its_sentence()
    {
        var s = CitationCheck.Sentences("设计压力 3.0 MPa。[1] 转速 500 rpm。[2]").ToList();
        Assert.Equal(2, s.Count);
        Assert.Contains("[1]", s[0]);
    }

    [Fact]
    public void Highlights_the_sentence_that_supports_the_answer()
    {
        var chunk = "本装置由成盐釜和聚合釜组成。聚合釜设计压力 3.0 MPa，设计温度 320℃。配套冷凝器一台。";
        var hl = CitationCheck.Highlights(chunk, ["聚合釜设计压力 3.0 MPa、设计温度 320℃[1]。"]);
        Assert.NotEmpty(hl);
        Assert.Contains("3.0 MPa", chunk.Substring(hl[0].Start, hl[0].Length));
    }
}

public class ToolCallParsingTests
{
    [Fact]
    public void Parses_hermes_style()
    {
        var p = ToolCallTextParser.Parse("好的<tool_call>{\"name\":\"search_documents\",\"arguments\":{\"query\":\"尼龙66\"}}</tool_call>");
        Assert.Single(p.Calls);
        Assert.Equal("search_documents", p.Calls[0].Name);
        Assert.Contains("尼龙66", p.Calls[0].ArgumentsJson);
        Assert.Equal("好的", p.Rest);
    }

    [Fact]
    public void Parses_qwen_xml_style()
    {
        var p = ToolCallTextParser.Parse("<tool_call>\n<function=convert_unit>\n<parameter=value>\n3\n</parameter>\n<parameter=from>mbar</parameter>\n<parameter=to>Pa</parameter>\n</function>\n</tool_call>");
        Assert.Single(p.Calls);
        using var doc = JsonDocument.Parse(p.Calls[0].ArgumentsJson);
        Assert.Equal(3, doc.RootElement.GetProperty("value").GetDouble());
        Assert.Equal("mbar", doc.RootElement.GetProperty("from").GetString());
    }

    [Fact]
    public void Stream_accumulator_rebuilds_split_tool_calls()
    {
        var acc = new OpenAiToolChatClient.StreamAccumulator();
        acc.Feed("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"search_","arguments":"{\"que"}}]}}]}""");
        acc.Feed("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"documents","arguments":"ry\":\"釜\"}"}}]}}]}""");
        Assert.Equal("你好", acc.Feed("""{"choices":[{"delta":{"content":"你好"}}]}"""));
        var r = acc.Result();
        Assert.Equal("你好", r.Content);
        Assert.Single(r.ToolCalls);
        Assert.Equal("search_documents", r.ToolCalls[0].Name);
        Assert.Equal("{\"query\":\"釜\"}", r.ToolCalls[0].ArgumentsJson);
    }
}

public class AssistantLoopTests
{
    private sealed class ScriptedModel(params ToolRoundResult[] rounds) : IToolChatClient
    {
        public int Calls;
        public readonly List<ToolRoundOptions> Seen = [];
        public async Task<ToolRoundResult> RunRoundAsync(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools,
            ToolRoundOptions options, Func<string, Task>? onText, CancellationToken ct = default)
        {
            Seen.Add(options);
            var r = rounds[Math.Min(Calls++, rounds.Length - 1)];
            if (onText is not null && r.Content.Length > 0) await onText(r.Content);
            return r;
        }
    }

    private static readonly IReadOnlyList<ToolSpec> Tools = [new("search_documents", "搜", new { })];

    [Fact]
    public async Task Runs_tools_until_the_model_answers()
    {
        var model = new ScriptedModel(
            new ToolRoundResult("我先查一下", [new ToolCall("c1", "search_documents", "{\"query\":\"釜\"}")], "tool_calls"),
            new ToolRoundResult("做过，设计压力 3 MPa[1]。", [], "stop"));
        var events = new List<QaEvent>();
        var msgs = new List<AgentMessage> { new("system", "s"), new("user", "q") };
        var r = await new AssistantLoop(model, new AssistantLoopOptions()).RunAsync(msgs, Tools,
            (call, _) => Task.FromResult(new ToolOutcome("[1] 设计压力 3 MPa", "[1]", "搜索资料", "釜", "找到 1 段")),
            e => { events.Add(e); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal("做过，设计压力 3 MPa[1]。", r.Answer);
        Assert.Equal(2, r.Rounds);
        Assert.Single(r.Steps);
        // 先说的「我先查一下」被清掉，过程有 running 和 done 两条
        Assert.Contains(events, e => e.Kind == "replace");
        Assert.Equal(2, events.Count(e => e.Kind == "step"));
        Assert.Contains(msgs, m => m.Role == "tool" && m.ToolCallId == "c1");
    }

    [Fact]
    public async Task Forces_an_answer_after_the_round_limit()
    {
        var model = new ScriptedModel(
            new ToolRoundResult("", [new ToolCall("c1", "search_documents", "{}")], "tool_calls"),
            new ToolRoundResult("", [new ToolCall("c2", "search_documents", "{}")], "tool_calls"),
            new ToolRoundResult("只能先说到这", [], "stop"));
        var r = await new AssistantLoop(model, new AssistantLoopOptions(MaxRounds: 2)).RunAsync(
            [new("user", "q")], Tools,
            (_, _) => Task.FromResult(new ToolOutcome("x", "x", "搜索资料", "", null)),
            _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("只能先说到这", r.Answer);
        Assert.False(model.Seen[2].AllowTools);
        Assert.True(r.HitRoundLimit);
    }

    [Fact]
    public async Task Unfinished_plan_nudges_the_model_to_keep_going()
    {
        // 模型做完第一步就说「接下来我将……」想收尾：提醒它一次，它接着调工具做完，再回答
        var model = new ScriptedModel(
            new ToolRoundResult("", [new ToolCall("c1", "search_documents", "{}")], "tool_calls"),
            new ToolRoundResult("接下来我将列大纲。", [], "stop"),
            new ToolRoundResult("", [new ToolCall("c2", "search_documents", "{}")], "tool_calls"),
            new ToolRoundResult("都做完了。", [], "stop"));
        var left = 2;
        var msgs = new List<AgentMessage> { new("user", "q") };
        var r = await new AssistantLoop(model, new AssistantLoopOptions()).RunAsync(msgs, Tools,
            (_, _) => { left--; return Task.FromResult(new ToolOutcome("x", "x", "搜索资料", "", null)); },
            _ => Task.CompletedTask, CancellationToken.None,
            continueHint: () => left > 0 ? "还有没做完的" : null);
        Assert.Equal("都做完了。", r.Answer);
        Assert.Equal(2, r.Steps.Count);
        Assert.Contains(msgs, m => m.Role == "user" && m.Content == "还有没做完的");
    }

    [Theory]
    [InlineData("<html><title>WTS-WAF拦截详情</title><body><h1>出现该页面的原因：</h1><p>1.你的请求是黑客攻击</p></body></html>", true)]
    [InlineData("<html><title>反应釜选型</title><body><p>高压反应釜由釜体、搅拌、加热组成，防火墙一词与此无关。</p></body></html>", false)]
    public void Recognizes_waf_block_pages(string html, bool blocked)
        => Assert.Equal(blocked, HT.Agent.Infrastructure.Clients.WebFetcher.BlockPage(html));

    [Fact]
    public async Task Nudges_are_capped()
    {
        var model = new ScriptedModel(
            new ToolRoundResult("a", [], "stop"), new ToolRoundResult("b", [], "stop"), new ToolRoundResult("c", [], "stop"));
        var r = await new AssistantLoop(model, new AssistantLoopOptions()).RunAsync([new("user", "q")], Tools,
            (_, _) => Task.FromResult(new ToolOutcome("x", "x", "", "", null)), _ => Task.CompletedTask, CancellationToken.None,
            continueHint: () => "还有");
        Assert.Equal("c", r.Answer);
        Assert.Equal(AssistantLoop.MaxNudges + 1, r.Rounds);
    }

    [Fact]
    public async Task Tool_errors_go_back_to_the_model_instead_of_failing()
    {
        var model = new ScriptedModel(
            new ToolRoundResult("", [new ToolCall("c1", "search_documents", "{}")], "tool_calls"),
            new ToolRoundResult("没查到", [], "stop"));
        var msgs = new List<AgentMessage> { new("user", "q") };
        var r = await new AssistantLoop(model, new AssistantLoopOptions()).RunAsync(msgs, Tools,
            (_, _) => throw new InvalidOperationException("库挂了"), _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("没查到", r.Answer);
        Assert.Equal("error", r.Steps[0].Status);
        Assert.Contains("库挂了", msgs.Last(m => m.Role == "tool").Content);
    }

    [Fact]
    public void Shrinks_oldest_tool_results_first()
    {
        var msgs = new List<AgentMessage> { new("system", "s"), new("tool", new string('a', 100)), new("tool", new string('b', 100)) };
        var compacts = new Dictionary<int, string> { [1] = "A", [2] = "B" };
        AssistantLoop.Shrink(msgs, compacts, 120);
        Assert.Equal("A", msgs[1].Content);
        Assert.Equal(new string('b', 100), msgs[2].Content);
    }
}
