using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using HT.Agent.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

/// <summary>PPT 模板库。员工：输入框「＋ → PPT 模板」里看能选的模板和缩略图。
/// 管理员（后台「PPT 模板」）：看全部、上传（要写来源和许可，不许转发的网站拦下）、停用、改名改标签、导入系统自带的。</summary>
[ApiController]
[Route("api/ppt-templates")]
[Authorize]
public class PptTemplatesController(AppDbContext db, ICurrentUser me, IAuditWriter audit, IFileStorage storage, IRuntimeConfig config,
    PptTemplateService svc, IServiceScopeFactory scopes) : ControllerBase
{
    static string[] Tags(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    static string[] Types(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => PptPages.TypeLabels.GetValueOrDefault(t, t)).ToArray();

    /// <summary>能选的模板（启用、分析过的）。</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        // 这个人不能让助手做 PPT（后台没开、角色不能用、外部客户）：不给选
        if (!(await AssistantService.FileKindsAsync(config, me, ct)).Contains("pptx")) return Ok(Array.Empty<object>());
        var rows = await db.PptTemplates.AsNoTracking().Where(t => t.Enabled && t.Status == "ready")
            .OrderBy(t => t.BuiltIn ? 1 : 0).ThenBy(t => t.Name).ToListAsync(ct);
        return Ok(rows.Select(t => new { t.Id, t.Name, tags = Tags(t.Tags), t.Slides, types = Types(t.PageTypes), t.Previews, t.License }));
    }

    /// <summary>缩略图（第 n 页）。</summary>
    [HttpGet("{id:guid}/preview/{n:int}")]
    public async Task<IActionResult> Preview(Guid id, int n, CancellationToken ct)
    {
        var t = await db.PptTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null || n < 1 || n > t.Previews) return NotFound(new { code = "NOT_FOUND", message = "没有这一页" });
        var key = PptTemplateService.PreviewKey(t.Id, n);
        if (!await storage.ExistsAsync(key, ct)) return NotFound(new { code = "NOT_FOUND", message = "没有这一页" });
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(await storage.OpenAsync(key, ct), "image/jpeg");
    }

    // ── 后台 ──────────────────────────────────────

    [HttpGet("admin")]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public async Task<IActionResult> Admin(CancellationToken ct)
    {
        var rows = await db.PptTemplates.AsNoTracking().OrderBy(t => t.BuiltIn ? 1 : 0).ThenBy(t => t.Name).ToListAsync(ct);
        var builtIns = await svc.BuiltInsAsync(ct);
        var have = rows.Where(r => r.BuiltIn && r.Status == "ready").Select(r => r.Slug).ToHashSet();
        return Ok(new
        {
            items = rows.Select(t => new
            {
                t.Id, t.Name, tags = Tags(t.Tags), t.BuiltIn, t.Enabled, t.Source, t.License, t.Credit, t.Slides, t.Previews, t.Size,
                types = t.PageTypes.Split(',', StringSplitOptions.RemoveEmptyEntries), t.Status, t.Problem, t.CreatedAt,
            }),
            builtInTotal = builtIns.Count,
            builtInMissing = builtIns.Count(b => !have.Contains(b.Id)),
            licenses = PptPages.Licenses,
            allTypes = PptPages.Types.Select(t => new { code = t, label = PptPages.TypeLabels[t] }),
        });
    }

    /// <summary>导入系统自带的模板：在后台慢慢做（每套要分析、转缩略图，30 套几分钟），列表里看进度。</summary>
    [HttpPost("admin/import")]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public IActionResult Import()
    {
        _ = Task.Run(async () =>
        {
            await using var scope = scopes.CreateAsyncScope();
            try { await scope.ServiceProvider.GetRequiredService<PptTemplateService>().ImportBuiltInAsync(CancellationToken.None); }
            catch (Exception ex) { scope.ServiceProvider.GetRequiredService<ILogger<PptTemplatesController>>().LogError(ex, "导入自带 PPT 模板出错"); }
        });
        return Accepted(new { started = true });
    }

    [HttpPost("admin/upload")]
    [RequirePermission(PermissionKeys.TemplateManage)]
    [RequestSizeLimit(31 * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] string name, [FromForm] string? tags, [FromForm] string source,
        [FromForm] string license, [FromForm] string? credit, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { code = "NO_FILE", message = "没有文件" });
        if (!file.FileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { code = "BAD_TYPE", message = "只收 .pptx（.potx 模板先在 PowerPoint 里另存为 .pptx）" });
        if (file.Length > 30 * 1024 * 1024) return BadRequest(new { code = "TOO_LARGE", message = "超过 30 MB：先把里面的图片压缩一下" });
        name = (name ?? "").Trim();
        if (name.Length == 0) return BadRequest(new { code = "NO_NAME", message = "写个名字" });
        if (string.IsNullOrWhiteSpace(source)) return BadRequest(new { code = "NO_SOURCE", message = "写清楚从哪下载的（网址），公司自己做的写「公司自有」" });
        if (!PptPages.Licenses.Contains(license))
            return BadRequest(new { code = "BAD_LICENSE", message = $"许可只能是：{string.Join("、", PptPages.Licenses)}。别的许可（只许个人用、不许转发）不能放进模板库" });
        var blockedList = await config.GetStringAsync(ConfigKeys.PptBlockedSources, PptPages.DefaultBlocked, ct);
        if (PptPages.Blocked(source, blockedList) is { } site)
            return BadRequest(new { code = "BLOCKED_SOURCE", message = $"{site} 的模板只许自己用，不许放进共享的模板库。可以自己下载了用，不能上传到这里" });
        if (license.StartsWith("CC BY") && string.IsNullOrWhiteSpace(credit))
            return BadRequest(new { code = "NO_CREDIT", message = "CC BY 许可要求署名：写一行署名（如「Presentation template by 某某」），做出来的 PPT 最后一页会带上" });
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var row = new PptTemplate
        {
            Id = Guid.NewGuid(), Slug = "u-" + Guid.NewGuid().ToString("N")[..12], Name = name.Length > 60 ? name[..60] : name,
            Tags = string.Join(",", Tags(tags ?? "").Take(4)), BuiltIn = false, Source = source.Trim(), License = license,
            Credit = string.IsNullOrWhiteSpace(credit) ? null : credit.Trim(), CreatedAt = DateTimeOffset.UtcNow, CreatedById = me.UserId,
        };
        db.PptTemplates.Add(row);
        await svc.PrepareAsync(row, ms.ToArray(), ct);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("ppt.template.upload", row.Status == "ready" ? AuditResult.Success : AuditResult.Failed,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId, TargetType: "ppt_template", TargetId: row.Id.ToString(),
            Detail: new { row.Name, row.Source, row.License, row.Status, row.Problem }), ct);
        return Ok(new { row.Id, row.Status, row.Problem, row.Slides, types = Types(row.PageTypes) });
    }

    public record PatchBody(bool? Enabled, string? Name, string[]? Tags);

    [HttpPatch("admin/{id:guid}")]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public async Task<IActionResult> Patch(Guid id, [FromBody] PatchBody b, CancellationToken ct)
    {
        var t = await db.PptTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这套模板" });
        if (b.Enabled is { } on) t.Enabled = on;
        if (!string.IsNullOrWhiteSpace(b.Name)) t.Name = b.Name.Trim() is var n && n.Length > 60 ? n[..60] : b.Name.Trim();
        if (b.Tags is not null) t.Tags = string.Join(",", b.Tags.Select(x => x.Trim()).Where(x => x.Length > 0).Take(4));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("ppt.template.edit", AuditResult.Success, UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "ppt_template", TargetId: t.Id.ToString(), Detail: new { t.Name, t.Enabled, t.Tags }), ct);
        return Ok(new { t.Id, t.Enabled, t.Name, tags = Tags(t.Tags) });
    }

    /// <summary>重新分析（换了分析规则、或上次沙箱没连上）。</summary>
    [HttpPost("admin/{id:guid}/reanalyze")]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public async Task<IActionResult> Reanalyze(Guid id, CancellationToken ct)
    {
        var t = await db.PptTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t?.FileKey is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这套模板" });
        await svc.PrepareAsync(t, await svc.LoadAsync(t, ct), ct);
        await db.SaveChangesAsync(ct);
        return Ok(new { t.Id, t.Status, t.Problem, t.Slides, types = Types(t.PageTypes) });
    }

    /// <summary>删掉上传的模板；系统自带的只能停用（删了下次启动又会导入）。</summary>
    [HttpDelete("admin/{id:guid}")]
    [RequirePermission(PermissionKeys.TemplateManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var t = await db.PptTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这套模板" });
        if (t.BuiltIn) return BadRequest(new { code = "BUILT_IN", message = "系统自带的模板只能停用" });
        if (t.FileKey is not null) await storage.DeleteAsync(t.FileKey, ct);
        for (var i = 1; i <= t.Previews; i++)
        {
            var key = PptTemplateService.PreviewKey(t.Id, i);
            if (await storage.ExistsAsync(key, ct)) await storage.DeleteAsync(key, ct);
        }
        db.PptTemplates.Remove(t);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("ppt.template.delete", AuditResult.Success, UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "ppt_template", TargetId: t.Id.ToString(), Detail: new { t.Name }), ct);
        return NoContent();
    }
}
