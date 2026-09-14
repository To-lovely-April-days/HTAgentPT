using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/vocab")]
[Authorize]
public class VocabController(IVocabService vocab) : ControllerBase
{
    /// <summary>读词表所有登录角色可用（上传表单要用）；维护动作要求 meta.manage。</summary>
    [HttpGet("{key}")]
    public async Task<IActionResult> List(string key, CancellationToken ct)
        => Ok(await vocab.ListAsync(key, ct));

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
