using HT.Agent.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>个人中心 › 我的记忆。只管当前登录人自己的。</summary>
[ApiController]
[Route("api/profile/memories")]
[Authorize]
public class MemoriesController(IMemoryService memory) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Mine(CancellationToken ct) => Ok(await memory.MineAsync(ct));

    public record Body(string Text, string? Kind);

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] Body b, CancellationToken ct) => Ok(await memory.AddAsync(b.Text, b.Kind ?? "偏好", "manual", ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] Body b, CancellationToken ct)
    {
        await memory.UpdateAsync(id, b.Text, b.Kind ?? "偏好", ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await memory.DeleteAsync(id, ct);
        return NoContent();
    }

    public record EnabledBody(bool Enabled);

    [HttpPut("enabled")]
    public async Task<IActionResult> SetEnabled([FromBody] EnabledBody b, CancellationToken ct)
    {
        await memory.SetEnabledAsync(b.Enabled, ct);
        return NoContent();
    }
}
