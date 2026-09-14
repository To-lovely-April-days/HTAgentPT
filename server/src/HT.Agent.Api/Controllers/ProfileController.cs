using HT.Agent.Application.Abstractions;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

/// <summary>个人中心（C3）。只看自己的，不需要额外权限键。</summary>
[ApiController]
[Route("api/profile")]
[Authorize]
public class ProfileController(AppDbContext db, IUserAdminService users, ICurrentUser me) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Role).Include(u => u.Company)
            .FirstAsync(u => u.Id == me.UserId, ct);
        var terminals = await db.UserTerminals.AsNoTracking()
            .Where(t => t.UserId == me.UserId && t.IsActive)
            .Select(t => new { t.Id, t.TerminalId, t.Name, t.LastSeenAt, t.LastIp })
            .ToListAsync(ct);
        return Ok(new
        {
            user.Username,
            user.DisplayName,
            user.EmployeeNo,
            user.Department,
            role = user.Role == null ? null : new { user.Role.Code, user.Role.Name },
            company = user.Company!.Name,
            classifications = me.Classifications,
            // 决策 2：不可自助修改；界面展示上次重置的时间与经手人
            password = new { selfServiceChange = false, user.PasswordResetAt, user.PasswordResetBy },
            currentTerminal = user.ActiveTerminalId,
            user.LastLoginAt,
            terminals
        });
    }

    /// <summary>我的操作记录（近 N 天分页查询，C3 列表区）。</summary>
    [HttpGet("my-audit")]
    public async Task<IActionResult> MyAudit([FromQuery] int days = 30, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 366));
        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.UserId == me.UserId && a.At >= from)
            .OrderByDescending(a => a.At).Take(Math.Clamp(limit, 1, 1000))
            .Select(a => new { a.Id, a.At, a.Action, a.TargetType, a.TargetId, a.Result, a.Detail })
            .ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>自助导出（FR-7.5 的个人侧）：与管理员离职导出走同一查询、同一 CSV 编码。</summary>
    [HttpGet("my-audit/export")]
    public async Task<IActionResult> ExportMyAudit(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct)
    {
        var rows = await users.ExportUserAuditAsync(me.UserId, from, to, ct);
        return File(AuditCsv.Build(rows), "text/csv; charset=utf-8", "my-operations.csv");
    }
}
