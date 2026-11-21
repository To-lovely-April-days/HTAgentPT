using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

public enum ThinkLevel { Fast, Standard, Deep }

/// <summary>一档的参数。Effort：off=不思考；low / medium / xhigh 是 Qwen3.8 自带的思考强度。</summary>
public record LevelSettings(string Effort, int MaxRounds, int MaxTokens, double Temperature)
{
    public bool Think => Effort != "off";
}

/// <summary>这次用哪一档、是不是系统自动选的、为什么。</summary>
public record LevelChoice(ThinkLevel Level, bool Auto, string? Reason);

/// <summary>采样参数：按「思考 / 不思考」照 Qwen 官方推荐带，温度另由每档自己定。</summary>
public record Sampling(double TopP, int TopK, double PresencePenalty);

/// <summary>思考档位：输入框里选「自动 / 快速 / 标准 / 深度」。
/// 不换模型，只是每次提问带的参数不同（思考开关和强度、最多查几轮、回答最长、温度）。</summary>
public static class ThinkLevels
{
    /// <summary>深度档要的上下文（模型服务的 --max-model-len）。不够时深度按标准答。</summary>
    public const int DeepMinContext = 32768;

    public static readonly string[] Efforts = ["off", "low", "medium", "xhigh"];

    public static string Code(ThinkLevel l) => l switch { ThinkLevel.Fast => "fast", ThinkLevel.Deep => "deep", _ => "standard" };
    public static string Label(ThinkLevel l) => l switch { ThinkLevel.Fast => "快速", ThinkLevel.Deep => "深度", _ => "标准" };

    public static ThinkLevel? Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "fast" => ThinkLevel.Fast,
        "standard" => ThinkLevel.Standard,
        "deep" => ThinkLevel.Deep,
        _ => null
    };

    /// <summary>出厂默认值（后台可改）。</summary>
    public static LevelSettings Default(ThinkLevel l) => l switch
    {
        ThinkLevel.Fast => new("off", 3, 2000, 0.7),
        ThinkLevel.Deep => new("xhigh", 10, 12000, 1.0),
        _ => new("medium", 6, 6000, 1.0)
    };

    public static Sampling SamplingFor(bool think) => think ? new(0.95, 20, 0) : new(0.8, 20, 1.5);

    /// <summary>定这次用哪一档。requested：输入框里选的（空或 auto = 自动）；员工不能自己选时只看后台的默认档位。</summary>
    public static LevelChoice Resolve(string? requested, string? defaultLevel, bool userChoice, bool deepAvailable, string question, bool hasAttachments)
    {
        var pick = (userChoice ? Parse(requested) : null) ?? Parse(defaultLevel);
        LevelChoice c;
        if (pick is null)
        {
            var (level, reason) = Auto(question, hasAttachments);
            c = new LevelChoice(level, true, reason);
        }
        else c = new LevelChoice(pick.Value, false, null);
        if (c.Level == ThinkLevel.Deep && !deepAvailable)
            c = new LevelChoice(ThinkLevel.Standard, c.Auto, "深度档要 32K 上下文，这次按标准答");
        return c;
    }

    private static readonly string[] Heavy =
    [
        "选型", "怎么选", "选哪", "推荐", "计算", "算一下", "核算", "校核", "多大", "对比", "比较", "区别",
        "方案", "设计", "标准", "规范", "为什么", "原因", "分析", "评估", "能不能用", "行不行", "故障", "报价", "技术协议", "偏离"
    ];

    /// <summary>自动：带了文件、问选型计算对比方案标准这类、或者问题比较长的，先想一想（标准）；其余直接答（快速）。深度只能手动选。</summary>
    public static (ThinkLevel Level, string Reason) Auto(string question, bool hasAttachments)
    {
        if (hasAttachments) return (ThinkLevel.Standard, "带了文件");
        var q = question ?? "";
        var hit = Heavy.FirstOrDefault(q.Contains);
        if (hit is not null) return (ThinkLevel.Standard, $"问题里有「{hit}」");
        if (Regex.Replace(q, @"\s+", "").Length > 60) return (ThinkLevel.Standard, "问题比较长");
        return (ThinkLevel.Fast, "简单问题");
    }

    /// <summary>「查了几步」里思考那一行的说明，比如「标准（自动：问题里有「选型」）· 想了 9 秒」。</summary>
    public static string Describe(LevelChoice c, bool think, double seconds)
    {
        var head = Label(c.Level) + (c.Reason is null ? "" : c.Auto ? $"（自动：{c.Reason}）" : $"（{c.Reason}）");
        if (!think) return head + " · 不思考，直接答";
        return seconds >= 0.5 ? $"{head} · 想了 {Math.Round(seconds)} 秒" : head;
    }
}
