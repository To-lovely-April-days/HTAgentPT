using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using HT.Agent.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

/// <summary>运维（E0/E16 数据面）：备份状态、告警、远程接入综合视图。</summary>
[ApiController]
[Route("api/ops")]
[Authorize]
[RequirePermission(PermissionKeys.SystemConfig)]
public class OpsController(AppDbContext db, BackupService backup, IRuntimeConfig config,
    IAuditWriter audit, ICurrentUser me) : ControllerBase
{
    /// <summary>最近一次各类备份的时间、结果与大小（FR-9.4），失败突出由前端按 ok=false 渲染。</summary>
    [HttpGet("backup/status")]
    public async Task<IActionResult> BackupStatus(CancellationToken ct)
    {
        var remoteConfigured = (await config.GetStringAsync(ConfigKeys.BackupRemoteDir, "", ct)).Length > 0;
        var kinds = new[] { BackupKind.LocalIncremental, BackupKind.LocalFull, BackupKind.RemoteFull };
        var rows = new List<object>();
        foreach (var kind in kinds)
        {
            var last = await db.BackupRuns.AsNoTracking()
                .Where(r => r.Kind == kind).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
            rows.Add(new
            {
                kind = kind.ToString(),
                configured = kind != BackupKind.RemoteFull || remoteConfigured,
                last = last is null ? null : new
                {
                    last.StartedAt, last.FinishedAt, last.Ok, last.SizeBytes, last.Target, last.Error,
                    last.AcknowledgedBy, last.AcknowledgedAt
                }
            });
        }
        return Ok(new
        {
            rows,
            // 10.3 的那句话常驻在数据里：向量索引不备份、由原始文件重建
            note = "备份覆盖原始文件、业务数据库与配置；向量索引不纳入常规备份，恢复后由原始文件重建。"
        });
    }

    /// <summary>手动执行一类备份（E16「立即执行」）。</summary>
    public record RunBody(BackupKind Kind);

    [HttpPost("backup/run")]
    public async Task<IActionResult> RunBackup([FromBody] RunBody body, CancellationToken ct)
    {
        var run = await backup.RunAsync(body.Kind, me.UserId, ct);
        return Ok(new { run.Id, run.Ok, run.Target, run.SizeBytes, run.Error });
    }

    /// <summary>告警清单（E0 告警条数据源）：失败且此后同类型未成功过的备份。
    /// 「知悉」只入审计不撤告警——告警随同类型下一次成功自动消失（FR-9.3 不得仅写日志的界面语义）。</summary>
    [HttpGet("alerts")]
    public async Task<IActionResult> Alerts(CancellationToken ct)
    {
        var alerts = new List<object>();
        foreach (var kind in Enum.GetValues<BackupKind>())
        {
            var last = await db.BackupRuns.AsNoTracking()
                .Where(r => r.Kind == kind && r.Ok != null)
                .OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
            if (last is not { Ok: false }) continue;
            var failStreak = await db.BackupRuns.AsNoTracking()
                .Where(r => r.Kind == kind && r.Ok == false &&
                            r.StartedAt > (db.BackupRuns.Where(x => x.Kind == kind && x.Ok == true)
                                .Max(x => (DateTimeOffset?)x.StartedAt) ?? DateTimeOffset.MinValue))
                .CountAsync(ct);
            alerts.Add(new
            {
                kind = kind.ToString(), runId = last.Id, last.StartedAt, last.Error,
                consecutiveFailures = failStreak, last.AcknowledgedBy, last.AcknowledgedAt
            });
        }
        return Ok(alerts);
    }

    /// <summary>知悉告警：只记审计与知悉人，不撤告警（E0 语义）。</summary>
    [HttpPost("backup/{runId:guid}/ack")]
    public async Task<IActionResult> Acknowledge(Guid runId, CancellationToken ct)
    {
        var run = await db.BackupRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return NotFound();
        run.AcknowledgedBy = me.UserId;
        run.AcknowledgedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("backup.acknowledge", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "backup_run", TargetId: runId.ToString(),
            Detail: new { note = "知悉不撤告警，须同类型备份成功一次才消" }), ct);
        return NoContent();
    }

    /// <summary>远程接入综合视图（FR-9.7，E16）：绑定关系 + 最近接入（来自审计，与操作日志同一时间线）。</summary>
    [HttpGet("remote-access")]
    public async Task<IActionResult> RemoteAccess(CancellationToken ct)
    {
        var bindings = await db.UserTerminals.AsNoTracking()
            .Join(db.Users.AsNoTracking(), t => t.UserId, u => u.Id, (t, u) => new
            {
                t.Id, t.TerminalId, t.Name, t.IsActive, t.BoundAt, t.LastSeenAt, t.LastIp,
                user = u.Username, display = u.DisplayName,
                online = u.ActiveTerminalId == t.TerminalId && u.ActiveSessionId != null
            })
            .OrderBy(x => x.user).ToListAsync(ct);
        var recentLogins = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "auth.login" && a.At > DateTimeOffset.UtcNow.AddDays(-7))
            .OrderByDescending(a => a.At).Take(100)
            .Select(a => new { a.At, a.Username, a.Result, a.Ip, a.TerminalId })
            .ToListAsync(ct);
        return Ok(new { bindings, recentLogins });
    }
}
