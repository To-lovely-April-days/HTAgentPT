using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HT.Agent.Infrastructure.Services;

/// <summary>首页每天的内容（见 <see cref="HomeBrief"/>）。每人每天生成一次放进缓存，「换一批」每天最多 3 次。
/// 候选都按这个人的权限取：没有价格权限不看价格，资料只看他检索得到的库和密级，台账要有查询权限。</summary>
public class HomeService(AppDbContext db, ICurrentUser me, IRuntimeConfig config, IMemoryCache cache, IServiceProvider sp, ILogger<HomeService> logger)
{
    public const int MaxRefresh = 3;
    private static readonly ConcurrentDictionary<string, Task<HomeBrief.Result>> InFlight = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>公司在国内，按北京时间算「今天」（容器里一般是 UTC）。</summary>
    public static DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));

    public record Today(HomeBrief.Result Brief, List<string> Recent, string OffWork, int RefreshLeft, string Name);

    public async Task<Today> TodayAsync(bool refresh, CancellationToken ct)
    {
        var now = Now;
        var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var key = $"home:{me.UserId}:{day}";
        var rkey = $"home-refresh:{me.UserId}:{day}";
        var used = cache.Get<int?>(rkey) ?? 0;
        if (refresh && used < MaxRefresh)
        {
            cache.Remove(key);
            cache.Set(rkey, used + 1, now.Date.AddDays(1) - now.DateTime);
            used++;
        }

        var user = await db.Users.AsNoTracking().Where(u => u.Id == me.UserId)
            .Select(u => new { u.DisplayName, u.Department, u.MemoryOff, Role = u.Role != null ? u.Role.Name : null }).FirstOrDefaultAsync(ct);
        var name = user?.DisplayName ?? me.Username;
        var drafts = me.Permissions.Contains(PermissionKeys.Generate)
            ? await db.GenerationSessions.CountAsync(x => x.CreatedById == me.UserId && x.Status == GenerationStatus.Draft, ct) : 0;
        var recent = await db.QaSessions.AsNoTracking().Where(s => s.UserId == me.UserId)
            .OrderByDescending(s => s.UpdatedAt).Select(s => s.Title).Take(6).ToListAsync(ct);

        if (!cache.TryGetValue(key, out HomeBrief.Result? brief) || brief is null)
        {
            var task = InFlight.GetOrAdd(key, _ => BuildAsync(name, user?.Role ?? me.RoleCode, user?.Department, user?.MemoryOff ?? false, recent, drafts, now));
            try { brief = await task; }
            finally { InFlight.TryRemove(key, out _); }
            // 模型没写成的只缓存 10 分钟，过一会再试
            cache.Set(key, brief, brief.Generated ? now.Date.AddDays(1) - now.DateTime : TimeSpan.FromMinutes(10));
        }
        var offWork = (await config.GetStringAsync("home.off_work", "18:00", ct)).Trim();
        return new Today(brief, recent.Take(4).ToList(), offWork, Math.Max(0, MaxRefresh - used), name);
    }

    private async Task<HomeBrief.Result> BuildAsync(string name, string role, string? dept, bool memoryOff, List<string> recent, int drafts, DateTimeOffset now)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = cts.Token;
        List<string> memories = memoryOff ? [] : await db.UserMemories.AsNoTracking().Where(m => m.UserId == me.UserId)
            .OrderByDescending(m => m.UpdatedAt).Select(m => m.Text).Take(10).ToListAsync(ct);
        var candidates = new List<HomeBrief.Candidate>();
        try { candidates = await CandidatesAsync(memories, recent, now, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "首页：取近况失败"); }
        var ctx = new HomeBrief.Context(name, role, dept, now, memories, recent, candidates, drafts);

        var chat = sp.GetService<IChatModelClient>();
        if (chat is null) return HomeBrief.Fallback(ctx);
        try
        {
            var raw = await chat.CompleteAsync([new ChatTurn("user", HomeBrief.Prompt(ctx))], ct);
            return HomeBrief.Parse(raw, ctx);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogInformation("首页：模型没写成，用系统台词（{Error}）", ex.Message);
            return HomeBrief.Fallback(ctx);
        }
    }

    private static bool Mentioned(string text, IEnumerable<string> hay)
        => text.Length >= 2 && hay.Any(h => h.Contains(text, StringComparison.OrdinalIgnoreCase));

    private async Task<List<HomeBrief.Candidate>> CandidatesAsync(List<string> memories, List<string> recent, DateTimeOffset now, CancellationToken ct)
    {
        var list = new List<HomeBrief.Candidate>();
        var hay = memories.Concat(recent).ToList();
        var since7 = now.AddDays(-7);

        // ① 价格库：最近两周更新的表，系统比过新旧版本
        if (me.Permissions.Contains(PermissionKeys.PriceView) || me.Permissions.Contains(PermissionKeys.PriceManage))
        {
            var since = now.AddDays(-14);
            var versions = await (from v in db.PriceVersions.AsNoTracking()
                                  join t in db.PriceTables.AsNoTracking() on v.TableId equals t.Id
                                  where t.CompanyId == me.CompanyId && t.CurrentVersionId == v.Id && v.UploadedAt >= since
                                  orderby v.UploadedAt descending
                                  select new { t.Name, v.Diff, v.VersionDate }).Take(4).ToListAsync(ct);
            var i = 0;
            foreach (var v in versions)
            {
                PriceDiff? diff = null;
                try { diff = JsonSerializer.Deserialize<PriceDiff>(v.Diff, Json); } catch (JsonException) { }
                if (diff is null || diff.Up + diff.Down == 0) continue;
                var top = (diff.Top ?? []).OrderByDescending(x => Math.Abs(x.Pct)).FirstOrDefault();
                var hit = (diff.Top ?? []).FirstOrDefault(x => Mentioned(x.Name, hay));
                var focus = hit ?? top;
                var badge = focus is null ? $"{diff.Up} 涨 {diff.Down} 跌" : $"{(focus.Pct > 0 ? "+" : "")}{focus.Pct:0.#}%";
                var what = focus is null ? "" : $"{focus.Name}{(string.IsNullOrEmpty(focus.Spec) ? "" : " " + focus.Spec)}";
                var facts = $"价格表《{v.Name}》{v.VersionDate:M月d日} 更新：上涨 {diff.Up} 项、下降 {diff.Down} 项"
                    + (focus is null ? "" : $"；{what}{(focus.Supplier is null ? "" : $"（{focus.Supplier}）")} 从 {focus.Old:0.##} 到 {focus.New:0.##}（{badge}）")
                    + (hit is not null ? "；这个物料他最近问过或在跟" : "");
                list.Add(new($"P{++i}", "price", "📈", badge, focus is { Pct: < 0 } ? "ok" : "up", facts,
                    focus is null ? $"《{v.Name}》价格更新了" : $"{what} {(focus.Pct > 0 ? "涨价" : "降价")}了",
                    $"《{v.Name}》上涨 {diff.Up} 项、下降 {diff.Down} 项", "看看",
                    focus is null ? $"价格表《{v.Name}》最近更新了，哪些物料涨价最多？" : $"{what} 最近价格变了，对我在估的设备材料成本有什么影响？"));
            }
            // 管价格的：哪些表很久没更新
            if (me.Permissions.Contains(PermissionKeys.PriceManage))
            {
                var months = Math.Max(1, await config.GetIntAsync(ConfigKeys.PriceStaleMonths, 3, ct));
                var cutoff = DateOnly.FromDateTime(now.AddMonths(-months).DateTime);
                var stale = await (from t in db.PriceTables.AsNoTracking()
                                   join v in db.PriceVersions.AsNoTracking() on t.CurrentVersionId equals v.Id
                                   where t.CompanyId == me.CompanyId && v.VersionDate < cutoff
                                   orderby v.VersionDate
                                   select new { t.Name, v.VersionDate }).Take(2).ToListAsync(ct);
                foreach (var s in stale)
                {
                    var m = (now.Year - s.VersionDate.Year) * 12 + now.Month - s.VersionDate.Month;
                    list.Add(new($"P{++i}", "stale", "🗂️", $"{m} 个月", "warn",
                        $"价格表《{s.Name}》最近一版是 {s.VersionDate:yyyy年M月}，已经 {m} 个月没更新",
                        $"《{s.Name}》该更新了", $"最近一版是 {s.VersionDate:yyyy年M月}", "看看",
                        $"《{s.Name}》里哪些价格最可能已经过时？"));
                }
            }
        }

        // ② 资料库新入库：只算他检索得到的库和密级
        if (me.Permissions.Contains(PermissionKeys.QaInternal) || me.Permissions.Contains(PermissionKeys.QaPublic))
        {
            var tiers = new List<KnowledgeBaseTier>();
            if (me.Permissions.Contains(PermissionKeys.QaInternal))
            {
                tiers.Add(KnowledgeBaseTier.Shared);
                if (me.RoleCode != RoleCodes.HqReviewer) tiers.Add(KnowledgeBaseTier.Private);
            }
            if (me.Permissions.Contains(PermissionKeys.QaPublic)) tiers.Add(KnowledgeBaseTier.Public);
            var cls = me.Classifications.ToList();
            var docs = await db.Documents.AsNoTracking()
                .Where(d => d.UploadedAt >= since7 && !d.IsWithdrawn && d.ParseStatus == ParseStatus.Parsed && d.Review == ReviewStatus.Approved
                            && cls.Contains(d.Classification) && d.Kb != null && d.Kb.IsActive && tiers.Contains(d.Kb.Tier)
                            && (d.Kb.Tier == KnowledgeBaseTier.Shared || d.Kb.CompanyId == me.CompanyId))
                .OrderByDescending(d => d.UploadedAt)
                .Select(d => new { d.Title, Customer = d.Metadata != null ? d.Metadata.CustomerName : null, Category = d.Metadata != null ? d.Metadata.DocCategory : null })
                .Take(30).ToListAsync(ct);
            var mine = docs.Where(d => d.Customer is { Length: >= 2 } c && Mentioned(c, hay)).Take(2).ToList();
            var i = 0;
            foreach (var d in mine)
                list.Add(new($"D{++i}", "doc", "📄", "新文件", "info", $"资料库新入库《{d.Title}》（{d.Category}，客户 {d.Customer}），他在跟这个客户",
                    $"{d.Customer} 有新文件：{d.Title}", d.Category ?? "新入库", "看看", $"《{d.Title}》主要讲了什么？跟以前的版本比有什么变化？"));
            if (docs.Count > mine.Count)
                list.Add(new($"D{++i}", "docs", "📚", $"{docs.Count} 份", "info",
                    $"近 7 天资料库新入库 {docs.Count} 份，比如：{string.Join("、", docs.Take(4).Select(d => $"《{d.Title}》"))}",
                    $"资料库这周新增 {docs.Count} 份", string.Join("、", docs.Take(2).Select(d => d.Title)), "看看",
                    "资料库这周新入库了哪些文件？挑跟我工作有关的说说"));
        }

        // ③ 他负责的项目最近有变动
        if (me.Permissions.Contains(PermissionKeys.ProjectSearch))
        {
            var since3 = now.AddDays(-3);
            var projects = await db.Projects.AsNoTracking()
                .Where(p => p.CompanyId == me.CompanyId && p.OwnerId == me.UserId && p.UpdatedAt >= since3)
                .OrderByDescending(p => p.UpdatedAt).Take(2)
                .Select(p => new { p.ProjectNo, p.CustomerName, p.DeviceType, p.DeliveryStatus, p.UpdatedAt }).ToListAsync(ct);
            var i = 0;
            foreach (var p in projects)
            {
                var status = p.DeliveryStatus switch { DeliveryStatus.InProgress => "进行中", DeliveryStatus.Delivered => "已交付", DeliveryStatus.Closed => "已关闭", _ => "有更新" };
                list.Add(new($"J{++i}", "project", "⏰", status, "warn",
                    $"他负责的项目 {p.ProjectNo}（{p.CustomerName} · {p.DeviceType}）台账 {p.UpdatedAt.ToOffset(TimeSpan.FromHours(8)):M月d日} 有更新，状态：{status}",
                    $"{p.ProjectNo} {p.CustomerName} 有更新", $"{p.DeviceType} · {status}", "看看",
                    $"项目 {p.ProjectNo} 最近有什么变化？现在进展到哪一步？"));
            }
        }
        return list;
    }
}
