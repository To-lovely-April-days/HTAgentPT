using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/vocab")]
[Authorize]
public class VocabController(IVocabService vocab, ICurrentUser me, IAuditWriter audit) : ControllerBase
{
    /// <summary>读词表限员工侧权限——客户名称词表就是全公司客户名册（含别名），
    /// 外部客户角色（仅 qa.public + customer.self）不得枚举（3.4）。维护动作另要求 meta.manage。</summary>
    [HttpGet("{key}")]
    public async Task<IActionResult> List(string key, CancellationToken ct)
    {
        var employeeFacing = me.Permissions.Contains(PermissionKeys.QaInternal) ||
                             me.Permissions.Contains(PermissionKeys.CorpusManage) ||
                             me.Permissions.Contains(PermissionKeys.MetaManage) ||
                             me.Permissions.Contains(PermissionKeys.ProjectSearch);
        if (!employeeFacing)
        {
            await audit.WriteAsync(new AuditEntry("authz.denied", AuditResult.Denied,
                UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                Detail: new { path = $"/api/vocab/{key}" }, Ip: me.Ip, TerminalId: me.TerminalId), ct);
            return StatusCode(403, new { code = "FORBIDDEN", message = "你的角色不能读取词表。本次请求已被记录。" });
        }
        return Ok(await vocab.ListAsync(key, ct));
    }

    public record AddBody(string Value, string? Aliases);

    [HttpPost("{key}")]
    [RequirePermission(PermissionKeys.MetaManage)]
    public async Task<IActionResult> Add(string key, [FromBody] AddBody body, CancellationToken ct)
        => Ok(new { id = await vocab.AddAsync(key, body.Value, body.Aliases, ct) });

    [HttpDelete("items/{id:guid}")]
    [RequirePermission(PermissionKeys.MetaManage)]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct)
    {
        await vocab.DisableAsync(id, ct);
        return NoContent();
    }
}
