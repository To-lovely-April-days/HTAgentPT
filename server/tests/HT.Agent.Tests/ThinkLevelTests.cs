using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Clients;
using HT.Agent.Infrastructure.Services;

namespace HT.Agent.Tests;

public class ThinkLevelTests
{
    [Theory]
    [InlineData("30L 加氢釜怎么选型？", "standard")]
    [InlineData("帮我算一下夹套换热面积", "standard")]
    [InlineData("316L 和 C276 有什么区别", "standard")]
    [InlineData("售后电话多少", "fast")]
    [InlineData("把这句翻成英文：反应釜", "fast")]
    public void Auto_picks_by_question(string q, string expected)
    {
        var c = ThinkLevels.Resolve(null, "auto", true, true, q, false);
        Assert.True(c.Auto);
        Assert.Equal(expected, ThinkLevels.Code(c.Level));
    }

    [Fact]
    public void Auto_thinks_when_files_attached()
    {
        var c = ThinkLevels.Resolve("auto", "auto", true, true, "看看", true);
        Assert.Equal(ThinkLevel.Standard, c.Level);
        Assert.Equal("带了文件", c.Reason);
    }

    [Fact]
    public void Picked_level_is_used_as_is()
    {
        var c = ThinkLevels.Resolve("fast", "auto", true, true, "30L 加氢釜怎么选型？", false);
        Assert.Equal(ThinkLevel.Fast, c.Level);
        Assert.False(c.Auto);
        Assert.Null(c.Reason);
    }

    [Fact]
    public void Deep_falls_back_to_standard_without_32k_context()
    {
        var c = ThinkLevels.Resolve("deep", "auto", true, false, "选型", false);
        Assert.Equal(ThinkLevel.Standard, c.Level);
        Assert.Contains("32K", c.Reason);
    }

    [Fact]
    public void Staff_choice_ignored_when_turned_off()
    {
        var c = ThinkLevels.Resolve("deep", "standard", false, true, "电话多少", false);
        Assert.Equal(ThinkLevel.Standard, c.Level);
        Assert.False(c.Auto);
    }

    [Fact]
    public void Describe_reads_naturally()
    {
        Assert.Equal("标准（自动：问题里有「选型」） · 想了 9 秒",
            ThinkLevels.Describe(new LevelChoice(ThinkLevel.Standard, true, "问题里有「选型」"), true, 9.2));
        Assert.Equal("快速 · 不思考，直接答", ThinkLevels.Describe(new LevelChoice(ThinkLevel.Fast, false, null), false, 0));
        Assert.Equal("深度 · 想了 41 秒", ThinkLevels.Describe(new LevelChoice(ThinkLevel.Deep, false, null), true, 41));
    }

    [Fact]
    public void Defaults_follow_official_sampling()
    {
        Assert.False(ThinkLevels.Default(ThinkLevel.Fast).Think);
        Assert.Equal("medium", ThinkLevels.Default(ThinkLevel.Standard).Effort);
        Assert.Equal("xhigh", ThinkLevels.Default(ThinkLevel.Deep).Effort);
        Assert.Equal(new Sampling(0.95, 20, 0), ThinkLevels.SamplingFor(true));
        Assert.Equal(new Sampling(0.8, 20, 1.5), ThinkLevels.SamplingFor(false));
    }

    [Fact]
    public void Round_max_tokens_stays_inside_context()
    {
        var msgs = new List<AgentMessage> { new("system", new string('字', 6500)), new("user", "问题") };
        var o = new AssistantLoopOptions(MaxTokens: 6000, ContextTokens: 8192);
        var t = AssistantLoop.RoundMaxTokens(msgs, 0, o);
        Assert.InRange(t, 256, 8192 - 5000);
        Assert.Equal(6000, AssistantLoop.RoundMaxTokens(msgs, 0, o with { ContextTokens = 0 }));
        Assert.Equal(6000, AssistantLoop.RoundMaxTokens(msgs, 0, o with { ContextTokens = 32768 }));
    }

    [Fact]
    public void Qwen_body_carries_effort_and_sampling()
    {
        var o = new ToolRoundOptions(true, true, 6000, 1.0, "medium", 0.95, 20, 0);
        var body = OpenAiToolChatClient.BuildBody("Qwen/Qwen3.8-27B", [new AgentMessage("user", "hi")], [], o, qwen: true);
        Assert.Equal("medium", body["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>());
        Assert.True(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(20, body["top_k"]!.GetValue<int>());
        Assert.Equal(0.95, body["top_p"]!.GetValue<double>());

        var off = OpenAiToolChatClient.BuildBody("Qwen/Qwen3.8-27B", [new AgentMessage("user", "hi")], [], o with { Think = false }, qwen: true);
        Assert.Null(off["chat_template_kwargs"]!["reasoning_effort"]);

        // 在线服务不带这些参数，免得被拒
        var ds = OpenAiToolChatClient.BuildBody("deepseek-chat", [new AgentMessage("user", "hi")], [], o, qwen: false);
        Assert.Null(ds["top_k"]);
        Assert.Null(ds["chat_template_kwargs"]!["reasoning_effort"]);
        Assert.True(OpenAiToolChatClient.IsQwen("qwen-local", "x"));
        Assert.False(OpenAiToolChatClient.IsQwen("deepseek", "deepseek-chat"));
    }

    [Fact]
    public void Accumulator_keeps_reasoning_and_think_time()
    {
        var acc = new OpenAiToolChatClient.StreamAccumulator();
        acc.Feed("""{"choices":[{"delta":{"reasoning_content":"先看介质"}}]}""", 1.0);
        acc.Feed("""{"choices":[{"delta":{"reasoning":"，再看温度"}}]}""", 4.5);
        acc.Feed("""{"choices":[{"delta":{"content":"结论"},"finish_reason":"length"}]}""", 5.0);
        var r = acc.Result();
        Assert.Equal("先看介质，再看温度", r.Reasoning);
        Assert.Equal(4.5, r.ThinkSeconds);
        Assert.Equal("length", r.FinishReason);
        Assert.Equal("结论", r.Content);
    }

    [Fact]
    public async Task 回答最长默认不设限_按上下文给_填了数就按填的但不超过上下文()
    {
        var c = await LevelConfig.LoadAsync(new MapConfig(new() { [ConfigKeys.ChatContextTokens] = "32768" }), CancellationToken.None);
        Assert.Equal(32768, c.Levels[ThinkLevel.Standard].MaxTokens);
        Assert.Equal("medium", c.Levels[ThinkLevel.Standard].Effort);

        c = await LevelConfig.LoadAsync(new MapConfig(new()
        {
            [ConfigKeys.ChatContextTokens] = "32768",
            [ConfigKeys.LevelStandardMaxTokens] = "10000",
            [ConfigKeys.LevelDeepMaxTokens] = "100000",
        }), CancellationToken.None);
        Assert.Equal(10000, c.Levels[ThinkLevel.Standard].MaxTokens);
        Assert.Equal(32768, c.Levels[ThinkLevel.Deep].MaxTokens);
    }

    private sealed class MapConfig(Dictionary<string, string> values) : IRuntimeConfig
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(values.GetValueOrDefault(key));
        public Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default)
            => Task.FromResult(values.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback);
        public Task<double> GetDoubleAsync(string key, double fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<string> GetStringAsync(string key, string fallback, CancellationToken ct = default)
            => Task.FromResult(values.GetValueOrDefault(key) ?? fallback);
        public Task SetAsync(string key, string value, Guid? updatedBy, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(values);
    }
}
