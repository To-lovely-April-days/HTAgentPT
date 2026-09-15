using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/retrieve")]
[Authorize]
public class RetrieveController(IRetrievalService retrieval, ICurrentUser me, IAuditWriter audit) : ControllerBase
{
    /// <summary>检索（表 8-1 POST /api/retrieve）：返回分块与分值。问答与生成的公共底座。</summary>
    [HttpPost]
    public async Task<IActionResult> Retrieve([FromBody] RetrievalRequest req, CancellationToken ct)
    {
        // 有任一问答权限即可检索；范围由 RetrievalService 按角色收敛（表 3-1 数据范围）
        if (!me.Permissions.Contains(PermissionKeys.QaInternal) &&
            !me.Permissions.Contains(PermissionKeys.QaPublic))
        {
            await audit.WriteAsync(new AuditEntry("authz.denied", AuditResult.Denied,
                UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                Detail: new { permission = "qa.*", path = "/api/retrieve" }, Ip: me.Ip), ct);
            return StatusCode(403, new { code = "FORBIDDEN", message = "你的角色没有检索权限。本次请求已被记录。" });
        }
        var result = await retrieval.RetrieveAsync(req, ct);
        // FR-9.1「记录检索…全部操作」：独立检索口与问答口同等留痕
        await audit.WriteAsync(new AuditEntry("retrieve.query", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new
            {
                query = req.Query,
                hits = result.Chunks.Count,
                aboveThreshold = result.AboveThreshold,
                topScore = result.TopScore
            }, Ip: me.Ip, TerminalId: me.TerminalId), ct);
        return Ok(result);
    }
}
