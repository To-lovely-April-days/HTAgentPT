using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

/// <summary>助手做事的固定流程：开工前判断、交付前自查和收尾——解析模型交回的 JSON。</summary>
public class TaskFlowTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 多步任务_列出步骤和假设_包在代码块里也认得()
    {
        var t = TaskFlow.ParseTriage("""
            <think>{先想想}</think>好的：
            ```json
            {"mode":"task","steps":["查我司实验室反应釜型号参数","查竞品公开资料","整理对比表","做 PPT"],
             "assumptions":["按 250mL 规格对比","按内部汇报风格"],"blocking":[]}
            ```
            """);
        Assert.True(t.Task);
        Assert.Equal(4, t.Steps.Count);
        Assert.Equal(["按 250mL 规格对比", "按内部汇报风格"], t.Assumptions);
        Assert.Empty(t.Blocking);
    }

    [Fact]
    public void 只有一步的不算多步任务_看不懂的当直接答()
    {
        Assert.False(TaskFlow.ParseTriage("""{"mode":"task","steps":["回答"]}""").Task);
        Assert.False(TaskFlow.ParseTriage("这个问题直接回答就行").Task);
        Assert.False(TaskFlow.ParseTriage("").Task);
    }

    [Fact]
    public void 非问不可的问题_选项不够两个的不要_最多四个()
    {
        var t = TaskFlow.ParseTriage("""
            {"mode":"direct","steps":[],"assumptions":[],"blocking":[
              {"question":"是哪个项目？","options":[{"label":"HT-2024-018","detail":"50L 加氢釜"},{"label":"HT-2025-003"}]},
              {"question":"只有一个选项","options":[{"label":"甲"}]},
              {"question":"要哪种格式？","options":["Word","PDF"],"multiple":true}]}
            """);
        Assert.Equal(2, t.Blocking.Count);
        Assert.Equal("50L 加氢釜", t.Blocking[0].Options[0].Detail);
        Assert.True(t.Blocking[1].Multiple);
        Assert.Equal(["Word", "PDF"], t.Blocking[1].Options.Select(o => o.Label));
    }

    [Fact]
    public void 列好的计划交给助手时写明步骤和假设_不用再问()
    {
        var plan = new TaskPlan();
        plan.Seed(["查资料", "做 PPT"], ["按内部汇报风格"], T0);
        var note = TaskFlow.PlanNote(plan);
        Assert.Contains("1. 查资料（现在做这步）", note);
        Assert.Contains("按内部汇报风格", note);
        Assert.Contains("不要停下来问用户", note);
        Assert.Equal("now", plan.Items[0].Status);
        Assert.Equal("todo", plan.Items[1].Status);
        Assert.True(plan.Seeded);
    }

    [Fact]
    public void 自查结果_缺口和收尾卡_按钮不够两个就不给()
    {
        var r = TaskFlow.ParseReview("""
            {"problems":["要 PPT 但没有做出文件"],"assumptions":["按 250mL 规格"],"left":["竞品价格没查到"],
             "question":"要换成深色风格吗？","options":["换成深色风格","可以了"]}
            """);
        Assert.Equal(["要 PPT 但没有做出文件"], r.Problems);
        Assert.Equal("要换成深色风格吗？", r.Ask);
        Assert.Equal(2, r.Options.Count);
        Assert.True(r.HasCard);

        var one = TaskFlow.ParseReview("""{"problems":[],"question":"还要别的吗？","options":["可以了"]}""");
        Assert.Empty(one.Options);
        Assert.Empty(TaskFlow.ParseReview("坏了").Problems);
    }

    [Fact]
    public void 交给自查的内容_有计划状态和系统查出的问题_长回答留头尾()
    {
        var plan = new TaskPlan();
        plan.Seed(["查资料", "做 PPT"], [], T0);
        var answer = new string('甲', 5000) + new string('乙', 3000);
        var input = TaskFlow.ReviewInput("对比竞品做 PPT", plan, answer, [], ["计划里没做完的步骤：做 PPT"]);
        Assert.Contains("- 做 PPT：没开始", input);
        Assert.Contains("做出的文件：没有", input);
        Assert.Contains("系统查出来的问题：计划里没做完的步骤：做 PPT", input);
        Assert.Contains("（中间略）", input);
        Assert.EndsWith(new string('乙', 2000), input);
    }

    [Fact]
    public void 计划能存档恢复_停下来问完接着做()
    {
        var plan = new TaskPlan();
        plan.Seed(["查资料", "整理", "做 PPT"], ["按内部风格"], T0);
        plan.OnStep("s1", "web_search");
        var json = JsonSerializer.Serialize(plan.ToSnap());
        var back = new TaskPlan();
        back.Restore(JsonSerializer.Deserialize<TaskPlan.Snap>(json));
        Assert.Equal(["查资料", "整理", "做 PPT"], back.Items.Select(i => i.Title));
        Assert.Equal(["s1"], back.Items[0].Steps);
        Assert.Equal(["按内部风格"], back.Assumptions);
        Assert.True(back.Seeded);
        Assert.Null(new TaskPlan().ToSnap());
    }

    [Theory]
    [InlineData("接着做：从中断的地方继续", true)]
    [InlineData("继续", true)]
    [InlineData("（接着做）", true)]
    [InlineData("Continue please", true)]
    [InlineData("压力容器怎么设计", false)]
    [InlineData("我想接着问一下封头", false)]
    public void 只有说接着做才从存下的进度续上(string q, bool expected) => Assert.Equal(expected, TaskFlow.WantsResume(q));

    [Fact]
    public void 续上时告诉助手前面查到的直接用()
    {
        var note = TaskFlow.ResumeNote("接着做");
        Assert.Contains("不要重新查", note);
        Assert.EndsWith("用户说：接着做", note);
    }

    [Fact]
    public void 存下的进度能原样读回_工具调用和结果都在()
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var plan = new TaskPlan();
        plan.Seed(["查标准", "算厚度"], ["按 Q345R"], DateTimeOffset.UtcNow);
        var cp = new HT.Agent.Infrastructure.Services.AssistantService.Checkpoint(
            [new("system", "s"), new("user", "q"), new("assistant", "", [new ToolCall("c1", "search_documents", "{\"q\":\"GB/T 150\"}")]), new("tool", "[1] 原文", ToolCallId: "c1")],
            new([], new() { ["k"] = 1 }, new(), [3.29], ["GB/T 150"], 2, plan.ToSnap()));
        var back = System.Text.Json.JsonSerializer.Deserialize<HT.Agent.Infrastructure.Services.AssistantService.Checkpoint>(
            System.Text.Json.JsonSerializer.Serialize(cp, json), json)!;

        Assert.Equal(4, back.Messages.Count);
        Assert.Equal("search_documents", back.Messages[2].ToolCalls![0].Name);
        Assert.Equal("c1", back.Messages[3].ToolCallId);
        Assert.Equal(2, back.Run.NextN);
        var restored = new TaskPlan();
        restored.Restore(back.Run.Plan);
        Assert.Equal(["查标准", "算厚度"], restored.Items.Select(i => i.Title));
    }
}
