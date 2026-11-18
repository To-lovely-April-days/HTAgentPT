using HT.Agent.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>价格库。权限在服务里校验：看 = 查看采购价格，导入 / 删除 = 管理价格库。</summary>
[ApiController]
[Route("api/prices")]
[Authorize]
public class PricesController(IPriceService prices) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Overview(CancellationToken ct) => Ok(await prices.OverviewAsync(ct));

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] Guid? table, [FromQuery] string? material, [FromQuery] string? supplier,
        [FromQuery] int take = 50, CancellationToken ct = default)
        => Ok(await prices.SearchAsync(q, table, material, supplier, take, ct));

    [HttpGet("items/{id:long}/history")]
    public async Task<IActionResult> History(long id, CancellationToken ct) => Ok(await prices.HistoryAsync(id, ct));

    [HttpPost("upload")]
    [RequestSizeLimit(32 * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file, CancellationToken ct)
    {
        await using var s = file.OpenReadStream();
        return Ok(await prices.UploadAsync(s, file.FileName, ct));
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] PricePreviewRequest req, CancellationToken ct) => Ok(await prices.PreviewAsync(req, ct));

    [HttpPost("commit")]
    public async Task<IActionResult> Commit([FromBody] PriceCommitRequest req, CancellationToken ct) => Ok(new { id = await prices.CommitAsync(req, ct) });

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await prices.DeleteTableAsync(id, ct);
        return NoContent();
    }
}
