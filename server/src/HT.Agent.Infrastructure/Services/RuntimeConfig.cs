using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace HT.Agent.Infrastructure.Services;

/// <summary>运行时配置（FR-9.6：界面可改，改后即时生效）。
/// 值以 JSON 字符串存 sys_config；进程内缓存 5 秒，写入即整体失效——
/// 「即时生效」的语义是下一次读取拿到新值，正在进行的会话下一轮生效（E15 文案与此一致）。</summary>
public class RuntimeConfig(IServiceScopeFactory scopeFactory, IMemoryCache cache) : IRuntimeConfig
{
    private const string CacheKey = "sys_config.all";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    /// <summary>默认值随标准部署包分发（10.4）。数据库有值即覆盖。</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        [ConfigKeys.ChatModelUrl] = "http://127.0.0.1:8000/v1",
        [ConfigKeys.ChatModelName] = "chat-default",
        [ConfigKeys.ChatTemperature] = "0.2",
        [ConfigKeys.ChatMaxTokens] = "2048",
        [ConfigKeys.EmbeddingUrl] = "http://127.0.0.1:8080/embed",
        [ConfigKeys.EmbeddingModelName] = "embedding-default",
        [ConfigKeys.EmbeddingDimension] = "1024",
        [ConfigKeys.EmbeddingBatchSize] = "32",
        [ConfigKeys.RerankUrl] = "http://127.0.0.1:8081/rerank",
        [ConfigKeys.RerankModelName] = "rerank-default",
        [ConfigKeys.ParserUrl] = "http://127.0.0.1:8082/parse",
        [ConfigKeys.ParserTimeoutSeconds] = "300",
        [ConfigKeys.ParserMaxRetries] = "3",
        [ConfigKeys.ParserConcurrency] = "1",
        [ConfigKeys.RecallTopK] = "50",
        [ConfigKeys.RerankTopN] = "8",
        // 重排分值非标定值，初值由测试集确定，试运行期以真实问题集标定（FR-4.7）
        [ConfigKeys.ScoreThreshold] = "0.62",
        [ConfigKeys.ContextTokens] = "6000",
        [ConfigKeys.MaxRetrievalPerTurn] = "3",
        [ConfigKeys.HistoryTurns] = "6",
        [ConfigKeys.HybridAlpha] = "0.6",
        [ConfigKeys.AnnCandidateFactor] = "4",
        [ConfigKeys.ChunkTargetLength] = "800",
        [ConfigKeys.ChunkOverlap] = "80",
        [ConfigKeys.ChunkMinLength] = "200",
        [ConfigKeys.UploadMaxFileMb] = "200",
        [ConfigKeys.UploadMaxBatch] = "50",
        [ConfigKeys.JwtLifetimeMinutes] = "480",
        [ConfigKeys.AuditRetentionMonths] = "12",
        [ConfigKeys.TranslateBatchChars] = "3000",
        // 项目编号识别正则（10.4：编号规则随部署配置，不硬编码）
        [ConfigKeys.IntentProjectNoPattern] = "P-\\d{4}-\\d+",
        [ConfigKeys.CaseNoPrefix] = "FC",
        [ConfigKeys.TicketNoPrefix] = "RT",
        [ConfigKeys.SyncHqUrl] = "",
        [ConfigKeys.SyncToken] = "",
        [ConfigKeys.SyncAcceptToken] = "",
        [ConfigKeys.PdfConverterUrl] = "",
        [ConfigKeys.BackupDir] = "data/backup",
        [ConfigKeys.BackupRemoteDir] = "",
        [ConfigKeys.BackupLocalIncrementalHours] = "6",
        [ConfigKeys.BackupLocalFullDays] = "7",
        [ConfigKeys.BackupRemoteFullDays] = "3"
    };

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct);
        if (all.TryGetValue(key, out var v)) return v;
        return Defaults.TryGetValue(key, out var d) ? d : null;
    }

    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default)
        => int.TryParse(await GetAsync(key, ct), out var v) ? v : fallback;

    public async Task<double> GetDoubleAsync(string key, double fallback, CancellationToken ct = default)
        => double.TryParse(await GetAsync(key, ct), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public async Task<string> GetStringAsync(string key, string fallback, CancellationToken ct = default)
        => await GetAsync(key, ct) ?? fallback;

    public async Task SetAsync(string key, string value, Guid? updatedBy, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.SysConfigs.FindAsync([key], ct);
        var json = JsonSerializer.Serialize(value);
        if (row is null)
            db.SysConfigs.Add(new SysConfig { Key = key, Value = json, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = updatedBy });
        else
        {
            row.Value = json;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedBy = updatedBy;
        }
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey); // 改后即时生效
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        var stored = await LoadAsync(ct);
        var merged = new Dictionary<string, string>(Defaults);
        foreach (var (k, v) in stored) merged[k] = v;
        return merged;
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        if (cache.TryGetValue<Dictionary<string, string>>(CacheKey, out var cached) && cached is not null)
            return cached;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.SysConfigs.AsNoTracking().ToListAsync(ct);
        var dict = rows.ToDictionary(
            r => r.Key,
            r => JsonSerializer.Deserialize<string>(r.Value) ?? r.Value);
        cache.Set(CacheKey, dict, Ttl);
        return dict;
    }
}
