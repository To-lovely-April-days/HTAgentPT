using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController(IQaService qa, ICurrentUser me, IAuditWriter audit, IRuntimeConfig config, ILogger<ChatController> logger,
    HT.Agent.Infrastructure.Persistence.AppDbContext db, IFileStorage storage) : ControllerBase
{
    /// <summary>下载助手做的文件（Excel、Word……）。只有让助手做文件的那个人能下；过了保留期文件就删了。</summary>
    [HttpGet("made-files/{id:guid}")]
    public async Task<IActionResult> MadeFile(Guid id, CancellationToken ct)
    {
        var f = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.MadeFiles, x => x.Id == id && x.UserId == me.UserId && x.CompanyId == me.CompanyId && x.Ok, ct);
        if (f is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这个文件" });
        if (f.FileKey is null || f.ExpiresAt < DateTimeOffset.UtcNow)
            return StatusCode(410, new { code = "EXPIRED", message = "这个文件已经过了保留期，删掉了。可以让助手再做一份。" });
        var type = f.Kind switch
        {
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "pdf" => "application/pdf",
            "csv" => "text/csv",
            _ => "application/octet-stream"
        };
        var st = await storage.OpenAsync(f.FileKey, ct);
        return File(st, type, f.FileName);
    }

    /// <summary>输入框里的「思考」：默认档位、能不能自己选、深度档开没开（模型服务上下文到 32K 才开）。</summary>
    [HttpGet("levels")]
    public async Task<IActionResult> Levels(CancellationToken ct)
    {
        var c = await HT.Agent.Infrastructure.Services.LevelConfig.LoadAsync(config, ct);
        return Ok(new { @default = c.Default, userChoice = c.UserChoice, deepAvailable = c.DeepAvailable, contextTokens = c.ContextTokens });
    }

    // SSE 手写响应不走 MVC 序列化管道，枚举转字符串要在这里单独配，否则密级/交付状态吐出来是数字
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public record AskBody(Guid? SessionId, string Question, RetrievalRequest? Filters, string? ForcedIntent,
        List<Guid>? AttachmentIds = null, SkillTest? SkillTest = null, string? Level = null);

    /// <summary>问答（表 8-1 POST /api/chat/completions）：SSE 流式。
    /// 事件：meta（改写与命中）→ delta*（增量）→ sources → done；或 meta → no_result。</summary>
    [HttpPost("completions")]
    public async Task Ask([FromBody] AskBody body, CancellationToken ct)
    {
        if (!me.Permissions.Contains(PermissionKeys.QaInternal) &&
            !me.Permissions.Contains(PermissionKeys.QaPublic))
        {
            await audit.WriteAsync(new AuditEntry("authz.denied", AuditResult.Denied,
                UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                Detail: new { permission = "qa.*", path = "/api/chat/completions" }, Ip: me.Ip), ct);
            Response.StatusCode = 403;
            await Response.WriteAsJsonAsync(new { code = "FORBIDDEN", message = "你的角色没有问答权限。本次请求已被记录。" }, ct);
            return;
        }

        Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var retrieval = (body.Filters ?? new RetrievalRequest(body.Question)) with { Query = body.Question };
        try
        {
            await foreach (var ev in qa.AskStreamAsync(new QaRequest(body.SessionId, body.Question, retrieval, body.ForcedIntent, body.AttachmentIds, body.SkillTest, body.Level), ct))
            {
                await Response.WriteAsync($"event: {ev.Kind}\n", ct);
                await Response.WriteAsync($"data: {JsonSerializer.Serialize(ev.Payload, JsonOpts)}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 客户端主动断开：QaService 的 finally 已做残答落库，这里安静收场
        }
        catch (Exception ex)
        {
            // 响应头已发出，中间件管不到已开始的流——错误必须作为 SSE 事件送达，
            // 否则客户端只看到连接被硬掐，分不清是网络断了还是服务出错（10.3：明确提示）
            logger.LogError(ex, "问答流中断 {Question}", body.Question);
            var (code, message) = ex switch
            {
                DomainRuleException dre => (dre.Code, dre.Message),
                ForbiddenException fe => (fe.Code, fe.Message),
                _ => ("INTERNAL_ERROR", "服务处理出错，本轮问答中断。请稍后重试。")
            };
            await Response.WriteAsync("event: error\n", CancellationToken.None);
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(new { code, message }, JsonOpts)}\n\n", CancellationToken.None);
            await Response.Body.FlushAsync(CancellationToken.None);
        }
    }
}
