using HT.Agent.Application.Abstractions;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HT.Agent.Infrastructure.Workers;

/// <summary>助手做的文件过了保留期就删文件（记录留着，后台看用量和失败率）；记录本身 90 天后删。每小时看一次。</summary>
public class MadeFileCleanupWorker(IServiceScopeFactory scopeFactory, ILogger<MadeFileCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "清理助手做的文件出错"); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var now = DateTimeOffset.UtcNow;
        var due = await db.MadeFiles.Where(f => f.FileKey != null && f.ExpiresAt < now).Take(500).ToListAsync(ct);
        foreach (var f in due)
        {
            try { await storage.DeleteAsync(f.FileKey!, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "删不掉 {Key}", f.FileKey); continue; }
            f.FileKey = null;
        }
        var old = now.AddDays(-90);
        await db.MadeFiles.Where(f => f.FileKey == null && f.CreatedAt < old).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
    }
}
