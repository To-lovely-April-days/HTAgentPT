using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>对话附件：在输入框里传的文件，只在这条对话里当资料用（不进知识库，密级按内部）。
/// 传文件要内部问答权限——附件按内部资料处理，只有公开问答权限的账号（客户）不开放。</summary>
[ApiController]
[Route("api/chat/attachments")]
[Authorize]
[RequirePermission(PermissionKeys.QaInternal)]
public class ChatAttachmentsController(IChatAttachmentService attachments) : ControllerBase
{
    /// <summary>上传一个文件，返回附件信息（解析在后台进行，按 id 轮询状态）。</summary>
    [HttpPost]
    [RequestSizeLimit(AttachmentLimits.MaxBytes + 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return Ok(await attachments.UploadAsync(stream, file.FileName, file.ContentType, file.Length, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => await attachments.GetAsync(id, ct) is { } v ? Ok(v) : NotFound();

    /// <summary>原件：预览面板里直接翻看。</summary>
    [HttpGet("{id:guid}/file")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
        => await attachments.OpenAsync(id, ct) is { } f ? File(f.Content, "application/octet-stream", f.FileName) : NotFound();

    /// <summary>撤回还没发出的文件。</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await attachments.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>这条对话里传过的文件。</summary>
    [HttpGet]
    public async Task<IActionResult> ForSession([FromQuery] Guid sessionId, CancellationToken ct)
        => Ok(await attachments.ListForSessionAsync(sessionId, ct));
}
