using HT.Agent.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HT.Agent.Infrastructure.Workers;

/// <summary>启动后把系统自带的 PPT 模板导进模板库（分析每页、转缩略图）。沙箱、office 容器还没起来就过几分钟再试，最多试 6 次；
/// 后台「PPT 模板」里也能手动点「导入自带模板」。没挂载模板目录（比如总部节点）就什么都不做。</summary>
public class PptTemplateImportWorker(IServiceScopeFactory scopeFactory, ILogger<PptTemplateImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }
        for (var attempt = 0; attempt < 6 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<PptTemplateService>();
                if ((await svc.BuiltInsAsync(stoppingToken)).Count == 0) return;
                var (added, failed) = await svc.ImportBuiltInAsync(stoppingToken);
                if (added > 0) logger.LogInformation("导入了 {Added} 套自带 PPT 模板", added);
                if (failed == 0) return;
                logger.LogWarning("{Failed} 套自带 PPT 模板没导入成功（沙箱或 office 容器没起来？），过几分钟再试", failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "导入自带 PPT 模板出错"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
