using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

/// <summary>审计查询（FR-9.2）：按人员、时间区间、操作类型、涉及对象组合查询并导出。</summary>
[ApiController]
[Route("api/audit")]
[Authorize]
[RequirePermission(PermissionKeys.AuditRead)]
public class AuditController(AppDbContext db) : ControllerBase
{
    [HttpGet("logs")]
    public async Task<IActionResult> Logs(
        [FromQuery] string? username, [FromQuery] string? action,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? targetType, [FromQuery] string? targetId,
        [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var q = Filter(db.AuditLogs.AsNoTracking(), username, action, from, to, targetType, targetId);
        return Ok(await q.OrderByDescending(a => a.At).Take(Math.Clamp(limit, 1, 2000))
            .Select(a => new { a.Id, a.At, a.Username, a.Action, a.TargetType, a.TargetId, a.Result, a.Ip, a.TerminalId, a.Detail })
            .ToListAsync(ct));
    }

    [HttpGet("logs/export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? username, [FromQuery] string? action,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? targetType, [FromQuery] string? targetId,
        CancellationToken ct = default)
    {
        var q = Filter(db.AuditLogs.AsNoTracking(), username, action, from, to, targetType, targetId);
        var rows = await q.OrderByDescending(a => a.At).Take(100_000)
            .Select(a => new AuditRow(a.Id, a.At, a.Username, a.Action, a.TargetType, a.TargetId, a.Detail, a.Result, a.Ip, a.TerminalId))
            .ToListAsync(ct);
        return File(AuditCsv.Build(rows), "text/csv; charset=utf-8", "audit-logs.csv");
    }

    private static IQueryable<Domain.Entities.AuditLog> Filter(
        IQueryable<Domain.Entities.AuditLog> q,
        string? username, string? action, DateTimeOffset? from, DateTimeOffset? to,
        string? targetType, string? targetId)
    {
        if (!string.IsNullOrWhiteSpace(username)) q = q.Where(a => a.Username == username);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
        if (from is not null) q = q.Where(a => a.At >= from);
        if (to is not null) q = q.Where(a => a.At < to);
        if (!string.IsNullOrWhiteSpace(targetType)) q = q.Where(a => a.TargetType == targetType);
        if (!string.IsNullOrWhiteSpace(targetId)) q = q.Where(a => a.TargetId == targetId);
        return q;
    }
}
