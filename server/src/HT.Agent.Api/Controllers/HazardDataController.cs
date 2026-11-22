using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Persistence;
using HT.Agent.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

/// <summary>后台「国家目录」：重点监管危险化工工艺、危险化学品目录、易制爆、易制毒。系统自带官方原文；
/// 目录更新或要补充时在这里改，改过的条目系统更新不会覆盖，也可以「恢复原文」。</summary>
[ApiController]
[Route("api/hazard-data")]
[Authorize]
[RequirePermission(PermissionKeys.TemplateManage)]
public class HazardDataController(AppDbContext db, ICurrentUser me, IAuditWriter audit) : ControllerBase
{
    [HttpGet("meta")]
    public async Task<IActionResult> Meta(CancellationToken ct) => Ok(new
    {
        processes = await db.HazardProcesses.CountAsync(ct),
        chemicals = await db.HazardChemicals.CountAsync(c => !c.Sub, ct),
        toxic = await db.HazardChemicals.CountAsync(c => !c.Sub && c.Toxic, ct),
        explosive = await db.ControlledChemicals.CountAsync(c => c.List == "yzb", ct),
        drug = await db.ControlledChemicals.CountAsync(c => c.List == "yzd", ct),
        edited = await db.HazardProcesses.CountAsync(p => p.UpdatedById != null, ct) + await db.HazardChemicals.CountAsync(c => c.UpdatedById != null, ct)
                 + await db.ControlledChemicals.CountAsync(c => c.UpdatedById != null, ct),
        sources = new[]
        {
            "重点监管危险化工工艺目录（2013年完整版）：原国家安全监管总局 安监总管三〔2009〕116号、安监总管三〔2013〕3号",
            "危险化学品目录（2015版）：原国家安全监管总局等十部门公告 2015 年第 5 号；危险化学品分类信息表：安监总厅管三〔2015〕80号附件；柴油条目按应急管理部等十部门公告 2022 年第 8 号调整",
            "易制爆危险化学品名录（2017年版）：公安部",
            "易制毒化学品的分类和品种目录：《易制毒化学品管理条例》附表，含 2008、2012、2014、2017、2021 年增列公告",
        },
    });

    [HttpGet("processes")]
    public async Task<IActionResult> Processes(CancellationToken ct)
        => Ok(await db.HazardProcesses.AsNoTracking().OrderBy(p => p.No)
            .Select(p => new { p.Id, p.No, p.Name, p.ReactionType, p.MonitorUnits, p.Intro, p.Hazards, p.Typical, p.Params, p.Requirements, p.Controls,
                p.Source, p.UpdatedAt, edited = p.UpdatedById != null })
            .ToListAsync(ct));

    public record ProcessBody(string ReactionType, string MonitorUnits, string Intro, string Hazards, string Typical, string Params, string Requirements, string Controls);

    [HttpPut("processes/{id:guid}")]
    public async Task<IActionResult> EditProcess(Guid id, [FromBody] ProcessBody b, CancellationToken ct)
    {
        var p = await db.HazardProcesses.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这一类工艺" });
        static string T(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : (s ?? "").Trim();
        p.ReactionType = T(b.ReactionType, 100); p.MonitorUnits = T(b.MonitorUnits, 300); p.Intro = T(b.Intro, 4000); p.Hazards = T(b.Hazards, 4000);
        p.Typical = T(b.Typical, 4000); p.Params = T(b.Params, 4000); p.Requirements = T(b.Requirements, 4000); p.Controls = T(b.Controls, 4000);
        p.UpdatedById = me.UserId; p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        HazardCatalog.Invalidate();
        await audit.WriteAsync(new AuditEntry("hazard.process.edit", AuditResult.Success, UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "hazard_process", TargetId: p.Id.ToString(), Detail: new { p.No, p.Name }), ct);
        return NoContent();
    }

    /// <summary>恢复成系统自带的原文（以后系统更新也会跟着更新）。</summary>
    [HttpPost("processes/{id:guid}/reset")]
    public async Task<IActionResult> ResetProcess(Guid id, CancellationToken ct)
    {
        var p = await db.HazardProcesses.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这一类工艺" });
        p.UpdatedById = null;
        await db.SaveChangesAsync(ct);
        await Seeder.EnsureHazardDataAsync(db, ct);
        HazardCatalog.Invalidate();
        return NoContent();
    }

    [HttpGet("chemicals")]
    public async Task<IActionResult> Chemicals([FromQuery] string? q, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var query = db.HazardChemicals.AsNoTracking();
        var k = (q ?? "").Trim();
        if (k.Length > 0)
            query = int.TryParse(k, out var seq) ? query.Where(c => c.Seq == seq)
                : query.Where(c => c.Name.Contains(k) || c.Aliases.Contains(k) || c.Cas == k || (c.English != null && c.English.ToLower().Contains(k.ToLower())));
        var total = await query.CountAsync(ct);
        var size = 50;
        page = Math.Max(1, page);
        var rows = await query.OrderBy(c => c.Seq).ThenBy(c => c.Sub).ThenBy(c => c.Name).Skip((page - 1) * size).Take(size)
            .Select(c => new { c.Id, c.Seq, c.Sub, c.Name, c.Aliases, c.English, c.Cas, c.Classes, c.Toxic, c.Note, edited = c.UpdatedById != null })
            .ToListAsync(ct);
        var cas = rows.Where(r => r.Cas != null).Select(r => r.Cas).ToList();
        var ctl = await db.ControlledChemicals.AsNoTracking().Where(c => c.Cas != null && cas.Contains(c.Cas)).Select(c => new { c.Cas, c.List, c.Category }).ToListAsync(ct);
        return Ok(new
        {
            total, page, size,
            items = rows.Select(r => new
            {
                r.Id, r.Seq, r.Sub, r.Name, r.Aliases, r.English, r.Cas, r.Classes, r.Toxic, r.Note, r.edited,
                controlled = ctl.Where(c => c.Cas == r.Cas).Select(c => c.List == "yzb" ? "易制爆" : $"易制毒{c.Category}").Distinct().ToList(),
            }),
        });
    }

    public record ChemicalBody(string Name, string? Aliases, string? Cas, string? Classes, bool Toxic);

    [HttpPut("chemicals/{id:guid}")]
    public async Task<IActionResult> EditChemical(Guid id, [FromBody] ChemicalBody b, CancellationToken ct)
    {
        var c = await db.HazardChemicals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这一条" });
        var name = (b.Name ?? "").Trim();
        if (name.Length == 0) return UnprocessableEntity(new { code = "NAME", message = "品名不能空" });
        var cas = string.IsNullOrWhiteSpace(b.Cas) ? null : b.Cas.Trim();
        if (cas is not null && !HT.Agent.Application.Logic.HazardMatch.IsCas(cas)) return UnprocessableEntity(new { code = "CAS", message = "CAS 号格式不对（如 67-56-1）" });
        c.Name = name.Length > 400 ? name[..400] : name;
        c.Aliases = (b.Aliases ?? "").Trim();
        c.Cas = cas;
        c.Classes = string.Join("\n", (b.Classes ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        c.Toxic = b.Toxic;
        c.UpdatedById = me.UserId; c.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        HazardCatalog.Invalidate();
        await audit.WriteAsync(new AuditEntry("hazard.chemical.edit", AuditResult.Success, UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "hazard_chemical", TargetId: c.Id.ToString(), Detail: new { c.Seq, c.Name }), ct);
        return NoContent();
    }

    [HttpGet("controlled")]
    public async Task<IActionResult> Controlled(CancellationToken ct)
        => Ok(await db.ControlledChemicals.AsNoTracking().OrderBy(c => c.List).ThenBy(c => c.Category).ThenBy(c => c.No)
            .Select(c => new { c.Id, c.List, c.No, c.Name, c.Cas, c.Category }).ToListAsync(ct));
}
