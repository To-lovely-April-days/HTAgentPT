using System.Text.Json.Nodes;
using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

/// <summary>回答里的任务清单：模型列计划、更新；系统兜底。</summary>
public class TaskPlanTests
{
    static JsonArray A(string json) => JsonNode.Parse(json)!.AsArray();
    static readonly DateTimeOffset T0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Steps_go_to_the_item_in_progress_and_previous_item_closes_itself()
    {
        var p = new TaskPlan();
        Assert.Null(p.Update(A("""[{"title":"检索","status":"now"},{"title":"读全文","status":"todo"},{"title":"出对比表","status":"todo"}]"""), null, T0));
        p.OnStep("s2", "search_literature");
        p.OnStep("s3", "plan");                       // 列计划本身不挂
        // 模型忘了把「检索」标 done，直接把下一步标 now：系统替它收尾
        Assert.Null(p.Update(A("""[{"title":"检索","status":"todo"},{"title":"读全文","status":"now"},{"title":"出对比表","status":"todo"}]"""), null, T0.AddSeconds(9)));
        p.OnStep("s4", "fetch_url");
        Assert.Equal("done", p.Items[0].Status);
        Assert.Equal(["s2"], p.Items[0].Steps);
        Assert.Equal(["s4"], p.Items[1].Steps);
        Assert.Equal(T0.AddSeconds(9), p.Items[0].Ended);
        Assert.Equal(T0.AddSeconds(9), p.Items[1].Started);
        Assert.Equal(0, p.Revisions);
    }

    [Fact]
    public void Changing_titles_counts_as_a_revision_and_keeps_history_of_same_steps()
    {
        var p = new TaskPlan();
        p.Update(A("""[{"title":"检索","status":"now"},{"title":"读全文","status":"todo"}]"""), null, T0);
        p.OnStep("s2", "search_literature");
        p.Update(A("""[{"title":"检索","status":"done"},{"title":"只读摘要","status":"now"},{"title":"出对比表","status":"todo"}]"""), "全文打不开", T0.AddSeconds(5));
        Assert.Equal(1, p.Revisions);
        Assert.Equal(["全文打不开"], p.Reasons);
        Assert.Equal(["s2"], p.Items[0].Steps);
        Assert.Equal(3, p.Items.Count);
    }

    [Fact]
    public void Only_one_step_in_progress_and_bad_input_is_explained()
    {
        var p = new TaskPlan();
        p.Update(A("""[{"title":"一","status":"now"},{"title":"二","status":"now"}]"""), null, T0);
        Assert.Single(p.Items, i => i.Status == "now");
        Assert.NotNull(new TaskPlan().Update(A("[]"), null, T0));
        Assert.NotNull(new TaskPlan().Update(A("""[{"title":"","status":"now"}]"""), null, T0));
        Assert.NotNull(new TaskPlan().Update(A(string.Concat("[", string.Join(",", Enumerable.Range(1, 8).Select(i => $$"""{"title":"{{i}}","status":"todo"}""")), "]")), null, T0));
    }

    [Fact]
    public void Finish_closes_the_current_step_and_marks_unreported_ones()
    {
        var p = new TaskPlan();
        p.Update(A("""[{"title":"一","status":"done"},{"title":"二","status":"now"},{"title":"三","status":"todo"}]"""), null, T0);
        p.Finish(T0.AddSeconds(30));
        Assert.Equal("done", p.Items[1].Status);
        Assert.Equal("skip", p.Items[2].Status);
        Assert.Equal("没标完成", p.Items[2].Note);
        p.OnStep("s9", "web_search");                 // 答完以后不再挂
        Assert.Empty(p.Items[1].Steps);
    }
}
