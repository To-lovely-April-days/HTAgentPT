using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Services;

/// <summary>后台设的思考档位：默认档位、员工能不能自己选、模型服务的上下文长度、三档各自的参数。</summary>
public record LevelConfig(string Default, bool UserChoice, int ContextTokens, IReadOnlyDictionary<ThinkLevel, LevelSettings> Levels)
{
    /// <summary>上下文到 32K 才开放深度档。</summary>
    public bool DeepAvailable => ContextTokens >= ThinkLevels.DeepMinContext;

    /// <summary>上下文长度：模型服务报了 max_model_len 就按它（这是实际值，后台填的数可能是旧的）；
    /// 报不出来（在线接口、服务没起来）才按后台填的，没填按 8192。</summary>
    public static async Task<LevelConfig> LoadAsync(IRuntimeConfig config, CancellationToken ct, ModelContextProbe? probe = null)
    {
        var levels = new Dictionary<ThinkLevel, LevelSettings>();
        foreach (var (level, effort, rounds, tokens, temp) in new[]
        {
            (ThinkLevel.Fast, ConfigKeys.LevelFastEffort, ConfigKeys.LevelFastRounds, ConfigKeys.LevelFastMaxTokens, ConfigKeys.LevelFastTemperature),
            (ThinkLevel.Standard, ConfigKeys.LevelStandardEffort, ConfigKeys.LevelStandardRounds, ConfigKeys.LevelStandardMaxTokens, ConfigKeys.LevelStandardTemperature),
            (ThinkLevel.Deep, ConfigKeys.LevelDeepEffort, ConfigKeys.LevelDeepRounds, ConfigKeys.LevelDeepMaxTokens, ConfigKeys.LevelDeepTemperature),
        })
        {
            var d = ThinkLevels.Default(level);
            var e = (await config.GetStringAsync(effort, d.Effort, ct)).Trim().ToLowerInvariant();
            levels[level] = new LevelSettings(
                ThinkLevels.Efforts.Contains(e) ? e : d.Effort,
                Math.Clamp(await config.GetIntAsync(rounds, d.MaxRounds, ct), 1, 16),
                Math.Clamp(await config.GetIntAsync(tokens, d.MaxTokens, ct), 512, 64000),
                Math.Clamp(await config.GetDoubleAsync(temp, d.Temperature, ct), 0, 2));
        }
        var def = (await config.GetStringAsync(ConfigKeys.AssistantLevelDefault, "auto", ct)).Trim().ToLowerInvariant();
        var choice = !(await config.GetStringAsync(ConfigKeys.AssistantLevelUserChoice, "true", ct)).Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
        var set = Math.Max(0, await config.GetIntAsync(ConfigKeys.ChatContextTokens, 0, ct));
        var context = (probe is null ? null : await probe.DetectAsync(ct)) ?? (set > 0 ? set : 8192);
        return new LevelConfig(def, choice, context, levels);
    }
}
