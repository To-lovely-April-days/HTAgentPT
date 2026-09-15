using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>公开库发布与撤回（FR-2.3/2.4）。全部归 kb.manage。</summary>
[ApiController]
[Route("api/publish")]
[Authorize]
[RequirePermission(PermissionKeys.KbManage)]
public class PublishController(IPublishService publish) : ControllerBase
{
    public record DraftBody(Guid DocId);

    /// <summary>建发布草稿：复制→单独解析→（可编辑分块）→确认后对外可见。</summary>
    [HttpPost]
    public async Task<IActionResult> CreateDraft([FromBody] DraftBody body, CancellationToken ct)
        => Ok(await publish.CreateDraftAsync(body.DocId, ct));

    [HttpPost("{recordId:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid recordId, CancellationToken ct)
    {
        await publish.ConfirmAsync(recordId, ct);
        return NoContent();
    }

    public record WithdrawBody(string Reason);

    [HttpPost("{recordId:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(Guid recordId, [FromBody] WithdrawBody body, CancellationToken ct)
    {
        await publish.WithdrawAsync(recordId, body.Reason, ct);
        return NoContent();
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await publish.ListAsync(ct));
}
