using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/documents")]
[Authorize]
[RequirePermission(PermissionKeys.CorpusManage)]
public class DocumentsController(IDocumentService docs, AppDbContext db) : ControllerBase
{
    /// <summary>上传（表 8-1 POST /api/documents）：multipart，附知识库、密级与元数据。</summary>
    [HttpPost]
    public async Task<IActionResult> Upload(
        [FromForm] IFormFile file,
        [FromForm] Guid kbId,
        [FromForm] Classification classification,
        [FromForm] string customerName,
        [FromForm] int year,
        [FromForm] string deviceType,
        [FromForm] string docCategory,
        [FromForm] string? projectNo,
        [FromForm] string? title,
        [FromForm] ChunkStrategy? chunkStrategy,
        [FromForm] string? docVersion,
        CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var result = await docs.UploadAsync(new UploadDocumentRequest(
            kbId, file.FileName, file.ContentType ?? "application/octet-stream", file.Length,
            classification, chunkStrategy,
            customerName, year, deviceType, docCategory, projectNo,
            OwnerId: null, DocVersion: docVersion, EffectiveDate: null, Title: title), stream, ct);
        return Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? kbId, [FromQuery] ParseStatus? status,
        [FromQuery] string? search, CancellationToken ct)
        => Ok(await docs.ListAsync(kbId, status, search, ct));

    public record ParseBody(List<Guid> DocIds);

    /// <summary>批量提交解析（表 8-1 POST /api/documents/{id}/parse 的批量形态；FR-1.2）。</summary>
    [HttpPost("parse")]
    public async Task<IActionResult> QueueParse([FromBody] ParseBody body, CancellationToken ct)
        => Ok(new { queued = await docs.QueueParseAsync(body.DocIds, ct) });

    [HttpPost("{id:guid}/parse")]
    public async Task<IActionResult> QueueParseOne(Guid id, CancellationToken ct)
        => Ok(new { queued = await docs.QueueParseAsync([id], ct) });

    public record ReparseBody(ChunkStrategy? NewStrategy);

    [HttpPost("{id:guid}/reparse")]
    public async Task<IActionResult> Reparse(Guid id, [FromBody] ReparseBody body, CancellationToken ct)
    {
        await docs.ReparseAsync(id, body.NewStrategy, ct);
        return NoContent();
    }

    /// <summary>分块检视（表 8-1 GET /api/documents/{id}/chunks；FR-1.5）。</summary>
    [HttpGet("{id:guid}/chunks")]
    public async Task<IActionResult> Chunks(Guid id, CancellationToken ct)
        => Ok(await docs.GetChunksAsync(id, ct));

    /// <summary>解析队列一览（E3 界面）：状态、尝试次数、具体失败原因。</summary>
    [HttpGet("queue")]
    public async Task<IActionResult> Queue(CancellationToken ct)
        => Ok(await db.ParseJobs.AsNoTracking()
            .Include(j => j.Doc)
            .OrderByDescending(j => j.QueuedAt).Take(200)
            .Select(j => new
            {
                j.Id, j.DocId, DocTitle = j.Doc!.Title, j.Kind, j.Status,
                j.Attempts, j.LastError, j.QueuedAt, j.StartedAt, j.FinishedAt
            })
            .ToListAsync(ct));
}

[ApiController]
[Route("api/chunks")]
[Authorize]
[RequirePermission(PermissionKeys.CorpusManage)]
public class ChunksController(IDocumentService docs) : ControllerBase
{
    public record EditBody(string Text);

    /// <summary>编辑单块（表 8-1 PUT /api/chunks/{id}）：仅重算该块向量（FR-1.5）。</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Edit(long id, [FromBody] EditBody body, CancellationToken ct)
    {
        await docs.EditChunkAsync(id, body.Text, ct);
        return NoContent();
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        await docs.DeleteChunkAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/files")]
[Authorize]
public class FilesController(IDocumentService docs) : ControllerBase
{
    /// <summary>下载原件（表 8-1 GET /api/files/{id}）：按密级校验，越权拒绝并审计。语料权限之外
    /// 不再要求 corpus.manage——售前售后在来源标注里回跳原文用的就是这条路（FR-4.9/1.8）。</summary>
    [HttpGet("{docId:guid}")]
    public async Task<IActionResult> Download(Guid docId, CancellationToken ct)
    {
        var (content, fileName, contentType) = await docs.DownloadAsync(docId, ct);
        return File(content, contentType, fileName);
    }
}
