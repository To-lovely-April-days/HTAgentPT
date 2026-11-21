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
public class ToolsController(AppDbContext db, IRuntimeConfig config, SandboxClient sandbox, SearxClient searx,
    LiteratureClient literature, IMemoryService memory, HT.Agent.Infrastructure.Vision.ImageDescriber vision) : ControllerBase
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
        // 助手做的文件：每种做了几份、几份检查没过
        var files = await db.MadeFiles.AsNoTracking().Where(f => f.CreatedAt >= from)
            .GroupBy(f => f.Kind == "csv" ? "xlsx" : f.Kind).Select(g => new { kind = g.Key, made = g.Count(x => x.Ok), failed = g.Count(x => !x.Ok) }).ToListAsync(ct);
        // 分头查：拆了几次、平均几个子任务、几成没查完
        var dels = await db.AuditLogs.AsNoTracking().Where(a => a.At >= from && a.Action == "qa.delegate").Select(a => a.Detail).ToListAsync(ct);
        int dTasks = 0, dFailed = 0;
        foreach (var d in dels)
        {
            try
            {
                using var j = JsonDocument.Parse(d ?? "{}");
                dTasks += j.RootElement.TryGetProperty("tasks", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
                dFailed += j.RootElement.TryGetProperty("failed", out var f) && f.TryGetInt32(out var fv) ? fv : 0;
            }
            catch (JsonException) { }
        }
        return Ok(new
        {
            total = rows.Count,
            asks,
            files,
            @delegate = new { runs = dels.Count, avgTasks = dels.Count == 0 ? 0 : Math.Round((double)dTasks / dels.Count, 1), failedPct = dTasks == 0 ? 0 : (int)Math.Round(100.0 * dFailed / dTasks) },
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
        bool office;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            var conv = (await config.GetStringAsync(ConfigKeys.PdfConverterUrl, "", ct)).Trim();
            if (conv.Length == 0) conv = ExportController.DefaultConverter;
            var health = new Uri(new Uri(conv), "/health");
            using var resp = await HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("tools").GetAsync(health, cts.Token);
            office = resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException) { office = false; }
        var epoSet = (await config.GetStringAsync(ConfigKeys.EpoKey, "", ct)).Trim().Length > 0
                     && (await config.GetStringAsync(ConfigKeys.EpoSecret, "", ct)).Trim().Length > 0;
        var (memUsers, memTotal) = await memory.StatsAsync(ct);
        return Ok(new
        {
            sandbox = sb, search, hazchem = System.IO.File.Exists(hazPath), office, epo = epoSet,
            browser = await HttpContext.RequestServices.GetRequiredService<HT.Agent.Infrastructure.Clients.BrowserClient>().HealthyAsync(ct),
            vision = await vision.EnabledAsync(ct),
            memory = new { users = memUsers, total = memTotal } // 只给条数，不给内容
        });
    }

    /// <summary>试一下欧洲专利局密钥能不能用。</summary>
    [HttpPost("test-epo")]
    public async Task<IActionResult> TestEpo(CancellationToken ct)
    {
        var key = (await config.GetStringAsync(ConfigKeys.EpoKey, "", ct)).Trim();
        var secret = (await config.GetStringAsync(ConfigKeys.EpoSecret, "", ct)).Trim();
        if (key.Length == 0 || secret.Length == 0) return Ok(new { ok = false, error = "还没填密钥" });
        return Ok(new { ok = await literature.CheckPatentKeyAsync(key, secret, ct) });
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
public class ExportController(IAuditWriter audit, ICurrentUser me, IRuntimeConfig config, IHttpClientFactory http) : ControllerBase
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

    /// <summary>默认的转换容器地址（docker-compose.tools.yml 里的 office）。</summary>
    public const string DefaultConverter = "http://office:3000/convert";

    /// <summary>导出 PDF：先按 Word 的样子排好，再交给 LibreOffice 容器转 PDF。</summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> Pdf([FromBody] DocxBody b, CancellationToken ct)
    {
        var title = string.IsNullOrWhiteSpace(b.Title) ? "助手回答" : b.Title.Trim();
        if (title.Length > 60) title = title[..60];
        var docx = MarkdownDocx.Build(title, b.Markdown ?? "", b.Sources);
        var url = (await config.GetStringAsync(ConfigKeys.PdfConverterUrl, "", ct)).Trim();
        if (url.Length == 0) url = DefaultConverter;
        byte[] pdf;
        try
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(docx), "file", "export.docx" } };
            using var resp = await http.CreateClient("tools").PostAsync(url, form, ct);
            resp.EnsureSuccessStatusCode();
            pdf = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { code = "PDF_UNAVAILABLE", message = "PDF 转换服务没连上，可以先导出 Word" });
        }
        await audit.WriteAsync(new AuditEntry("export.pdf", AuditResult.Success, UserId: me.UserId, Username: me.Username,
            CompanyId: me.CompanyId, Detail: new { title, chars = b.Markdown?.Length ?? 0 }), ct);
        var safe = string.Concat(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        return File(pdf, "application/pdf", $"{safe}.pdf");
    }
}
