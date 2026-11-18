using System.Text.Json;
using HT.Agent.Api.Security;
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

/// <summary>系统设置 › 助手工具：每个工具的用量、运行环境是否正常、试一下联网搜索。开关和可用角色走通用配置接口。</summary>
[ApiController]
[Route("api/ops/tools")]
[Authorize]
[RequirePermission(PermissionKeys.SystemConfig)]
public class ToolsController(AppDbContext db, IRuntimeConfig config, SandboxClient sandbox, SearxClient searx) : ControllerBase
{
    /// <summary>近 7 天每个工具调用了几次、失败几次；外加确认卡的回答情况。</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-7);
        var rows = await db.AuditLogs.AsNoTracking().Where(a => a.At >= from && a.Action == "qa.tool")
            .Select(a => new { a.Detail, a.Result }).ToListAsync(ct);
        var byTool = new Dictionary<string, (int N, int Failed, long Ms)>();
        foreach (var r in rows)
        {
            var tool = "?"; long ms = 0;
            try
            {
                using var d = JsonDocument.Parse(r.Detail ?? "{}");
                tool = d.RootElement.TryGetProperty("tool", out var t) ? t.GetString() ?? "?" : "?";
                ms = d.RootElement.TryGetProperty("ms", out var m) && m.TryGetInt64(out var v) ? v : 0;
            }
            catch (JsonException) { }
            var cur = byTool.GetValueOrDefault(tool);
            byTool[tool] = (cur.N + 1, cur.Failed + (r.Result == AuditResult.Success ? 0 : 1), cur.Ms + ms);
        }
        var asks = await db.AuditLogs.AsNoTracking().CountAsync(a => a.At >= from && a.Action == "qa.assistant.ask", ct);
        return Ok(new
        {
            total = rows.Count,
            asks,
            tools = byTool.Select(kv => new { tool = kv.Key, calls = kv.Value.N, failed = kv.Value.Failed, avgMs = kv.Value.N == 0 ? 0 : kv.Value.Ms / kv.Value.N })
                .OrderByDescending(x => x.calls).ToList()
        });
    }

    /// <summary>运行环境检查：计算环境和搜索服务连不连得上。</summary>
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        var sb = await sandbox.HealthyAsync(await config.GetStringAsync(ConfigKeys.SandboxUrl, "http://sandbox:8000", ct), ct);
        bool search;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var resp = await HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("tools")
                .GetAsync((await config.GetStringAsync(ConfigKeys.SearxUrl, "http://searxng:8080", ct)).TrimEnd('/') + "/healthz", cts.Token);
            search = resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { search = false; }
        var hazFile = await config.GetStringAsync(ConfigKeys.HazchemFile, "hazchem.csv", ct);
        var hazPath = Path.IsPathRooted(hazFile) ? hazFile : Path.Combine("data", hazFile);
        return Ok(new { sandbox = sb, search, hazchem = System.IO.File.Exists(hazPath) });
    }

    public record TestBody(string Query);

    /// <summary>试一下联网搜索（同样会先拦掉内部信息）。</summary>
    [HttpPost("test-search")]
    public async Task<IActionResult> TestSearch([FromBody] TestBody b, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var names = await db.VocabTerms.AsNoTracking().Where(v => v.VocabKey == VocabKeys.CustomerName).Select(v => v.Value).ToListAsync(ct);
        var (q, changed) = Redactor.Redact(b.Query ?? "", names, [], await config.GetStringAsync(ConfigKeys.IntentProjectNoPattern, "", ct));
        try
        {
            var hits = await searx.SearchAsync(await config.GetStringAsync(ConfigKeys.SearxUrl, "http://searxng:8080", ct), q,
                await config.GetStringAsync(ConfigKeys.SearxEngines, "bing,baidu,sogou", ct), 8, ct);
            return Ok(new { ok = true, query = q, redacted = changed, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), count = hits.Count,
                engines = hits.GroupBy(h => h.Engine).ToDictionary(g => g.Key, g => g.Count()), hits = hits.Take(3) });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Ok(new { ok = false, query = q, redacted = changed, error = ex.Message });
        }
    }
}

/// <summary>把回答导出成 Word（表格导出 Excel 在浏览器里做）。登录即可用，只处理调用方自己提交的文字。</summary>
[ApiController]
[Route("api/export")]
[Authorize]
public class ExportController(IAuditWriter audit, ICurrentUser me) : ControllerBase
{
    public record DocxBody(string Title, string Markdown, List<string>? Sources);

    [HttpPost("docx")]
    public async Task<IActionResult> Docx([FromBody] DocxBody b, CancellationToken ct)
    {
        var title = string.IsNullOrWhiteSpace(b.Title) ? "助手回答" : b.Title.Trim();
        if (title.Length > 60) title = title[..60];
        var bytes = MarkdownDocx.Build(title, b.Markdown ?? "", b.Sources);
        await audit.WriteAsync(new AuditEntry("export.docx", AuditResult.Success, UserId: me.UserId, Username: me.Username,
            CompanyId: me.CompanyId, Detail: new { title, chars = b.Markdown?.Length ?? 0 }), ct);
        var safe = string.Concat(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        return File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"{safe}.docx");
    }
}
