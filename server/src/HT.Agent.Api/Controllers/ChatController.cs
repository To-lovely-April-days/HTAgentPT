using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController(IQaService qa, ICurrentUser me, IAuditWriter audit, ILogger<ChatController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public record AskBody(Guid? SessionId, string Question, RetrievalRequest? Filters, string? ForcedIntent);

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
            await foreach (var ev in qa.AskStreamAsync(new QaRequest(body.SessionId, body.Question, retrieval, body.ForcedIntent), ct))
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
