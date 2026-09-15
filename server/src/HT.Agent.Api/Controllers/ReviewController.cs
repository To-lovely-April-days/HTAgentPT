using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>同步通道的案例接收与状态回查（节点令牌鉴权——对端是公司节点不是人）。</summary>
[ApiController]
[Route("api/sync/cases")]
public class SyncCasesController(ICaseReviewService review, IRuntimeConfig config, IAuditWriter audit) : ControllerBase
{
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Receive([FromBody] SubmittedCase submitted, CancellationToken ct)
    {
        if (!await TokenOk(ct)) return Rejected();
        await review.ReceiveAsync(submitted, ct);
        return NoContent();
    }

    /// <summary>公司节点撤回待审案例：DomainRule REVIEW_ALREADY_DECIDED 经错误中间件转 422，
    /// 公司侧据此提示「已有结论撤不回」。</summary>
    [HttpPost("{id:guid}/withdraw")]
    [AllowAnonymous]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct)
    {
        if (!await TokenOk(ct)) return Rejected();
        await review.WithdrawAsync(id, ct);
        return NoContent();
    }

    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<IActionResult> Status([FromQuery] string ids, CancellationToken ct)
    {
        if (!await TokenOk(ct)) return Rejected();
        var parsed = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Take(500).ToList();
        return Ok(await review.StatusOfAsync(parsed, ct));
    }

    private async Task<bool> TokenOk(CancellationToken ct)
    {
        var accept = await config.GetStringAsync(ConfigKeys.SyncAcceptToken, "", ct);
        var given = Request.Headers["X-Sync-Token"].FirstOrDefault() ?? "";
        if (accept.Length >= 16 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(accept), System.Text.Encoding.UTF8.GetBytes(given)))
            return true;
        await audit.WriteAsync(new AuditEntry("case.receive", AuditResult.Denied, Username: "sync-node",
            Detail: new { reason = "token", ip = HttpContext.Connection.RemoteIpAddress?.ToString() }), ct);
        return false;
    }

    private ObjectResult Rejected() => StatusCode(403,
        new { code = "SYNC_TOKEN_INVALID", message = "节点令牌无效。本次请求已被记录。" });
}

/// <summary>总部审核台（H1/H2/H4，FR-8.4/8.5）。case.review 只种子给总部审核人。</summary>
[ApiController]
[Route("api/review/cases")]
[Authorize]
[RequirePermission(PermissionKeys.CaseReview)]
public class ReviewController(ICaseReviewService review) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Pending(CancellationToken ct) => Ok(await review.PendingAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => await review.GetAsync(id, ct) is { } d ? Ok(d) : NotFound();

    public record ApproveBody(CaseEdit? Edited);

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ApproveBody body, CancellationToken ct)
    {
        await review.ApproveAsync(id, body.Edited, ct);
        return NoContent();
    }

    public record RejectBody(string Reason);

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectBody body, CancellationToken ct)
    {
        await review.RejectAsync(id, body.Reason, ct);
        return NoContent();
    }
}
