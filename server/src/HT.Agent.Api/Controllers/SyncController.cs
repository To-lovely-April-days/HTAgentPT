using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>共享库同步（表 8-1 POST /api/sync/shared）。
/// 导出端在总部节点，鉴权走节点令牌（对端是系统不是人，不走 JWT）；
/// 拉取端在公司节点，是管理员动作，走正常权限。</summary>
[ApiController]
[Route("api/sync/shared")]
public class SyncController(ISharedSyncService sync, IRuntimeConfig config, IAuditWriter audit) : ControllerBase
{
    /// <summary>总部侧：导出增量包。X-Embedding-Tag 与总部一致时附带向量（FR-2.2 复用）。</summary>
    [HttpGet("export")]
    [AllowAnonymous]
    public async Task<IActionResult> Export([FromQuery] DateTimeOffset? since, CancellationToken ct)
    {
        if (!await NodeTokenValidAsync(ct)) return NodeTokenRejected();
        var tag = Request.Headers["X-Embedding-Tag"].FirstOrDefault();
        return Ok(await sync.ExportAsync(since, tag, ct));
    }

    /// <summary>总部侧：原始文件流出（FR-2.2 同步内容含原始文件）。</summary>
    [HttpGet("files/{docId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> ExportFile(Guid docId, CancellationToken ct)
    {
        if (!await NodeTokenValidAsync(ct)) return NodeTokenRejected();
        var (content, fileName, contentType) = await sync.ExportFileAsync(docId, ct);
        return File(content, contentType, fileName);
    }

    /// <summary>公司侧：拉取集团共享库并合入（表 8-1）。</summary>
    [HttpPost]
    [Authorize]
    [RequirePermission(PermissionKeys.KbManage)]
    public async Task<IActionResult> Pull([FromQuery] bool full = false, CancellationToken ct = default)
        => Ok(await sync.PullAsync(full, ct));

    /// <summary>节点令牌校验：总部配置 sync.accept_token，空值即拒绝一切拉取（默认关闭）。</summary>
    private async Task<bool> NodeTokenValidAsync(CancellationToken ct)
    {
        var accept = await config.GetStringAsync(ConfigKeys.SyncAcceptToken, "", ct);
        var given = Request.Headers["X-Sync-Token"].FirstOrDefault() ?? "";
        if (accept.Length >= 16 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(accept), System.Text.Encoding.UTF8.GetBytes(given)))
            return true;
        await audit.WriteAsync(new AuditEntry("sync.export", AuditResult.Denied,
            Username: "sync-node",
            Detail: new
            {
                reason = accept.Length < 16 ? "accept_token_unset_or_short" : "token_mismatch",
                ip = HttpContext.Connection.RemoteIpAddress?.ToString()
            }), ct);
        return false;
    }

    private ObjectResult NodeTokenRejected()
        => StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = "SYNC_TOKEN_INVALID",
            message = "节点令牌无效或总部未配置 sync.accept_token（至少 16 位）。本次请求已被记录。"
        });
}
