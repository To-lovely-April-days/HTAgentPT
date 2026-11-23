using System.IO.Compression;
using System.Text;
using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

/// <summary>后台「技能管理」：给助手的专家答题指南。和模板管理同属「能力域」，同一个权限。
/// 保存即留一版；改过的内容要重新审核才给助手用（启用中的技能保存后变成「待审核」）。</summary>
[ApiController]
[Route("api/skills")]
[Authorize]
[RequirePermission(PermissionKeys.TemplateManage)]
public class SkillsController(AppDbContext db, ICurrentUser me, IAuditWriter audit, IChatModelClient model, ILogger<SkillsController> logger) : ControllerBase
{
    public static readonly string[] Categories = ["公司与产品", "售前与客户", "设计与标准", "图纸与文档", "售后与运维", "安全与化学", "其他"];

    [HttpGet("meta")]
    public IActionResult Meta() => Ok(new
    {
        categories = Categories,
        tools = SkillFiles.KnownTools.Select(t => new { code = t, label = AssistantLoop.ToolLabel(t) }),
        maxBody = SkillFiles.MaxBody,
        licenses = SkillFiles.OpenLicenses,
    });

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var skills = await db.AssistantSkills.AsNoTracking().Where(s => s.CompanyId == me.CompanyId)
            .OrderBy(s => s.Category).ThenBy(s => s.Name)
            .Select(s => new { s.Id, s.Code, s.Name, s.Category, s.Description, s.Source, s.Status, s.Version, s.UpdatedAt, s.Roles, s.InMenu })
            .ToListAsync(ct);
        var used = await UsageAsync(since, ct);
        var total = await db.QaMessages.AsNoTracking().CountAsync(m => m.Session!.CompanyId == me.CompanyId && m.At >= since, ct);
        var withSkill = await db.SkillUses.AsNoTracking().Where(u => u.CompanyId == me.CompanyId && u.At >= since).Select(u => u.MessageId).Distinct().CountAsync(ct);
        return Ok(new
        {
            answersWeek = total, answersWithSkillWeek = withSkill,
            items = skills.Select(s =>
            {
                used.TryGetValue(s.Id, out var u);
                return new { s.Id, s.Code, s.Name, s.Category, s.Description, s.Source, s.Status, s.Version, s.UpdatedAt, s.Roles, s.InMenu, usedWeek = u.Used, notHelpfulWeek = u.NotHelpful };
            }),
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var s = await db.AssistantSkills.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == me.CompanyId, ct);
        if (s is null) return NotFound();
        var versions = await db.AssistantSkillVersions.AsNoTracking().Where(v => v.SkillId == id).OrderByDescending(v => v.Version)
            .Select(v => new { v.Version, v.Note, v.EditedAt, v.EditedById }).Take(30).ToListAsync(ct);
        var people = await NamesAsync(versions.Select(v => v.EditedById).Append(s.ReviewedById), ct);
        var docs = s.RefDocIds.Count == 0 ? [] : await db.Documents.AsNoTracking().Where(d => s.RefDocIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Title }).ToListAsync(ct);
        var used = await UsageAsync(DateTimeOffset.UtcNow.AddDays(-7), ct, id);
        used.TryGetValue(id, out var u);
        return Ok(new
        {
            s.Id, s.Code, s.Name, s.Category, s.Description, s.Body, s.Source, s.License, s.Status, s.Version, s.Roles, s.InMenu,
            s.UpdatedAt, s.ReviewedAt, reviewer = s.ReviewedById is Guid r && people.TryGetValue(r, out var rn) ? rn : null,
            refDocs = docs, usedWeek = u.Used, notHelpfulWeek = u.NotHelpful,
            length = SkillFiles.Length(s.Body), unknownTools = SkillFiles.UnknownTools(s.Body),
            versions = versions.Select(v => new { v.Version, v.Note, v.EditedAt, editor = v.EditedById is Guid e && people.TryGetValue(e, out var en) ? en : null }),
        });
    }

    public record SkillBody(string? Code, string Name, string Category, string Description, string Body, List<string>? Roles, List<Guid>? RefDocIds,
        bool Submit = false, string? Note = null);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SkillBody b, CancellationToken ct)
    {
        var code = SkillFiles.NormalizeCode(b.Code ?? "");
        if (code.Length == 0) throw new DomainRuleException("SKILL_CODE", "代号要用英文字母、数字和连字符，比如 material-selection");
        if (await db.AssistantSkills.AnyAsync(s => s.CompanyId == me.CompanyId && s.Code == code, ct))
            throw new DomainRuleException("SKILL_DUP", $"已经有代号为 {code} 的技能了");
        var now = DateTimeOffset.UtcNow;
        var s = new AssistantSkill
        {
            Id = Guid.NewGuid(), CompanyId = me.CompanyId, Code = code, Name = "", Category = "", Description = "",
            Status = b.Submit ? SkillStatus.Review : SkillStatus.Draft, Version = 1, CreatedAt = now, UpdatedAt = now, UpdatedById = me.UserId,
        };
        Apply(s, b);
        db.AssistantSkills.Add(s);
        db.AssistantSkillVersions.Add(Snapshot(s, b.Note ?? "新建"));
        await db.SaveChangesAsync(ct);
        await Log("skill.create", s, ct);
        return Ok(new { s.Id });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SkillBody b, CancellationToken ct)
    {
        var s = await db.AssistantSkills.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == me.CompanyId, ct);
        if (s is null) return NotFound();
        var contentChanged = s.Name != b.Name.Trim() || s.Description != b.Description.Trim() || s.Body != (b.Body ?? "").Trim();
        Apply(s, b);
        s.UpdatedAt = DateTimeOffset.UtcNow; s.UpdatedById = me.UserId;
        if (contentChanged)
        {
            s.Version++;
            db.AssistantSkillVersions.Add(Snapshot(s, b.Note));
            // 改了内容就要重新审核才给助手用
            s.Status = b.Submit ? SkillStatus.Review : SkillStatus.Draft;
        }
        else if (b.Submit && s.Status == SkillStatus.Draft) s.Status = SkillStatus.Review;
        await db.SaveChangesAsync(ct);
        await Log("skill.update", s, ct);
        return Ok(new { s.Version, s.Status });
    }

    public record StatusBody(string Status);

    /// <summary>草稿 / 送审 / 启用（启用 = 审核通过，记下审核人）。</summary>
    [HttpPost("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] StatusBody b, CancellationToken ct)
    {
        var s = await db.AssistantSkills.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == me.CompanyId, ct);
        if (s is null) return NotFound();
        var st = b.Status switch { "enabled" => SkillStatus.Enabled, "review" => SkillStatus.Review, "draft" => SkillStatus.Draft, _ => s.Status };
        if (st == SkillStatus.Enabled)
        {
            if (string.IsNullOrWhiteSpace(s.Body) || s.Body.Contains("[待写]"))
                throw new DomainRuleException("SKILL_EMPTY", "正文还有「待写」的地方，写完再启用");
            if (SkillFiles.Length(s.Body) > SkillFiles.MaxBody)
                throw new DomainRuleException("SKILL_LONG", $"正文超过 {SkillFiles.MaxBody} 字，本地模型看不完，压短再启用");
            s.ReviewedById = me.UserId; s.ReviewedAt = DateTimeOffset.UtcNow;
        }
        s.Status = st;
        await db.SaveChangesAsync(ct);
        await Log("skill.status", s, ct);
        return NoContent();
    }

    public record MenuBody(bool InMenu);

    /// <summary>出不出现在聊天「用技能」清单里。只是清单显示，不动内容、不用重新审核。</summary>
    [HttpPost("{id:guid}/menu")]
    public async Task<IActionResult> SetMenu(Guid id, [FromBody] MenuBody b, CancellationToken ct)
    {
        var s = await db.AssistantSkills.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == me.CompanyId, ct);
        if (s is null) return NotFound();
        s.InMenu = b.InMenu;
        await db.SaveChangesAsync(ct);
        await Log("skill.menu", s, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/versions/{v:int}")]
    public async Task<IActionResult> Version(Guid id, int v, CancellationToken ct)
    {
        if (!await db.AssistantSkills.AnyAsync(x => x.Id == id && x.CompanyId == me.CompanyId, ct)) return NotFound();
        var row = await db.AssistantSkillVersions.AsNoTracking().FirstOrDefaultAsync(x => x.SkillId == id && x.Version == v, ct);
        return row is null ? NotFound() : Ok(new { row.Version, row.Name, row.Description, row.Body, row.Note, row.EditedAt });
    }

    /// <summary>退回到某一版：把那一版的内容存成新的一版（待审核）。</summary>
    [HttpPost("{id:guid}/restore/{v:int}")]
    public async Task<IActionResult> Restore(Guid id, int v, CancellationToken ct)
    {
        var s = await db.AssistantSkills.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == me.CompanyId, ct);
        var row = await db.AssistantSkillVersions.AsNoTracking().FirstOrDefaultAsync(x => x.SkillId == id && x.Version == v, ct);
        if (s is null || row is null) return NotFound();
        s.Name = row.Name; s.Description = row.Description; s.Body = row.Body;
        s.Version++; s.Status = SkillStatus.Review; s.UpdatedAt = DateTimeOffset.UtcNow; s.UpdatedById = me.UserId;
        db.AssistantSkillVersions.Add(Snapshot(s, $"退回到 v{v}"));
        await db.SaveChangesAsync(ct);
        await Log("skill.restore", s, ct);
        return Ok(new { s.Version });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var s = await db.AssistantSkills.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == me.CompanyId, ct);
        if (s is null) return NotFound();
        db.AssistantSkills.Remove(s);
        await db.SaveChangesAsync(ct);
        await Log("skill.delete", s, ct);
        return NoContent();
    }

    /// <summary>导出成 SKILL.md（开放标准格式），能带到别的平台用。</summary>
    [HttpGet("{id:guid}/export")]
    public async Task<IActionResult> Export(Guid id, CancellationToken ct)
    {
        var s = await db.AssistantSkills.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == me.CompanyId, ct);
        if (s is null) return NotFound();
        var text = SkillFiles.Write(s.Code, s.Description, $"# {s.Name}\n\n" + StripTitle(s.Body, s.Name), s.License);
        return File(Encoding.UTF8.GetBytes(text), "text/markdown; charset=utf-8", $"{s.Code}-SKILL.md");
    }

    // ── 导入开源技能 ─────────────────────────────────────

    /// <summary>导入 SKILL.md 或技能文件夹的 zip。dryRun=true 只做检查；否则存成草稿（来源「改编开源」）。
    /// translate=true 时先让模型把正文和简介译成中文、压短，并把工具名换成我们的——存成草稿等人校对。</summary>
    [HttpPost("import")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<IActionResult> Import([FromForm] IFormFile file, [FromForm] bool dryRun = true, [FromForm] bool translate = false,
        [FromForm] string? category = null, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0) throw new DomainRuleException("SKILL_FILE", "选一个 SKILL.md 或技能文件夹的 zip");
        string skillMd;
        var references = new List<string>();
        var scripts = new List<string>();
        await using (var st = file.OpenReadStream())
        {
            if (file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = new ZipArchive(st, ZipArchiveMode.Read);
                var entry = zip.Entries.Where(e => e.Name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName.Length).FirstOrDefault()
                            ?? throw new DomainRuleException("SKILL_NO_MD", "压缩包里没有 SKILL.md");
                using (var r = new StreamReader(entry.Open())) skillMd = await r.ReadToEndAsync(ct);
                foreach (var e in zip.Entries.Where(e => e.Length > 0 && e != entry))
                {
                    if (e.FullName.Contains("scripts/", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".py") || e.Name.EndsWith(".sh") || e.Name.EndsWith(".js"))
                        scripts.Add(e.FullName);
                    else if (e.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) references.Add(e.FullName);
                }
            }
            else
            {
                using var r = new StreamReader(st);
                skillMd = await r.ReadToEndAsync(ct);
            }
        }
        var p = SkillFiles.Parse(skillMd);
        var code = SkillFiles.NormalizeCode(p.Name);
        var checks = new
        {
            name = p.Name, code, description = p.Description, license = p.License, licenseOk = SkillFiles.LicenseOk(p.License),
            length = SkillFiles.Length(p.Body), maxBody = SkillFiles.MaxBody, unknownTools = SkillFiles.UnknownTools(p.Body),
            scripts, references, exists = code.Length > 0 && await db.AssistantSkills.AnyAsync(s => s.CompanyId == me.CompanyId && s.Code == code, ct),
        };
        if (dryRun) return Ok(new { checks, saved = (Guid?)null });
        if (!checks.licenseOk) throw new DomainRuleException("SKILL_LICENSE", "没有声明可以商用、修改的许可证（如 Apache-2.0、MIT），不能导入");
        if (code.Length == 0) throw new DomainRuleException("SKILL_CODE", "SKILL.md 里没有 name");
        if (checks.exists) throw new DomainRuleException("SKILL_DUP", $"已经有代号为 {code} 的技能了");

        var name = p.Name;
        var desc = p.Description;
        var body = p.Body;
        var note = $"从 {file.FileName} 导入（{p.License}）";
        if (translate)
        {
            try
            {
                body = (await model.CompleteAsync([
                    new ChatTurn("system", """
                        你在把一份英文的技能说明（给 AI 助手的答题指南）改写成中文，给一家反应釜、高压釜设备公司的内部助手用。要求：
                        - 译成简体中文，压缩到 2500 字以内，保留标题结构、步骤、要点和注意事项，删掉和本公司无关的部分；
                        - 工具名换成这家公司助手的：bash / python / 运行代码 → run_python；web_fetch / fetch / 打开网页 → fetch_url；web search / 搜索网页 → web_search；
                          读文件、grep、搜索文件 → search_documents 或 read_document；没有对应的就删掉那一步；
                        - 只输出改写后的正文 Markdown，不要加说明。
                        """),
                    new ChatTurn("user", body)], ct)).Trim();
                var d2 = (await model.CompleteAsync([
                    new ChatTurn("system", "把下面这句英文翻成一句中文，说明这个技能「什么时候用」，只输出这一句。"),
                    new ChatTurn("user", desc)], ct)).Trim();
                if (d2.Length > 0) desc = d2;
                note += "，模型初译，待人工校对";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "导入技能时翻译失败，按原文导入");
                note += "，翻译没成功，保留原文";
            }
        }
        var now = DateTimeOffset.UtcNow;
        var s = new AssistantSkill
        {
            Id = Guid.NewGuid(), CompanyId = me.CompanyId, Code = code, Name = name, Category = Categories.Contains(category) ? category! : "其他",
            Description = desc, Body = body, Source = "adapted", License = p.License, Status = SkillStatus.Draft, Version = 1,
            CreatedAt = now, UpdatedAt = now, UpdatedById = me.UserId,
        };
        db.AssistantSkills.Add(s);
        db.AssistantSkillVersions.Add(Snapshot(s, note));
        await db.SaveChangesAsync(ct);
        await Log("skill.import", s, ct);
        return Ok(new { checks, saved = (Guid?)s.Id });
    }

    // ── 小工具 ───────────────────────────────────────

    private static void Apply(AssistantSkill s, SkillBody b)
    {
        var name = (b.Name ?? "").Trim();
        if (name.Length == 0) throw new DomainRuleException("SKILL_NAME", "名称不能为空");
        var desc = (b.Description ?? "").Trim();
        if (desc.Length == 0) throw new DomainRuleException("SKILL_DESC", "「什么时候用」不能为空——模型只凭这一句决定要不要翻看");
        s.Name = name[..Math.Min(name.Length, 40)];
        s.Description = desc[..Math.Min(desc.Length, 200)];
        s.Category = Categories.Contains(b.Category) ? b.Category : "其他";
        s.Body = (b.Body ?? "").Trim();
        if (s.Body.Length > 20000) throw new DomainRuleException("SKILL_TOO_LONG", "正文太长了");
        s.Roles = (b.Roles ?? []).Select(r => r.Trim()).Where(r => r.Length > 0).Distinct().ToList();
        s.RefDocIds = (b.RefDocIds ?? []).Distinct().Take(10).ToList();
    }

    private AssistantSkillVersion Snapshot(AssistantSkill s, string? note) => new()
    {
        SkillId = s.Id, Version = s.Version, Name = s.Name, Description = s.Description, Body = s.Body,
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 200)], EditedById = me.UserId, EditedAt = DateTimeOffset.UtcNow,
    };

    private static string StripTitle(string body, string name)
    {
        var b = body.TrimStart();
        return b.StartsWith($"# {name}") ? b[(name.Length + 2)..].TrimStart() : b;
    }

    private readonly record struct Use(int Used, int NotHelpful);

    /// <summary>每个技能这段时间被翻看几次；用了它的回答里被点「没帮上」的有几条。</summary>
    private async Task<Dictionary<Guid, Use>> UsageAsync(DateTimeOffset since, CancellationToken ct, Guid? only = null)
    {
        var q = db.SkillUses.AsNoTracking().Where(u => u.CompanyId == me.CompanyId && u.At >= since);
        if (only is Guid id) q = q.Where(u => u.SkillId == id);
        var rows = await q.Select(u => new { u.SkillId, u.MessageId }).ToListAsync(ct);
        var msgIds = rows.Select(r => r.MessageId).Distinct().ToList();
        var bad = (await db.QaMessages.AsNoTracking().Where(m => msgIds.Contains(m.Id) && m.Helpful == false).Select(m => m.Id).ToListAsync(ct)).ToHashSet();
        return rows.GroupBy(r => r.SkillId).ToDictionary(g => g.Key,
            g => new Use(g.Count(), g.Select(x => x.MessageId).Distinct().Count(bad.Contains)));
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private Task Log(string action, AssistantSkill s, CancellationToken ct) => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
        UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId, TargetType: "assistant_skill", TargetId: s.Id.ToString(),
        Detail: new { s.Code, s.Version, status = s.Status.ToString() }), ct);
}
