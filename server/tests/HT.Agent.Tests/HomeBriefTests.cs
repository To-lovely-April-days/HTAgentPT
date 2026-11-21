using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class HomeBriefTests
{
    private static HomeBrief.Context Ctx(params HomeBrief.Candidate[] c) => new("张工", "售前工程师", null,
        new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.FromHours(8)), ["在跟华东理工"], ["50L 加氢釜材料成本"], c, 1);

    private static readonly HomeBrief.Candidate Price = new("P1", "price", "📈", "+6.2%", "up", "C276 板材从 268 到 285", "C276 涨价了", "细节", "看看", "C276 涨价影响？");

    [Fact]
    public void Model_output_is_used_but_numbers_stay_from_the_system()
    {
        var raw = """
            <think>想一想</think>
            {"lines":{"afternoon":{"title":"下午三点，撑住 ☕","sub":"有一件正事"}},
             "knowledge":{"title":"「哈氏」不是人名","body":"来自 Haynes 公司。"},
             "quiz":{"question":"不锈钢为什么不容易生锈？","options":["镀锌","铬的钝化膜","没有铁"],"answer":1,"explain":"铬生成致密氧化膜。"},
             "history":{"year":"1913","text":"哈伯–博施法合成氨实现工业化。"},
             "number":{"value":"1.25×","text":"液压试验压力系数。"},
             "work":[{"id":"P1","title":"C276 板材涨了，你那台釜要重算","detail":"多一万二","action":"重算","prompt":"按新价重算 50L 加氢釜材料成本"},
                     {"id":"X9","title":"编的","detail":"","action":"","prompt":""}],
             "quiet":["问题一","问题二"]}
            """;
        var r = HomeBrief.Parse(raw, Ctx(Price));
        Assert.True(r.Generated);
        Assert.Equal("下午三点，撑住 ☕", r.Lines["afternoon"].Title);
        Assert.Equal("早，张工 ☀️", r.Lines["morning"].Title); // 模型没给的段落用兜底
        var w = Assert.Single(r.Work);
        Assert.Equal("+6.2%", w.Badge);
        Assert.Equal("重算", w.Action);
        Assert.Equal("「哈氏」不是人名", r.Fun.Knowledge!.Title);
        Assert.Equal(1, r.Fun.Quiz!.Answer);
        Assert.Equal(3, r.Fun.Quiz.Options.Count);
        Assert.Equal("1913", r.Fun.History!.Year);
        Assert.Equal("1.25×", r.Fun.Number!.Value);
        Assert.Equal(2, r.Quiet.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("不是 JSON")]
    [InlineData("{ 坏掉的")]
    public void Bad_output_falls_back_without_fake_examples(string? raw)
    {
        var r = HomeBrief.Parse(raw, Ctx(Price));
        Assert.False(r.Generated);
        Assert.Equal("P1", Assert.Single(r.Work).Id);
        Assert.Empty(r.Quiet);
        Assert.Null(r.Fun.Knowledge);
        Assert.Null(r.Fun.Quiz);
    }

    [Theory]
    [InlineData(2026, 11, 20, 15, "friday")]
    [InlineData(2026, 11, 19, 15, "afternoon")]
    [InlineData(2026, 11, 19, 9, "morning")]
    [InlineData(2026, 11, 19, 12, "noon")]
    [InlineData(2026, 11, 19, 22, "late")]
    [InlineData(2026, 11, 19, 3, "late")]
    public void Slot_follows_the_clock(int y, int m, int d, int h, string slot)
        => Assert.Equal(slot, HomeBrief.SlotOf(new DateTimeOffset(y, m, d, h, 0, 0, TimeSpan.FromHours(8))));

    [Fact]
    public void Quiz_with_a_bad_answer_index_is_dropped()
    {
        var r = HomeBrief.Parse("""{"quiz":{"question":"问","options":["a","b"],"answer":5}}""", Ctx());
        Assert.Null(r.Fun.Quiz);
    }

    [Fact]
    public void Prompt_lists_candidates_and_forbids_made_up_numbers()
    {
        var p = HomeBrief.Prompt(Ctx(Price));
        Assert.Contains("P1：C276 板材从 268 到 285", p);
        Assert.Contains("不要编造", p);
        Assert.Contains("星期五", p);
    }
}
