using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/kbs")]
[Authorize]
public class KnowledgeBasesController(IKnowledgeBaseService kbs, ICurrentUser me, IAuditWriter audit) : ControllerBase
{
    /// <summary>库清单与库级统计（FR-2.5）属内部信息：客户角色连私有库的存在与更新节奏都不该看到（3.4）。</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var internalFacing = me.Permissions.Contains(PermissionKeys.QaInternal) ||
                             me.Permissions.Contains(PermissionKeys.CorpusManage) ||
                             me.Permissions.Contains(PermissionKeys.KbManage);
        if (!internalFacing)
        {
            await audit.WriteAsync(new AuditEntry("authz.denied", AuditResult.Denied,
                UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                Detail: new { path = "/api/kbs" }, Ip: me.Ip, TerminalId: me.TerminalId), ct);
            return StatusCode(403, new { code = "FORBIDDEN", message = "你的角色不能查看知识库清单。本次请求已被记录。" });
        }
        return Ok(await kbs.ListAsync(ct));
    }

    public record CreateBody(string Name, KnowledgeBaseTier Tier, ChunkStrategy DefaultChunkStrategy, string? Description);

    [HttpPost]
    [RequirePermission(PermissionKeys.KbManage)]
    public async Task<IActionResult> Create([FromBody] CreateBody body, CancellationToken ct)
        => Ok(new { id = await kbs.CreateAsync(body.Name, body.Tier, body.DefaultChunkStrategy, body.Description, ct) });

    public record UpdateBody(string Name, ChunkStrategy DefaultChunkStrategy, string? Description);

    /// <summary>tier 不在请求体里——创建后不可修改（FR-2.1），接口层就没有这条路。</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionKeys.KbManage)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateBody body, CancellationToken ct)
    {
        await kbs.UpdateAsync(id, body.Name, body.DefaultChunkStrategy, body.Description, ct);
        return NoContent();
    }
}
