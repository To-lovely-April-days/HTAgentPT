using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>文档生成（表 8-1 /api/generate/*）。方案与投标书生成 = generate.doc（表 3-2 仅售前）。</summary>
[ApiController]
[Route("api/generate")]
[Authorize]
[RequirePermission(PermissionKeys.Generate)]
public class GenerateController(IGenerationService gen, IGenerationChatService genChat) : ControllerBase
{
    public record CreateBody(Guid TemplateId, string? ProjectHint, Guid? QaSessionId);

    [HttpPost("sessions")]
    public async Task<IActionResult> Create([FromBody] CreateBody body, CancellationToken ct)
        => Ok(await gen.CreateSessionAsync(body.TemplateId, body.ProjectHint, body.QaSessionId, ct));

    [HttpGet("sessions")]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await gen.ListSessionsAsync(ct));

    [HttpGet("sessions/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => await gen.GetSessionAsync(id, ct) is { } v ? Ok(v) : NotFound();

    [HttpGet("sessions/{id:guid}/candidates")]
    public async Task<IActionResult> Candidates(Guid id, CancellationToken ct)
        => Ok(await gen.GetCandidatesAsync(id, ct));

    public record BaseBody(string? ProjectNo);

    [HttpPut("sessions/{id:guid}/base")]
    public async Task<IActionResult> SetBase(Guid id, [FromBody] BaseBody body, CancellationToken ct)
        => Ok(await gen.SetBaseProjectAsync(id, body.ProjectNo, ct));

    [HttpGet("sessions/{id:guid}/slots")]
    public async Task<IActionResult> Slots(Guid id, CancellationToken ct)
        => await gen.GetSessionAsync(id, ct) is { } v ? Ok(v.Slots) : NotFound();

    [HttpPut("sessions/{id:guid}/slots/{tag}")]
    public async Task<IActionResult> PutSlot(Guid id, string tag, [FromBody] SlotPut put, CancellationToken ct)
        => Ok(await gen.PutSlotAsync(id, tag, put, ct));

    /// <summary>hint 可带上用户的原话（工作台里不传，对话里会传），并进检索词提准。</summary>
    [HttpPost("sessions/{id:guid}/slots/{tag}/suggest")]
    public async Task<IActionResult> Suggest(Guid id, string tag, [FromQuery] string? hint, CancellationToken ct)
        => Ok(await gen.SuggestAsync(id, tag, hint, ct));

    public record SlotChatBody(string Question);

    [HttpPost("sessions/{id:guid}/slots/{tag}/chat")]
    public async Task<IActionResult> SlotChat(Guid id, string tag, [FromBody] SlotChatBody body, CancellationToken ct)
        => Ok(new { answer = await gen.SlotChatAsync(id, tag, body.Question, ct) });

    // ── 对话式填槽（A4 聊天形态）────────────────────────────────

    [HttpGet("sessions/{id:guid}/conversation")]
    public async Task<IActionResult> Conversation(Guid id, CancellationToken ct)
        => Ok(await genChat.GetAsync(id, ct));

    [HttpPost("sessions/{id:guid}/conversation")]
    public async Task<IActionResult> ConversationTurn(Guid id, [FromBody] GenChatTurnInput input, CancellationToken ct)
        => Ok(await genChat.TurnAsync(id, input, ct));

    [HttpGet("sessions/{id:guid}/completeness")]
    public async Task<IActionResult> Completeness(Guid id, CancellationToken ct)
        => Ok(await gen.CheckCompletenessAsync(id, ct));

    [HttpGet("sessions/{id:guid}/preview")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
        => Ok(await gen.PreviewAsync(id, ct));

    /// <summary>草稿预览的 docx 字节：界面拿去在浏览器里还原版式，边填边看。
    /// inline 返回，不当附件下载；它不是产出，不落盘也不记生成记录。</summary>
    [HttpGet("sessions/{id:guid}/draft.docx")]
    public async Task<IActionResult> Draft(Guid id, CancellationToken ct)
    {
        var d = await gen.RenderDraftAsync(id, ct);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Filled"] = d.Filled.ToString();
        Response.Headers["X-Blank"] = d.Blank.ToString();
        return File(d.Content, "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
    }

    [HttpPost("sessions/{id:guid}/render")]
    public async Task<IActionResult> Render(Guid id, CancellationToken ct)
        => Ok(await gen.RenderAsync(id, ct));

    [HttpGet("sessions/{id:guid}/output")]
    public async Task<IActionResult> Output(Guid id, CancellationToken ct)
    {
        var (content, fileName) = await gen.OpenOutputAsync(id, ct);
        return File(content,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileName);
    }
}

/// <summary>条款库与拼装（FR-5.18，E11 / A9）。</summary>
[ApiController]
[Route("api/clauses")]
[Authorize]
public class ClausesController(IClauseService clauses, ICurrentUser me) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? category, [FromQuery] string? status, CancellationToken ct)
    {
        // 报价拼装（售前）与条款维护（模板管理）都可读；待审条款对拼装侧不可见由前端按状态过滤，
        // 服务端拼装时强校验 Active（FR-5.18），看见了也用不了
        if (!me.Permissions.Contains(PermissionKeys.GenerateContract) &&
            !me.Permissions.Contains(PermissionKeys.TemplateManage))
            return StatusCode(403, new { code = "FORBIDDEN", message = "你的角色不能查看条款库。" });
        return Ok(await clauses.ListAsync(category, status, ct));
    }

    [HttpPost]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public async Task<IActionResult> Draft([FromBody] ClauseEdit edit, CancellationToken ct)
        => Ok(new { id = await clauses.DraftAsync(edit, ct) });

    [HttpPost("{id:guid}/revise")]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public async Task<IActionResult> Revise(Guid id, [FromBody] ClauseEdit edit, CancellationToken ct)
        => Ok(new { id = await clauses.ReviseAsync(id, edit, ct) });

    [HttpPost("{id:guid}/approve")]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        await clauses.ApproveAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reject")]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        await clauses.RejectAsync(id, ct);
        return NoContent();
    }
}

/// <summary>报价合同拼装（表 8-1 POST /api/generate/contract）。</summary>
[ApiController]
[Route("api/generate/contract")]
[Authorize]
[RequirePermission(PermissionKeys.GenerateContract)]
public class ContractController(IClauseService clauses) : ControllerBase
{
    public record AssembleBody(List<Guid> ClauseIds);

    [HttpPost]
    public async Task<IActionResult> Assemble([FromBody] AssembleBody body, CancellationToken ct)
        => Ok(await clauses.AssembleAsync(body.ClauseIds, ct));
}
