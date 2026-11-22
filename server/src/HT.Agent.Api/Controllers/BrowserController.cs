using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Clients;
using HT.Agent.Infrastructure.Persistence;
using HT.Agent.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

/// <summary>右侧网页面板「可操作」：服务器上那个浏览器的画面、标签列表、谁在操作；员工在画面上点和打字也走这里。
/// 只有这个对话的主人能用，而且要有「内置浏览器」工具的权限（后台助手工具里开关和角色）。</summary>
[ApiController]
[Route("api/browser/{sessionId:guid}")]
[Authorize]
public class BrowserController(AppDbContext db, ICurrentUser me, IRuntimeConfig config, BrowserClient browser, BrowserActivity activity, IAuditWriter audit,
    IChatAttachmentService attachments) : ControllerBase
{
    private async Task<IActionResult?> GuardAsync(Guid sessionId, CancellationToken ct)
    {
        if (!await AssistantService.ToolAllowedAsync(config, me, "browser", ct))
            return StatusCode(403, new { code = "FORBIDDEN", message = "内置浏览器没开，或你的角色不能用" });
        if (!await db.QaSessions.AnyAsync(s => s.Id == sessionId && s.UserId == me.UserId, ct))
            return NotFound(new { code = "NOT_FOUND", message = "找不到这个对话" });
        return null;
    }

    private static string Sid(Guid sessionId) => sessionId.ToString("N");

    private IActionResult Fail(BrowserClient.BrowserException ex) => StatusCode((int)ex.Status is >= 400 and < 600 ? (int)ex.Status : 502, new { code = "BROWSER", message = ex.Message });

    /// <summary>标签列表 + 每个标签谁在操作（主助手 0、子代理 1…）、在做什么。面板每秒拉一次。</summary>
    [HttpGet("state")]
    public async Task<IActionResult> State(Guid sessionId, CancellationToken ct)
    {
        if (await GuardAsync(sessionId, ct) is { } g) return g;
        try
        {
            var (alive, tabs) = await browser.TabsAsync(Sid(sessionId), ct);
            var marks = activity.Of(Sid(sessionId));
            var downloads = alive ? await browser.DownloadsAsync(Sid(sessionId), ct) : [];
            return Ok(new
            {
                alive, stopped = activity.Stopped(Sid(sessionId)),
                downloads = downloads.Select(d => new { d.Id, d.Name, d.Size, d.Ok, d.Note, d.Tab,
                    accepted = AttachmentLimits.Extensions.Contains(Path.GetExtension(d.Name).ToLowerInvariant()) }),
                tabs = tabs.Select(t => new
                {
                    t.Tab, t.Url, t.Title, t.Rev,
                    who = marks.TryGetValue(t.Tab, out var m) ? m.Who : (int?)null,
                    live = m?.Live ?? false, done = m?.Done ?? false, label = m?.Label
                })
            });
        }
        catch (BrowserClient.BrowserException ex) { return Fail(ex); }
    }

    public record OpenBody(string Url, string? Tab);

    [HttpPost("open")]
    public async Task<IActionResult> Open(Guid sessionId, [FromBody] OpenBody b, CancellationToken ct)
    {
        if (await GuardAsync(sessionId, ct) is { } g) return g;
        var url = (b.Url ?? "").Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (BrowserRules.Blocked(url, await browser.BlockHostsAsync(ct)))
            return UnprocessableEntity(new { code = "BLOCKED", message = "这个网站不让在内置浏览器里打开（网银、支付类，或管理员设了不让开）" });
        try
        {
            var info = await browser.OpenAsync(Sid(sessionId), url, b.Tab, ct);
            await audit.WriteAsync(new AuditEntry("browser.open", AuditResult.Success, UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                TargetType: "qa_session", TargetId: sessionId.ToString(), Detail: new { url = info.Url, by = "user" }), ct);
            return Ok(info);
        }
        catch (BrowserClient.BrowserException ex) { return Fail(ex); }
    }

    public record InputBody(string Kind, double? X, double? Y, string? Text, string? Key, int? Dy);

    /// <summary>员工在画面上的点击、打字、按键、滚动。助手正在操作这一页时先别动（要先点「停下」）。</summary>
    [HttpPost("t/{tab}/input")]
    public async Task<IActionResult> Input(Guid sessionId, string tab, [FromBody] InputBody b, CancellationToken ct)
    {
        if (await GuardAsync(sessionId, ct) is { } g) return g;
        if (activity.LiveOn(Sid(sessionId), tab))
            return Conflict(new { code = "BUSY", message = "助手正在操作这一页，先点「停下」再动手" });
        try { return Ok(await browser.InputAsync(Sid(sessionId), tab, new { kind = b.Kind, x = b.X, y = b.Y, text = b.Text, key = b.Key, dy = b.Dy }, ct)); }
        catch (BrowserClient.BrowserException ex) { return Fail(ex); }
    }

    public record NavBody(string Action);

    [HttpPost("t/{tab}/nav")]
    public async Task<IActionResult> Nav(Guid sessionId, string tab, [FromBody] NavBody b, CancellationToken ct)
    {
        if (await GuardAsync(sessionId, ct) is { } g) return g;
        if (b.Action is not ("back" or "forward" or "reload")) return BadRequest(new { code = "BAD", message = "只能后退、前进、刷新" });
        if (activity.LiveOn(Sid(sessionId), tab)) return Conflict(new { code = "BUSY", message = "助手正在操作这一页，先点「停下」再动手" });
        try { return Ok(await browser.ActAsync(Sid(sessionId), tab, b.Action, null, null, null, false, ct)); }
        catch (BrowserClient.BrowserException ex) { return Fail(ex); }
    }

    [HttpGet("t/{tab}/shot")]
    public async Task<IActionResult> Shot(Guid sessionId, string tab, CancellationToken ct)
    {
        if (await GuardAsync(sessionId, ct) is { } g) return g;
        var r = await browser.ShotAsync(Sid(sessionId), tab, ct);
        if (r is null) return NotFound(new { code = "NOT_FOUND", message = "这个网页已经关了" });
        Response.Headers["X-Rev"] = r.Value.Rev.ToString();
        Response.Headers.CacheControl = "no-store";
        return File(r.Value.Image, "image/jpeg");
    }

    [HttpDelete("t/{tab}")]
    public async Task<IActionResult> Close(Guid sessionId, string tab, CancellationToken ct)
    {
        if (await GuardAsync(sessionId, ct) is { } g) return g;
        try { await browser.CloseTabAsync(Sid(sessionId), tab, ct); }
        catch (BrowserClient.BrowserException) { /* 已经关了 */ }
        return NoContent();
    }

    /// <summary>实时画面的票：画面用 &lt;img&gt; 放，带不了登录头，先换一张 2 分钟内有效的票。</summary>
    [HttpPost("t/{tab}/ticket")]
    public async Task<IActionResult> Ticket(Guid sessionId, string tab, CancellationToken ct)
    {
        if (await GuardAsync(sessionId, ct) is { } g) return g;
        var t = activity.Ticket(Sid(sessionId), tab, me.UserId);
        return Ok(new { url = $"/api/browser/stream?ticket={t}" });
    }

    /// <summary>实时画面（MJPEG）：拿票取流，原样转过去；员工关掉面板就断。</summary>
    [HttpGet("~/api/browser/stream")]
    [AllowAnonymous]
    public async Task Stream([FromQuery] string ticket, CancellationToken ct)
    {
        var t = activity.Redeem(ticket);
        if (t is null) { Response.StatusCode = 403; return; }
        using var up = await browser.StreamAsync(t.Value.Sid, t.Value.Tab, ct);
        if (up is null) { Response.StatusCode = 404; return; }
        Response.ContentType = up.Content.Headers.ContentType?.ToString() ?? "multipart/x-mixed-replace; boundary=frame";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Accel-Buffering"] = "no";          // 前面有 nginx 时别攒着
        try
        {
            await using var s = await up.Content.ReadAsStreamAsync(ct);
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0)
            {
                await Response.Body.WriteAsync(buf.AsMemory(0, n), ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { /* 员工关掉了 */ }
    }

    /// <summary>网页下载的文件：员工点「放进对话附件」才收进来，和自己上传的一样排队解析，助手就能读了。</summary>
    [HttpPost("downloads/{id}/keep")]
    public async Task<IActionResult> Keep(Guid sessionId, string id, CancellationToken ct)
    {
        if (await GuardAsync(sessionId, ct) is { } g) return g;
        try
        {
            var (name, data) = await browser.TakeDownloadAsync(Sid(sessionId), id, ct);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (!AttachmentLimits.Extensions.Contains(ext))
                return UnprocessableEntity(new { code = "ATTACH_FORMAT", message = $"「{name}」这种文件放不进对话（只收 Word、Excel、PowerPoint、PDF）" });
            var type = ext switch
            {
                ".pdf" => "application/pdf",
                ".docx" or ".docm" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" or ".xlsm" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                _ => "application/vnd.openxmlformats-officedocument.presentationml.presentation"
            };
            var view = await attachments.UploadAsync(new MemoryStream(data), name, type, data.Length, ct);
            await attachments.BindAsync(sessionId, [view.Id], ct);
            await browser.DropDownloadAsync(Sid(sessionId), id, ct);
            await audit.WriteAsync(new AuditEntry("browser.download.keep", AuditResult.Success, UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                TargetType: "qa_session", TargetId: sessionId.ToString(), Detail: new { name, size = data.Length }), ct);
            return Ok(view);
        }
        catch (BrowserClient.BrowserException ex) { return Fail(ex); }
    }

    [HttpDelete("downloads/{id}")]
    public async Task<IActionResult> Drop(Guid sessionId, string id, CancellationToken ct)
    {
        if (await GuardAsync(sessionId, ct) is { } g) return g;
        try { await browser.DropDownloadAsync(Sid(sessionId), id, ct); }
        catch (BrowserClient.BrowserException) { /* 已经没了 */ }
        return NoContent();
    }

    /// <summary>「停下」：助手下一步操作网页前会看到，不再动；下一次提问时自动恢复。</summary>
    [HttpPost("stop")]
    public async Task<IActionResult> Stop(Guid sessionId, CancellationToken ct)
    {
        if (await GuardAsync(sessionId, ct) is { } g) return g;
        activity.Stop(Sid(sessionId));
        activity.EndAll(Sid(sessionId));
        return NoContent();
    }
}
