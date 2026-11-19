using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Clients;
using HT.Agent.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>回答旁边的预览面板直接用的几样：网页阅读模式（服务器去抓，去掉脚本广告，只回纯数据）、
/// 阅读模式里的图片代取、代码块里的 Python「运行」。跟助手用的是同一套开关和角色名单。</summary>
[ApiController]
[Route("api/web")]
[Authorize]
public class WebController(IRuntimeConfig config, ICurrentUser me, WebFetcher fetcher, SandboxClient sandbox, IAuditWriter audit) : ControllerBase
{
    /// <summary>这个人在预览里能用哪些：读网页、运行 Python。</summary>
    [HttpGet("caps")]
    public async Task<IActionResult> Caps(CancellationToken ct) => Ok(new
    {
        web = await AssistantService.ToolAllowedAsync(config, me, "fetch_url", ct),
        python = await AssistantService.ToolAllowedAsync(config, me, "run_python", ct),
    });

    /// <summary>阅读模式：抓网页，拆成标题、段落、表格、图片块。embeddable 告诉前端「原网页」能不能嵌进来显示。</summary>
    [HttpGet("read")]
    public async Task<IActionResult> Read([FromQuery] string url, CancellationToken ct)
    {
        if (!await AssistantService.ToolAllowedAsync(config, me, "fetch_url", ct))
            return StatusCode(403, new { code = "FORBIDDEN", message = "没有打开网页的权限，或这个功能在系统设置里关了" });
        WebFetcher.Raw raw;
        try { raw = await fetcher.FetchRawAsync(url ?? "", ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return Ok(new { url, ok = false, note = ex is TaskCanceledException ? "网站响应太慢，超时了" : $"打不开：{ex.Message}", embeddable = false });
        }
        if (raw.Note is not null) return Ok(new { url = raw.Url.ToString(), ok = false, note = raw.Note, embeddable = raw.Embeddable });
        var article = raw.Type.Contains("html") || raw.Html.Contains("<html", StringComparison.OrdinalIgnoreCase)
            ? WebReader.Read(raw.Html, raw.Url)
            : new WebReader.Article(raw.Url.Host, null, null, raw.Html.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(800).Select(l => new WebReader.Block("p", l)).ToList());
        await audit.WriteAsync(new AuditEntry("web.read", AuditResult.Success, UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new { url = raw.Url.ToString(), blocks = article.Blocks.Count }), ct);
        return Ok(new
        {
            url = raw.Url.ToString(), ok = article.Blocks.Count > 0, note = article.Blocks.Count == 0 ? "网页里没有读到正文（可能是靠脚本加载的页面），可以切到原网页或新窗口打开" : null,
            embeddable = raw.Embeddable, title = article.Title, site = article.Site ?? raw.Url.Host, published = article.Published,
            fetchedAt = DateTimeOffset.Now, blocks = article.Blocks,
            markdown = WebReader.ToMarkdown(article, raw.Url.ToString()),
        });
    }

    /// <summary>阅读模式里的图片：服务器代取，只回位图。</summary>
    [HttpGet("image")]
    public async Task<IActionResult> Image([FromQuery] string url, CancellationToken ct)
    {
        if (!await AssistantService.ToolAllowedAsync(config, me, "fetch_url", ct)) return Forbid();
        (byte[] Bytes, string Type)? img;
        try { img = await fetcher.FetchImageAsync(url ?? "", ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { img = null; }
        if (img is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=3600";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(img.Value.Bytes, img.Value.Type);
    }

    public record PythonBody(string Code);

    /// <summary>代码块里的「运行」：跟助手的 run_python 用同一个隔离计算环境。</summary>
    [HttpPost("python")]
    public async Task<IActionResult> Python([FromBody] PythonBody b, CancellationToken ct)
    {
        if (!await AssistantService.ToolAllowedAsync(config, me, "run_python", ct))
            return StatusCode(403, new { code = "FORBIDDEN", message = "没有运行代码的权限，或计算环境在系统设置里关了" });
        var code = b.Code ?? "";
        if (code.Trim().Length == 0 || code.Length > 50_000) return BadRequest(new { code = "BAD_CODE", message = "代码为空或太长" });
        var url = await config.GetStringAsync(ConfigKeys.SandboxUrl, "http://sandbox:8000", ct);
        var timeout = Math.Clamp(await config.GetIntAsync(ConfigKeys.SandboxTimeout, 30, ct), 5, 120);
        SandboxClient.RunResult r;
        try { r = await sandbox.RunAsync(url, code, timeout, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { code = "SANDBOX_DOWN", message = "计算环境现在连不上" });
        }
        await audit.WriteAsync(new AuditEntry("web.python", r.Error is null ? AuditResult.Success : AuditResult.Failed, UserId: me.UserId, Username: me.Username,
            CompanyId: me.CompanyId, Detail: new { chars = code.Length, seconds = r.Seconds }), ct);
        return Ok(new { stdout = r.Stdout.Length > 20000 ? r.Stdout[..20000] : r.Stdout, error = r.Error, images = r.Images, seconds = r.Seconds });
    }
}
