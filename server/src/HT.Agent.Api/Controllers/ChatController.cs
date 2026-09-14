using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController(IQaService qa, ICurrentUser me, IAuditWriter audit) : ControllerBase
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
        await foreach (var ev in qa.AskStreamAsync(new QaRequest(body.SessionId, body.Question, retrieval, body.ForcedIntent), ct))
        {
            await Response.WriteAsync($"event: {ev.Kind}\n", ct);
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(ev.Payload, JsonOpts)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }
}
