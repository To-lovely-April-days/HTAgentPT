using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Persistence;
using HT.Agent.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HT.Agent.Infrastructure.Workers;

/// <summary>备份调度（FR-9.3：按策略执行）。间隔配置化：本地增量按小时、
/// 本地全量与异地全量按天；每 10 分钟核对一次到期任务。失败不重试轰炸——
/// 到下个核对周期再试，告警在失败当下已发出。</summary>
public class BackupWorker(IServiceScopeFactory scopeFactory, ILogger<BackupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动缓冲：让迁移与种子先落定
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "备份调度循环异常"); }
            try { await Task.Delay(Poll, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task RunDueAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IRuntimeConfig>();
        var backup = scope.ServiceProvider.GetRequiredService<BackupService>();

        var now = DateTimeOffset.UtcNow;
        var plan = new (BackupKind Kind, TimeSpan Interval)[]
        {
            (BackupKind.LocalIncremental, TimeSpan.FromHours(await config.GetIntAsync(ConfigKeys.BackupLocalIncrementalHours, 6, ct))),
            (BackupKind.LocalFull, TimeSpan.FromDays(await config.GetIntAsync(ConfigKeys.BackupLocalFullDays, 7, ct))),
            (BackupKind.RemoteFull, TimeSpan.FromDays(await config.GetIntAsync(ConfigKeys.BackupRemoteFullDays, 3, ct)))
        };
        foreach (var (kind, interval) in plan)
        {
            // 未配置异地目录时不空跑失败刷屏——状态接口会把「未配置」如实标出来
            if (kind == BackupKind.RemoteFull &&
                (await config.GetStringAsync(ConfigKeys.BackupRemoteDir, "", ct)).Length == 0)
                continue;
            var last = await db.BackupRuns.AsNoTracking()
                .Where(r => r.Kind == kind)
                .OrderByDescending(r => r.StartedAt)
                .Select(r => (DateTimeOffset?)r.StartedAt)
                .FirstOrDefaultAsync(ct);
            if (last is null || now - last >= interval)
                await backup.RunAsync(kind, requestedBy: null, ct);
        }
    }
}
