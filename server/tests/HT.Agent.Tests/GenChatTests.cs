using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

/// <summary>对话式填槽的纯逻辑（GenChatLogic）：指令、日期、选项、模型输出解析、模板推荐打分。</summary>
public class GenChatTests
{
    [Theory]
    [InlineData("跳过", GenChatLogic.ChatCommand.Skip)]
    [InlineData("先跳过。", GenChatLogic.ChatCommand.Skip)]
    [InlineData("下一个", GenChatLogic.ChatCommand.Skip)]
    [InlineData("生成文档", GenChatLogic.ChatCommand.Render)]
    [InlineData("生成", GenChatLogic.ChatCommand.Render)]
    [InlineData("可以生成了", GenChatLogic.ChatCommand.Render)]
    [InlineData("汇总", GenChatLogic.ChatCommand.Summary)]
    [InlineData("材质是316L", GenChatLogic.ChatCommand.None)]
    // 长句里带「生成」不算指令——那是内容
    [InlineData("加热形式选电加热就能生成合格品", GenChatLogic.ChatCommand.None)]
    public void 指令识别_只认独立短句(string text, GenChatLogic.ChatCommand expected)
        => Assert.Equal(expected, GenChatLogic.DetectCommand(text));

    [Theory]
    [InlineData("2026-9-3", "2026-09-03")]
    [InlineData("2026/09/03", "2026-09-03")]
    [InlineData("2026年9月3日", "2026-09-03")]
    [InlineData("9月3日", "2025-09-03")]
    [InlineData("2026-13-01", null)]
    [InlineData("下周三", null)]
    public void 日期规整(string raw, string? expected)
        => Assert.Equal(expected, GenChatLogic.NormalizeDate(raw, 2025));

    [Fact]
    public void 选项匹配_精确_包含_同义_歧义()
    {
        string[] heat = ["电加热", "油浴控夹套", "油浴控物料"];
        Assert.Equal("电加热", GenChatLogic.MatchChoice("电加热", heat));
        Assert.Equal("电加热", GenChatLogic.MatchChoice("用电加热的", heat));
        Assert.Null(GenChatLogic.MatchChoice("油浴", heat));           // 两个油浴选项都沾边 → 不猜
        Assert.Null(GenChatLogic.MatchChoice("蒸汽加热", heat));
        string[] yn = ["有", "无"];
        Assert.Equal("无", GenChatLogic.MatchChoice("不需要", yn));
        Assert.Equal("有", GenChatLogic.MatchChoice("需要", yn));
        string[] volt = ["220V", "380V"];
        Assert.Equal("380V", GenChatLogic.MatchChoice("380v", volt));
    }

    [Fact]
    public void 模型输出解析_容忍围栏与废话_解析失败给空表()
    {
        var fills = GenChatLogic.ParseModelFills(
            "好的，以下是抽取结果：\n```json\n{\"fills\":[{\"tag\":\"material\",\"value\":\"316L\"},{\"tag\":\"device_qty\",\"value\":2}]}\n```");
        Assert.Equal(2, fills.Count);
        Assert.Equal(("material", "316L"), fills[0]);
        Assert.Equal(("device_qty", "2"), fills[1]);

        Assert.Empty(GenChatLogic.ParseModelFills("完全不是 JSON 的回答"));
        Assert.Empty(GenChatLogic.ParseModelFills("{\"fills\":\"不是数组\"}"));
        Assert.Empty(GenChatLogic.ParseModelFills("{\"fills\":[{\"tag\":\"a\"}]}")); // 缺 value 丢弃
    }

    [Fact]
    public void 模板推荐打分_名称命中优先_类别兜底()
    {
        var q = "帮我出一份C类生产任务单，客户是华东理工";
        var hit = GenChatLogic.TemplateScore(q, "C类生产任务单（非标）", "方案");
        var miss = GenChatLogic.TemplateScore(q, "售后维修报告", "报告");
        Assert.True(hit > miss);
        Assert.True(GenChatLogic.TemplateScore("起草一个方案", "投标书模板", "方案") > 0); // 类别命中
        Assert.Equal(0, GenChatLogic.TemplateScore("水泵怎么保养", "投标书模板", "投标"));
    }
}
