using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
[RequirePermission(PermissionKeys.UserManage)]
public class UsersController(IUserAdminService users) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeInactive = true, CancellationToken ct = default)
        => Ok(await users.ListAsync(includeInactive, ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req, CancellationToken ct)
        => Ok(new { id = await users.CreateAsync(req, ct) });

    public record AssignRoleBody(Guid RoleId);

    [HttpPost("{id:guid}/role")]
    public async Task<IActionResult> AssignRole(Guid id, [FromBody] AssignRoleBody body, CancellationToken ct)
    {
        await users.AssignRoleAsync(id, body.RoleId, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        await users.DeactivateAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reactivate")]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct)
    {
        await users.ReactivateAsync(id, ct);
        return NoContent();
    }

    public record ResetPasswordBody(string NewPassword);

    /// <summary>重置口令（决策 2：系统无自助改密，这是唯一的改口令入口，入审计）。</summary>
    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id, [FromBody] ResetPasswordBody body, CancellationToken ct)
    {
        await users.ResetPasswordAsync(id, body.NewPassword, ct);
        return NoContent();
    }

    /// <summary>离职回收导出（FR-7.5）。返回 CSV。</summary>
    [HttpGet("{id:guid}/audit-export")]
    public async Task<IActionResult> ExportAudit(Guid id,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct)
    {
        var rows = await users.ExportUserAuditAsync(id, from, to, ct);
        return File(AuditCsv.Build(rows), "text/csv; charset=utf-8", $"audit-{id:N}.csv");
    }

    public record BindTerminalBody(string TerminalId, string Name);

    [HttpPost("{id:guid}/terminals")]
    public async Task<IActionResult> BindTerminal(Guid id, [FromBody] BindTerminalBody body, CancellationToken ct)
    {
        await users.BindTerminalAsync(id, body.TerminalId, body.Name, ct);
        return NoContent();
    }

    [HttpDelete("terminals/{bindingId:guid}")]
    public async Task<IActionResult> UnbindTerminal(Guid bindingId, CancellationToken ct)
    {
        await users.UnbindTerminalAsync(bindingId, ct);
        return NoContent();
    }
}

/// <summary>审计导出的 CSV 编码。管理员导出与个人中心自助导出共用——两个入口一份数据（C3）。</summary>
public static class AuditCsv
{
    public static byte[] Build(IReadOnlyList<AuditRow> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("id,at,username,action,target_type,target_id,result,ip,terminal_id,detail");
        foreach (var r in rows)
            sb.AppendLine(string.Join(',',
                r.Id, r.At.ToString("O"), Csv(r.Username), Csv(r.Action),
                Csv(r.TargetType), Csv(r.TargetId), r.Result, Csv(r.Ip), Csv(r.TerminalId), Csv(r.Detail)));
        return System.Text.Encoding.UTF8.GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Csv(string? v) =>
        v is null ? "" : '"' + v.Replace("\"", "\"\"") + '"';
}
