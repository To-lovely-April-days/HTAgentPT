using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/kbs")]
[Authorize]
public class KnowledgeBasesController(IKnowledgeBaseService kbs) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await kbs.ListAsync(ct));

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
