using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>系统设置（FR-9.5/9.6）：模型地址与运行参数界面可改，改后即时生效，无需重启。</summary>
[ApiController]
[Route("api/config")]
[Authorize]
[RequirePermission(PermissionKeys.SystemConfig)]
public class ConfigController(IRuntimeConfig config, ICurrentUser me, IAuditWriter audit) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
        => Ok(await config.GetAllAsync(ct));

    public record SetBody(Dictionary<string, string> Values);

    [HttpPut]
    public async Task<IActionResult> Set([FromBody] SetBody body, CancellationToken ct)
    {
        foreach (var (key, value) in body.Values)
            await config.SetAsync(key, value, me.UserId, ct);
        // 配置变更入审计（FR-9.1：权限变更之外，运行参数变更同样要留痕）
        await audit.WriteAsync(new AuditEntry("config.update", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new { keys = body.Values.Keys }), ct);
        return NoContent();
    }
}
