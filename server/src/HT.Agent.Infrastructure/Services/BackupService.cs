using System.Diagnostics;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace HT.Agent.Infrastructure.Services;

/// <summary>备份执行（FR-9.3/9.4）。实现口径（README 详述）：
/// 本地全量 = pg_dump 定制格式 + 原始文件目录归档；本地增量 = 数据库 pg_dump 滚动
/// （文件级增量与 WAL 归档属部署层，此处如实按「数据库快照」执行并在类型说明里写明）；
/// 异地全量 = 全量产物复制到配置的异地目录（挂载卷/同步盘），未配置即不执行并如实标注。
/// 失败告警不得仅写日志：失败落 backup_run + 审计，告警接口常驻直至同类型成功一次。</summary>
public class BackupService(
    AppDbContext db,
    IRuntimeConfig config,
    IConfiguration appConfig,
    IAuditWriter audit,
    ILogger<BackupService> logger)
{
    public async Task<BackupRun> RunAsync(BackupKind kind, Guid? requestedBy, CancellationToken ct = default)
    {
        var run = new BackupRun { Id = Guid.NewGuid(), Kind = kind, StartedAt = DateTimeOffset.UtcNow };
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(ct);
        try
        {
            var (target, size) = kind switch
            {
                BackupKind.LocalIncremental => await DumpDatabaseAsync(ct),
                BackupKind.LocalFull => await FullBackupAsync(ct),
                BackupKind.RemoteFull => await RemoteCopyAsync(ct),
                _ => throw new InvalidOperationException()
            };
            run.Ok = true;
            run.Target = target;
            run.SizeBytes = size;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Ok = false;
            run.Error = ex.Message;
            logger.LogError(ex, "备份失败 {Kind}", kind);
            // 失败须告警，不得仅写日志（FR-9.3）：审计一条 + 告警接口常驻展示
            await audit.WriteAsync(new AuditEntry("backup.failed", AuditResult.Failed,
                UserId: requestedBy, Username: requestedBy is null ? "backup-worker" : "-",
                TargetType: "backup_run", TargetId: run.Id.ToString(),
                Detail: new { kind = kind.ToString(), error = ex.Message }), ct);
        }
        run.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return run;
    }

    private async Task<(string, long)> DumpDatabaseAsync(CancellationToken ct)
    {
        var dir = Path.GetFullPath(await config.GetStringAsync(ConfigKeys.BackupDir, "data/backup", ct));
        Directory.CreateDirectory(dir);
        var conn = appConfig.GetConnectionString("Default")!;
        var parts = conn.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
        var file = Path.Combine(dir, $"db-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.dump");
        var psi = new ProcessStartInfo("pg_dump",
            $"-h {parts.GetValueOrDefault("host", "127.0.0.1")} -p {parts.GetValueOrDefault("port", "5432")} " +
            $"-U {parts.GetValueOrDefault("username", "postgres")} -Fc -f \"{file}\" {parts.GetValueOrDefault("database", "htagent")}")
        { RedirectStandardError = true };
        psi.Environment["PGPASSWORD"] = parts.GetValueOrDefault("password", "");
        using var proc = Process.Start(psi)!;
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"pg_dump 退出码 {proc.ExitCode}：{Truncate(stderr)}");
        return (file, new FileInfo(file).Length);
    }

    private async Task<(string, long)> FullBackupAsync(CancellationToken ct)
    {
        var (dbFile, dbSize) = await DumpDatabaseAsync(ct);
        // 原始文件与业务数据是备份的两样正主（10.3：向量索引可重建，不纳入常规备份）
        var storageRoot = Path.GetFullPath(appConfig["Storage:Root"] ?? "data/files");
        var dir = Path.GetDirectoryName(dbFile)!;
        var tarFile = Path.Combine(dir, $"files-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.tar.gz");
        long tarSize = 0;
        if (Directory.Exists(storageRoot))
        {
            var psi = new ProcessStartInfo("tar", $"-czf \"{tarFile}\" -C \"{storageRoot}\" .")
            { RedirectStandardError = true };
            using var proc = Process.Start(psi)!;
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"文件归档失败（tar 退出码 {proc.ExitCode}）：{Truncate(stderr)}");
            tarSize = new FileInfo(tarFile).Length;
        }
        return ($"{dbFile} + {tarFile}", dbSize + tarSize);
    }

    private async Task<(string, long)> RemoteCopyAsync(CancellationToken ct)
    {
        var remote = await config.GetStringAsync(ConfigKeys.BackupRemoteDir, "", ct);
        if (remote.Length == 0)
            throw new InvalidOperationException("异地目标未配置（backup.remote_dir）——异地容灾这条线现在是断的");
        var (local, size) = await FullBackupAsync(ct);
        Directory.CreateDirectory(remote);
        long copied = 0;
        foreach (var part in local.Split(" + "))
        {
            var dest = Path.Combine(remote, Path.GetFileName(part));
            File.Copy(part, dest, overwrite: true);
            copied += new FileInfo(dest).Length;
        }
        if (copied < size)
            throw new InvalidOperationException("异地副本大小与本地产物不一致");
        return (remote, copied);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
