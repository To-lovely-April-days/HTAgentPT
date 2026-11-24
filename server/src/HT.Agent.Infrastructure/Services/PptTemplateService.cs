using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HT.Agent.Infrastructure.Services;

/// <summary>PPT 模板库：导入系统自带的模板、管理员上传的模板，在沙箱里分析每页类型（htppt.analyze）、
/// 在 office 容器里转每页缩略图；做 PPT 时在沙箱里套模板（htppt.build）。</summary>
public sealed class PptTemplateService(AppDbContext db, IFileStorage storage, Clients.SandboxClient sandbox, Clients.OfficeCheckClient office,
    IRuntimeConfig config, ILogger<PptTemplateService> logger)
{
    private static readonly SemaphoreSlim ImportLock = new(1, 1);
    public const int PreviewWidth = 480;

    public record BuiltIn(string Id, string Name, string[] Tags, string File, string Source, string License, string Credit);

    public async Task<string> DirAsync(CancellationToken ct) => (await config.GetStringAsync(ConfigKeys.PptTemplateDir, "/app/ppt-templates", ct)).Trim();

    /// <summary>系统自带的模板清单（templates.json）；目录没挂载返回空。</summary>
    public async Task<List<BuiltIn>> BuiltInsAsync(CancellationToken ct)
    {
        var path = Path.Combine(await DirAsync(ct), "templates.json");
        if (!File.Exists(path)) return [];
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, ct));
        return ((node?["templates"] as JsonArray) ?? []).OfType<JsonObject>().Select(t => new BuiltIn(
            t["id"]?.ToString() ?? "", t["name"]?.ToString() ?? "", ((t["tags"] as JsonArray) ?? []).Select(x => x?.ToString() ?? "").ToArray(),
            t["file"]?.ToString() ?? "", t["source"]?.ToString() ?? "", t["license"]?.ToString() ?? "", t["credit"]?.ToString() ?? ""))
            .Where(t => t.Id.Length > 0 && t.File.Length > 0).ToList();
    }

    /// <summary>把还没导入的自带模板导进来（分析、转缩略图）。沙箱、office 连不上的记成 failed，下次再试。</summary>
    public async Task<(int Added, int Failed)> ImportBuiltInAsync(CancellationToken ct)
    {
        if (!await ImportLock.WaitAsync(0, ct)) return (0, 0);
        try
        {
            var dir = await DirAsync(ct);
            var list = await BuiltInsAsync(ct);
            var have = await db.PptTemplates.Where(t => t.BuiltIn).ToDictionaryAsync(t => t.Slug, ct);
            int added = 0, failed = 0;
            foreach (var b in list)
            {
                if (have.TryGetValue(b.Id, out var row) && row.Status == "ready" && await HasPreviewsAsync(row, ct)) continue;
                var file = Path.Combine(dir, Path.GetFileName(b.File));
                if (!File.Exists(file)) continue;
                var data = await File.ReadAllBytesAsync(file, ct);
                row ??= new PptTemplate { Id = Guid.NewGuid(), Slug = b.Id, Name = b.Name, BuiltIn = true, CreatedAt = DateTimeOffset.UtcNow };
                row.Name = b.Name; row.Tags = string.Join(",", b.Tags); row.Source = b.Source; row.License = b.License;
                row.Credit = b.Credit.Length > 0 ? b.Credit : null;
                if (!have.ContainsKey(b.Id)) { db.PptTemplates.Add(row); have[b.Id] = row; }
                await PrepareAsync(row, data, ct);
                await db.SaveChangesAsync(ct);
                if (row.Status == "ready") added++; else failed++;
            }
            return (added, failed);
        }
        finally { ImportLock.Release(); }
    }

    public static string PreviewKey(Guid id, int page) => $"ppt-templates/{id:N}/p{page}.jpg";

    private async Task<bool> HasPreviewsAsync(PptTemplate row, CancellationToken ct)
        => row.Previews > 0 && await storage.ExistsAsync(PreviewKey(row.Id, 1), ct);

    /// <summary>缩略图找不到的模板（早先的版本把缩略图存到了带随机编号的地方，按固定地址取不到）重新转一遍。
    /// 系统自带的由 ImportBuiltInAsync 处理；这里管管理员上传的。</summary>
    public async Task<int> RepairUploadedPreviewsAsync(CancellationToken ct)
    {
        var rows = await db.PptTemplates.Where(t => !t.BuiltIn && t.Status == "ready" && t.FileKey != null).ToListAsync(ct);
        var fixedCount = 0;
        foreach (var row in rows)
        {
            if (await HasPreviewsAsync(row, ct)) continue;
            byte[] data;
            await using (var s = await storage.OpenAsync(row.FileKey!, ct))
            using (var ms = new MemoryStream()) { await s.CopyToAsync(ms, ct); data = ms.ToArray(); }
            await PrepareAsync(row, data, ct);
            await db.SaveChangesAsync(ct);
            if (row.Status == "ready") fixedCount++;
        }
        return fixedCount;
    }

    /// <summary>存文件、分析每页、转缩略图。分析不了标 failed（不给员工选）。</summary>
    public async Task PrepareAsync(PptTemplate row, byte[] data, CancellationToken ct)
    {
        if (row.FileKey is null)
            row.FileKey = await storage.SaveAsync(new MemoryStream(data), $"ppt-templates/{row.Id:N}.pptx", ct);
        row.Size = data.Length;
        var problem = await AnalyzeAsync(row, data, ct);
        if (problem is not null) { row.Status = "failed"; row.Problem = problem; return; }
        var pv = await office.RenderAsync("t.pptx", data, PreviewWidth, 40, ct);
        if (pv is null) { row.Status = "failed"; row.Problem = "转缩略图的服务（office 容器）连不上"; return; }
        for (var i = 0; i < pv.Value.Pages.Count; i++)
        {
            await storage.SaveAtAsync(PreviewKey(row.Id, i + 1), new MemoryStream(pv.Value.Pages[i]), ct);
        }
        row.Previews = pv.Value.Pages.Count;
        row.Status = "ready";
        row.Problem = null;
    }

    private async Task<string?> AnalyzeAsync(PptTemplate row, byte[] data, CancellationToken ct)
    {
        var url = await config.GetStringAsync(ConfigKeys.SandboxUrl, "http://sandbox:8000", ct);
        Clients.SandboxClient.RunResult r;
        try
        {
            r = await sandbox.RunAsync(url, "import htppt, json\nprint(json.dumps(htppt.analyze('in/t.pptx'), ensure_ascii=False))", 60, ct,
                [new Clients.SandboxClient.InFile("t.pptx", data)]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "沙箱（代码解释器）连不上";
        }
        if (r.Error is not null) return "分析不了：" + (r.Error.Length > 300 ? r.Error[^300..] : r.Error);
        try
        {
            var a = JsonNode.Parse(r.Stdout.Trim());
            row.Slides = a?["slides"]?.GetValue<int>() ?? 0;
            var types = ((a?["types"] as JsonArray) ?? []).Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
            row.PageTypes = string.Join(",", PptPages.Types.Where(types.Contains));
            row.Analysis = a?.ToJsonString();
            if (!types.Contains("cover") || types.Count < 3) return "认出的页面太少（至少要有封面和两种内容页），这套模板没法自动套";
            return null;
        }
        catch (JsonException) { return "分析结果读不懂"; }
    }

    public async Task<byte[]> LoadAsync(PptTemplate t, CancellationToken ct)
    {
        await using var st = await storage.OpenAsync(t.FileKey!, ct);
        using var ms = new MemoryStream();
        await st.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public record BuildResult(byte[]? File, string? Error, JsonNode? Report, bool Unavailable = false);

    /// <summary>在沙箱里按模板做 PPT。images：页面里引用的附件（放进 in/）。</summary>
    public async Task<BuildResult> BuildAsync(PptTemplate t, JsonArray pages, string fileName, IReadOnlyList<Clients.SandboxClient.InFile> images, CancellationToken ct)
    {
        var data = await LoadAsync(t, ct);
        var payload = new JsonObject { ["pages"] = pages.DeepClone(), ["out"] = "out/" + fileName, ["credit"] = t.Credit };
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var code = "import htppt, json, base64\n"
                   + $"a = json.loads(base64.b64decode('{b64}').decode('utf-8'))\n"
                   + "r = htppt.build('in/__template.pptx', a['pages'], a['out'], credit=a['credit'])\n"
                   + "print(json.dumps(r, ensure_ascii=False))";
        var inputs = new List<Clients.SandboxClient.InFile> { new("__template.pptx", data) };
        inputs.AddRange(images.Take(2));
        var url = await config.GetStringAsync(ConfigKeys.SandboxUrl, "http://sandbox:8000", ct);
        var timeout = Math.Clamp(await config.GetIntAsync(ConfigKeys.SandboxTimeout, 30, ct), 60, 120);
        Clients.SandboxClient.RunResult r;
        try { r = await sandbox.RunAsync(url, code, timeout, ct, inputs); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new BuildResult(null, "计算环境（沙箱）连不上", null, true);
        }
        if (r.Error is not null)
        {
            logger.LogWarning("套模板出错 {Template}: {Error}", t.Name, r.Error);
            return new BuildResult(null, "套模板出错：" + (r.Error.Length > 600 ? r.Error[^600..] : r.Error), null);
        }
        var file = r.Files?.FirstOrDefault(f => f.Name.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase));
        if (file is null) return new BuildResult(null, "没做出文件" + (r.Skipped is { Count: > 0 } sk ? "：" + string.Join("、", sk) : ""), null);
        JsonNode? report = null;
        try { report = JsonNode.Parse(r.Stdout.Trim().Split('\n').Last()); } catch (JsonException) { }
        return new BuildResult(file.Data, null, report);
    }

    /// <summary>做好的 PPT 每页转图（文件卡上看）。</summary>
    public Task<(List<byte[]> Pages, int Total)?> RenderPagesAsync(byte[] data, CancellationToken ct) => office.RenderAsync("deck.pptx", data, 640, 30, ct);
}
