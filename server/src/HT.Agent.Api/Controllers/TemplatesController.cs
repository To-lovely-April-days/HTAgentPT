using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>模板与槽位管理（FR-5.3，E12/E13）。列表读取放开给生成权限（选模板要用）。</summary>
[ApiController]
[Route("api/templates")]
[Authorize]
public class TemplatesController(ITemplateService templates, ICurrentUser me) : ControllerBase
{
    [HttpPost]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public async Task<IActionResult> Upload(
        [FromForm] IFormFile file, [FromForm] string name, [FromForm] string docType, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return Ok(await templates.UploadAsync(stream, file.FileName, name, docType, ct));
    }

    /// <summary>模板匹配用列表（FR-5.2）：生成权限只见已启用；模板管理可见全部。</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var manage = me.Permissions.Contains(PermissionKeys.TemplateManage);
        if (!manage && !me.Permissions.Contains(PermissionKeys.Generate))
            return StatusCode(403, new { code = "FORBIDDEN", message = "你的角色不能查看模板。" });
        return Ok(await templates.ListAsync(includeDisabled: manage, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!me.Permissions.Contains(PermissionKeys.TemplateManage) &&
            !me.Permissions.Contains(PermissionKeys.Generate))
            return StatusCode(403, new { code = "FORBIDDEN", message = "你的角色不能查看模板。" });
        return await templates.GetAsync(id, ct) is { } d ? Ok(d) : NotFound();
    }

    [HttpPut("slots/{slotId:guid}")]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public async Task<IActionResult> UpdateSlot(Guid slotId, [FromBody] SlotDefEdit edit, CancellationToken ct)
    {
        await templates.UpdateSlotAsync(slotId, edit, ct);
        return NoContent();
    }

    public record EnableBody(bool Enable);

    [HttpPost("{id:guid}/enable")]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public async Task<IActionResult> Enable(Guid id, [FromBody] EnableBody body, CancellationToken ct)
    {
        await templates.EnableAsync(id, body.Enable, ct);
        return NoContent();
    }

    public record CopyBody(string NewName);

    [HttpPost("{id:guid}/copy")]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public async Task<IActionResult> Copy(Guid id, [FromBody] CopyBody body, CancellationToken ct)
        => Ok(new { id = await templates.CopyAsync(id, body.NewName, ct) });
}
