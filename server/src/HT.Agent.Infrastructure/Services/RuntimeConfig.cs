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
        [ConfigKeys.ChatContextTokens] = "8192",
        // 做文件：先开 Excel 和 Word；PPT、PDF 用测试题跑过、成功率够了再开
        [ConfigKeys.FileXlsx] = "on",
        [ConfigKeys.FileDocx] = "on",
        [ConfigKeys.FilePptx] = "off",
        [ConfigKeys.FilePdf] = "off",
        [ConfigKeys.FileKeepDays] = "7",
        [ConfigKeys.Delegate] = "on",
        [ConfigKeys.BrowserUrl] = "http://browser:8100",
        [ConfigKeys.DelegateMaxTasks] = "4",
        [ConfigKeys.DelegateParallel] = "3",
        [ConfigKeys.DelegateRounds] = "5",
        [ConfigKeys.EmbeddingUrl] = "http://127.0.0.1:8080/embed",
        [ConfigKeys.EmbeddingModelName] = "embedding-default",
        [ConfigKeys.EmbeddingDimension] = "1024",
        [ConfigKeys.EmbeddingBatchSize] = "32",
        [ConfigKeys.RerankUrl] = "http://127.0.0.1:8081/rerank",
        [ConfigKeys.RerankModelName] = "rerank-default",
        [ConfigKeys.ParserUrl] = "http://127.0.0.1:8082/parse",
        [ConfigKeys.ParserBackend] = "pipeline",
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
        [ConfigKeys.QaEngine] = "assistant",
        [ConfigKeys.AssistantLevelDefault] = "auto",
        [ConfigKeys.AssistantLevelUserChoice] = "true",
        [ConfigKeys.LevelFastEffort] = "off",
        [ConfigKeys.LevelFastRounds] = "3",
        [ConfigKeys.LevelFastMaxTokens] = "2000",
        [ConfigKeys.LevelFastTemperature] = "0.7",
        [ConfigKeys.LevelStandardEffort] = "medium",
        [ConfigKeys.LevelStandardRounds] = "6",
        [ConfigKeys.LevelStandardMaxTokens] = "6000",
        [ConfigKeys.LevelStandardTemperature] = "1.0",
        [ConfigKeys.LevelDeepEffort] = "xhigh",
        [ConfigKeys.LevelDeepRounds] = "10",
        [ConfigKeys.LevelDeepMaxTokens] = "12000",
        [ConfigKeys.LevelDeepTemperature] = "1.0",
        [ConfigKeys.AssistantContextChars] = "36000",
        [ConfigKeys.AssistantSearchTopK] = "6",
        [ConfigKeys.ToolAskUser] = "on",
        [ConfigKeys.ToolPython] = "on",
        [ConfigKeys.ToolChemical] = "on",
        [ConfigKeys.ToolWebSearch] = "on",    // 没部署 SearXNG 时调用会提示「这次没搜到」，不影响其它工具
        [ConfigKeys.ToolFetchUrl] = "on",
        [ConfigKeys.ToolCompare] = "on",
        [ConfigKeys.SandboxUrl] = "http://sandbox:8000",
        [ConfigKeys.DwsimUrl] = "http://dwsim:5901",
        [ConfigKeys.DwsimFilesDir] = "/dwsim-files",
        [ConfigKeys.SandboxTimeout] = "30",
        [ConfigKeys.SearxUrl] = "http://searxng:8080",
        [ConfigKeys.SearxEngines] = "bing,baidu,sogou",
        [ConfigKeys.HazchemFile] = "hazchem.csv",
        [ConfigKeys.MemoryEnabled] = "on",
        [ConfigKeys.MemoryMax] = "50",
        [ConfigKeys.EpoKey] = "",
        [ConfigKeys.EpoSecret] = "",
        [ConfigKeys.OpenAlexMailto] = "",
        [ConfigKeys.ToolLiterature] = "on",
        [ConfigKeys.ToolVision] = "on",
        [ConfigKeys.ToolTranslateFile] = "on",
        [ConfigKeys.ToolInquiryForm] = "on",
        [ConfigKeys.ToolPrices] = "on",
        [ConfigKeys.ToolShowTable] = "on",
        [ConfigKeys.ToolShowBlock] = "on",
        [ConfigKeys.ChunkContextSummary] = "on",
        [ConfigKeys.PriceStaleMonths] = "12",
        [ConfigKeys.PriceTaxRate] = "1.13",
        [ConfigKeys.AssistantProfile] = "霍桐仪器专业生产反应釜，是反应釜、固定床和工艺流程一体化解决方案的制造商：从实验室微型、平行、自动升降反应釜，到 5–30000 L 的生产中试釜和按工艺定制的成套反应装置、连续流反应装置；有压力容器特种设备生产许可证、CE 和 ISO9001 认证；客户以高校、科研院所和石油、化工、制药、新材料、能源、环保企业为主。总部在上海，北京、广州、安徽设有分公司。",
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
