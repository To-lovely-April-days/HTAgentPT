using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

/// <summary>对话的「项目」：侧栏的项目列表、项目首页（对话、说明、资料、关联的台账）、新建、改、归档、传项目资料。
/// 第一版只有建的人自己能看。路由不用 /api/projects——那是台账。</summary>
[ApiController]
[Route("api/chat-projects")]
[Authorize]
public class ChatProjectsController(AppDbContext db, ICurrentUser me, IChatAttachmentService attachments, IProjectService ledger, IAuditWriter audit) : ControllerBase
{
    public record EditBody(string Name, string? Description, string? LedgerNo);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await db.ChatProjects.AsNoTracking().Where(p => p.OwnerId == me.UserId && p.ArchivedAt == null)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new { p.Id, p.Name, p.LedgerNo, p.UpdatedAt, count = db.QaSessions.Count(s => s.ProjectId == p.Id) })
            .ToListAsync(ct);
        return Ok(rows);
    }

    private async Task<string?> CheckLedgerAsync(string? no, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(no)) return null;
        try { if (await ledger.GetAsync(no.Trim(), ct) is not null) return null; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return "你没有查台账的权限，关联不了台账项目号"; }
        return $"台账里没有项目号「{no.Trim()}」";
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] EditBody b, CancellationToken ct)
    {
        var name = (b.Name ?? "").Trim();
        if (name.Length == 0) return UnprocessableEntity(new { code = "NAME", message = "给项目起个名字" });
        if (await CheckLedgerAsync(b.LedgerNo, ct) is { } bad) return UnprocessableEntity(new { code = "LEDGER", message = bad });
        var p = new ChatProject
        {
            Id = Guid.NewGuid(), CompanyId = me.CompanyId, OwnerId = me.UserId, Name = name.Length > 80 ? name[..80] : name,
            Description = string.IsNullOrWhiteSpace(b.Description) ? null : b.Description.Trim(),
            LedgerNo = string.IsNullOrWhiteSpace(b.LedgerNo) ? null : b.LedgerNo.Trim(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.ChatProjects.Add(p);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("chat.project.create", AuditResult.Success, UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "chat_project", TargetId: p.Id.ToString(), Detail: new { name = p.Name, ledger = p.LedgerNo }), ct);
        return Ok(new { p.Id });
    }

    private Task<ChatProject?> MineAsync(Guid id, CancellationToken ct) => db.ChatProjects.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == me.UserId, ct);

    /// <summary>项目首页：项目本身、这个项目的对话、项目资料、关联的台账（没有查台账权限就不给）。</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var p = await MineAsync(id, ct);
        if (p is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这个项目" });
        var sessions = await db.QaSessions.AsNoTracking().Where(s => s.ProjectId == id && s.UserId == me.UserId)
            .OrderByDescending(s => s.UpdatedAt).Select(s => new { s.Id, s.Title, s.UpdatedAt, messages = s.Messages.Count }).ToListAsync(ct);
        var files = await db.ChatAttachments.AsNoTracking().Where(a => a.ProjectId == id).OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.FileName, a.Size, a.Status, a.Error, a.CreatedAt }).ToListAsync(ct);
        object? led = null;
        if (p.LedgerNo is not null)
        {
            try
            {
                if (await ledger.GetAsync(p.LedgerNo, ct) is { } d)
                    led = new { no = d.Row.ProjectNo, customer = d.Row.CustomerName, year = d.Row.Year, device = d.Row.DeviceType, model = d.Row.DeviceModel,
                        status = d.Row.DeliveryStatus?.ToString(), owner = d.Row.OwnerName };
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* 没权限就不给 */ }
        }
        return Ok(new { project = new { p.Id, p.Name, p.Description, p.LedgerNo, p.CreatedAt, p.UpdatedAt }, sessions, files, ledger = led });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] EditBody b, CancellationToken ct)
    {
        var p = await MineAsync(id, ct);
        if (p is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这个项目" });
        var name = (b.Name ?? "").Trim();
        if (name.Length == 0) return UnprocessableEntity(new { code = "NAME", message = "项目名字不能空" });
        if ((b.LedgerNo ?? "").Trim() != (p.LedgerNo ?? "") && await CheckLedgerAsync(b.LedgerNo, ct) is { } bad)
            return UnprocessableEntity(new { code = "LEDGER", message = bad });
        p.Name = name.Length > 80 ? name[..80] : name;
        p.Description = string.IsNullOrWhiteSpace(b.Description) ? null : b.Description.Trim();
        p.LedgerNo = string.IsNullOrWhiteSpace(b.LedgerNo) ? null : b.LedgerNo.Trim();
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>归档：侧栏里不显示了；对话移出项目、还在「最近」里，项目资料留着。</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct)
    {
        var p = await MineAsync(id, ct);
        if (p is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这个项目" });
        p.ArchivedAt = DateTimeOffset.UtcNow;
        await db.QaSessions.Where(s => s.ProjectId == id).ExecuteUpdateAsync(x => x.SetProperty(s => s.ProjectId, (Guid?)null), ct);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>传项目资料：和对话附件一样排队解析，只在这个项目里用（不进公司知识库、不用审核）。</summary>
    [HttpPost("{id:guid}/files")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> Upload(Guid id, IFormFile file, CancellationToken ct)
    {
        var p = await MineAsync(id, ct);
        if (p is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这个项目" });
        await using var st = file.OpenReadStream();
        var view = await attachments.UploadAsync(st, file.FileName, file.ContentType, file.Length, ct);
        var row = await db.ChatAttachments.FirstAsync(a => a.Id == view.Id, ct);
        row.ProjectId = id;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(view);
    }

    [HttpDelete("{id:guid}/files/{fileId:guid}")]
    public async Task<IActionResult> DeleteFile(Guid id, Guid fileId, CancellationToken ct)
    {
        if (await MineAsync(id, ct) is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这个项目" });
        var row = await db.ChatAttachments.FirstOrDefaultAsync(a => a.Id == fileId && a.ProjectId == id, ct);
        if (row is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这个文件" });
        db.ChatAttachments.Remove(row);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>助手做的文件放进项目资料（报价单、修订版……）。</summary>
    [HttpPost("{id:guid}/files/from-made/{madeId:guid}")]
    public async Task<IActionResult> FromMade(Guid id, Guid madeId, [FromServices] IFileStorage storage, CancellationToken ct)
    {
        var p = await MineAsync(id, ct);
        if (p is null) return NotFound(new { code = "NOT_FOUND", message = "找不到这个项目" });
        var f = await db.MadeFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == madeId && x.UserId == me.UserId && x.Ok && x.FileKey != null, ct);
        if (f is null) return NotFound(new { code = "NOT_FOUND", message = "这个文件已经没了" });
        await using var st = await storage.OpenAsync(f.FileKey!, ct);
        using var ms = new MemoryStream();
        await st.CopyToAsync(ms, ct);
        ms.Position = 0;
        var view = await attachments.UploadAsync(ms, f.FileName, "application/octet-stream", ms.Length, ct);
        var row = await db.ChatAttachments.FirstAsync(a => a.Id == view.Id, ct);
        row.ProjectId = id;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(view);
    }
}
