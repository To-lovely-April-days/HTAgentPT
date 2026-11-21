using System.Text.Json.Nodes;
using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class DelegationTests
{
    [Fact]
    public void Parses_tasks_and_caps_count()
    {
        var a = JsonNode.Parse("""
            {"tasks":[
              {"title":"某研究院协议","task":"逐条对照 30L 标准配置","deliver":"做不到的条款","files":["a.docx","a.docx"]},
              {"task":"某化工企业协议逐条对照"},
              {"title":"空的","task":""},
              {"title":"C","task":"c"},{"title":"D","task":"d"},{"title":"E","task":"e"}
            ]}
            """)!.AsObject();
        var t = Delegation.Parse(a, 4);
        Assert.Equal(4, t.Count);
        Assert.Equal(["a.docx"], t[0].Files);
        Assert.Equal(2, t[1].N);
        Assert.Equal("某化工企业协议逐条对照", t[1].Title);
        Assert.Equal("结论、依据，以及没查到的", t[1].Deliver);
        Assert.Empty(Delegation.Parse(new JsonObject(), 4));
    }

    [Fact]
    public void Short_takes_the_conclusion_line()
    {
        Assert.Equal("38 条里 4 条做不到，其余满足。", Delegation.Short("结论：38 条里 4 条做不到，其余满足。\n明细：……"));
        Assert.EndsWith("…", Delegation.Short("结论：" + new string('长', 60)));
    }

    [Fact]
    public void Main_gets_every_result_and_what_to_do_next()
    {
        var t1 = new SubTask(1, "A", "a", "x", []);
        var t2 = new SubTask(2, "B", "b", "y", []);
        var text = Delegation.ForMain([new SubResult(t1, "done", "结论：好", null), new SubResult(t2, "partial", "结论：一半", "查了 5 轮还没查完，停了")]);
        Assert.Contains("【子任务 1：A】", text);
        Assert.Contains("没查完：查了 5 轮还没查完", text);
        Assert.Contains("自己补查一次", text);
        Assert.Contains("2. B：一半", Delegation.Compact([new SubResult(t1, "done", "结论：好", null), new SubResult(t2, "partial", "结论：一半", null)]));
    }

    [Fact]
    public void Sub_agents_cannot_split_ask_or_make_widgets()
    {
        foreach (var t in new[] { "delegate", "ask_user", "show_block", "remember" })
            Assert.Contains(t, Delegation.SubAgentDrops);
        Assert.DoesNotContain("search_documents", Delegation.SubAgentDrops);
        var p = Delegation.SubPrompt("霍桐仪器", "做反应釜", new SubTask(1, "A", "a", "x", []), ["a.docx"]);
        Assert.Contains("子代理", p);
        Assert.Contains("《a.docx》", p);
        Assert.Contains("没查到：", p);
    }

    [Fact]
    public void Step_line_for_delegate_counts_tasks()
    {
        var d = AssistantLoop.Describe(new HT.Agent.Application.Abstractions.ToolCall("c1", "delegate", """{"tasks":[{"task":"a"},{"task":"b"},{"task":"c"}]}"""));
        Assert.Equal("3 个子任务，同时进行", d);
        Assert.Equal("分头查", AssistantLoop.ToolLabel("delegate"));
    }
}
