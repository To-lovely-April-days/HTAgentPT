using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>翻译（表 8-1 POST /api/translate）。</summary>
[ApiController]
[Route("api/translate")]
[Authorize]
[RequirePermission(PermissionKeys.Translate)]
public class TranslateController(ITranslationService translation) : ControllerBase
{
    /// <summary>文本翻译：术语注入 + 分段分批 + 双语对照 + 合同提示。</summary>
    [HttpPost]
    public async Task<IActionResult> Text([FromBody] TextTranslationRequest req, CancellationToken ct)
        => Ok(await translation.TranslateTextAsync(req, ct));

    /// <summary>docx 整篇翻译与格式回填（FR-6.3）。返回任务号、回填报告与命中术语；文件按任务号下载。</summary>
    [HttpPost("file")]
    public async Task<IActionResult> File(
        [FromForm] IFormFile file, [FromForm] string direction, [FromForm] string? termDomain, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return Ok(await translation.TranslateDocxAsync(stream, file.FileName, direction, termDomain, ct));
    }

    [HttpGet("files/{taskId:guid}")]
    public async Task<IActionResult> Download(Guid taskId, CancellationToken ct)
    {
        var (content, fileName) = await translation.OpenOutputAsync(taskId, ct);
        return File(content,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileName);
    }
}
