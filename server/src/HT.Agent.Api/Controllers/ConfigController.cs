using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>系统设置（FR-9.5/9.6）：模型地址与运行参数界面可改，改后即时生效，无需重启。</summary>
[ApiController]
[Route("api/config")]
[Authorize]
[RequirePermission(PermissionKeys.SystemConfig)]
public class ConfigController(IRuntimeConfig config, ICurrentUser me, IAuditWriter audit) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
        => Ok(await config.GetAllAsync(ct));

    public record SetBody(Dictionary<string, string> Values);

    /// <summary>已知配置键（ConfigKeys 全量常量反射）。未知键拒收——
    /// 打错键名静默入库后界面显示改了、实际走默认值，这种「看着生效了」比报错更害人。</summary>
    private static readonly IReadOnlySet<string> KnownKeys = typeof(ConfigKeys)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Select(f => (string)f.GetValue(null)!)
        .ToHashSet();

    /// <summary>数值型键：写入前必须可解析，否则运行期静默回退默认值，界面与实际生效值分叉。</summary>
    private static readonly IReadOnlySet<string> NumericKeys = new HashSet<string>
    {
        ConfigKeys.ChatTemperature, ConfigKeys.ChatMaxTokens, ConfigKeys.EmbeddingDimension,
        ConfigKeys.EmbeddingBatchSize, ConfigKeys.ParserTimeoutSeconds, ConfigKeys.ParserMaxRetries,
        ConfigKeys.ParserConcurrency, ConfigKeys.RecallTopK, ConfigKeys.RerankTopN,
        ConfigKeys.ScoreThreshold, ConfigKeys.ContextTokens, ConfigKeys.MaxRetrievalPerTurn,
        ConfigKeys.HistoryTurns, ConfigKeys.HybridAlpha, ConfigKeys.AnnCandidateFactor,
        ConfigKeys.ChunkTargetLength, ConfigKeys.ChunkOverlap, ConfigKeys.ChunkMinLength,
        ConfigKeys.UploadMaxFileMb, ConfigKeys.UploadMaxBatch, ConfigKeys.JwtLifetimeMinutes,
        ConfigKeys.AuditRetentionMonths, ConfigKeys.TranslateBatchChars
    };

    [HttpPut]
    public async Task<IActionResult> Set([FromBody] SetBody body, CancellationToken ct)
    {
        var unknown = body.Values.Keys.Where(k => !KnownKeys.Contains(k)).ToList();
        if (unknown.Count > 0)
            return UnprocessableEntity(new { code = "CONFIG_UNKNOWN_KEY", message = $"未知配置键：{string.Join("、", unknown)}" });
        var bad = body.Values
            .Where(kv => NumericKeys.Contains(kv.Key) &&
                         !double.TryParse(kv.Value, System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(kv => kv.Key).ToList();
        if (bad.Count > 0)
            return UnprocessableEntity(new { code = "CONFIG_NOT_NUMERIC", message = $"这些键要求数值：{string.Join("、", bad)}" });

        foreach (var (key, value) in body.Values)
            await config.SetAsync(key, value, me.UserId, ct);
        // 配置变更入审计（FR-9.1：权限变更之外，运行参数变更同样要留痕）
        await audit.WriteAsync(new AuditEntry("config.update", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new { keys = body.Values.Keys }), ct);
        return NoContent();
    }
}
