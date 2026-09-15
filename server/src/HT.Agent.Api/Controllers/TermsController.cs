using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>术语表（FR-3.5、FR-6.4）。读与提交=翻译权限；维护与审定=元数据管理权限。</summary>
[ApiController]
[Route("api/terms")]
[Authorize]
public class TermsController(ITermService terms, ICurrentUser me, IAuditWriter audit) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? domain, [FromQuery] string? status, CancellationToken ct)
    {
        // 术语是内部资料，客户角色不可读；翻译或元数据权限皆可
        if (!me.Permissions.Contains(PermissionKeys.Translate) &&
            !me.Permissions.Contains(PermissionKeys.MetaManage))
        {
            await audit.WriteAsync(new AuditEntry("authz.denied", AuditResult.Denied,
                UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                Detail: new { path = "/api/terms" }, Ip: me.Ip, TerminalId: me.TerminalId), ct);
            return StatusCode(403, new { code = "FORBIDDEN", message = "你的角色不能读取术语表。本次请求已被记录。" });
        }
        return Ok(await terms.ListAsync(domain, status, ct));
    }

    [HttpPost]
    [RequirePermission(PermissionKeys.MetaManage)]
    public async Task<IActionResult> Add([FromBody] TermEdit edit, CancellationToken ct)
        => Ok(new { id = await terms.AddAsync(edit, ct) });

    public record ImportBody(List<TermEdit> Rows);

    /// <summary>批量导入（FR-3.5）。</summary>
    [HttpPost("import")]
    [RequirePermission(PermissionKeys.MetaManage)]
    public async Task<IActionResult> Import([FromBody] ImportBody body, CancellationToken ct)
        => Ok(new { imported = await terms.ImportAsync(body.Rows, ct) });

    /// <summary>翻译结果中标记译法不当并提交补充（FR-6.4），待管理员确认。</summary>
    [HttpPost("suggest")]
    [RequirePermission(PermissionKeys.Translate)]
    public async Task<IActionResult> Suggest([FromBody] TermEdit edit, CancellationToken ct)
        => Ok(new { id = await terms.SuggestAsync(edit, ct) });

    [HttpPost("{id:guid}/approve")]
    [RequirePermission(PermissionKeys.MetaManage)]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        await terms.ApproveAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reject")]
    [RequirePermission(PermissionKeys.MetaManage)]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        await terms.RejectAsync(id, ct);
        return NoContent();
    }
}
