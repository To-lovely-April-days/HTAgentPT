using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>对话附件：上传落存储、排队解析（<see cref="Workers.AttachmentParseWorker"/>），
/// 随第一句话绑到对话上。只本人可见、可撤；跟着对话一直保存，不进知识库。</summary>
public class ChatAttachmentService(
    AppDbContext db,
    IFileStorage storage,
    IAuditWriter audit,
    ICurrentUser me) : IChatAttachmentService
{
    public async Task<ChatAttachmentView> UploadAsync(Stream content, string fileName, string contentType, long size, CancellationToken ct = default)
    {
        var name = Path.GetFileName(fileName ?? "").Trim();
        if (name.Length == 0) throw new DomainRuleException("ATTACH_NAME", "文件名为空");
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (!AttachmentLimits.Extensions.Contains(ext))
            throw new DomainRuleException("ATTACH_FORMAT", ext is ".doc" or ".xls" or ".ppt" or ".wps"
                ? $"「{name}」是老格式，读不了——用 Office 另存为 .docx / .xlsx / .pptx 再传"
                : $"「{name}」的格式不支持，可以传 Word、Excel、PowerPoint、PDF 或图片");
        if (size <= 0) throw new DomainRuleException("ATTACH_EMPTY", $"「{name}」是空文件");
        if (size > AttachmentLimits.MaxBytes)
            throw new DomainRuleException("ATTACH_SIZE", $"「{name}」超过 20MB，传不了——可以拆成几份，或只传需要的章节");
        var pending = await db.ChatAttachments.CountAsync(a => a.UploaderId == me.UserId && a.QaSessionId == null && a.ProjectId == null, ct);
        if (pending >= AttachmentLimits.MaxPerMessage)
            throw new DomainRuleException("ATTACH_COUNT", $"一次最多带 {AttachmentLimits.MaxPerMessage} 个文件，先发出这一轮再传");

        var key = await storage.SaveAsync(content, name, ct);
        var a = new ChatAttachment
        {
            Id = Guid.NewGuid(),
            UploaderId = me.UserId,
            CompanyId = me.CompanyId,
            FileName = name,
            ContentType = AttachmentLimits.IsImage(name) ? AttachmentLimits.ImageType(name, contentType)
                : string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            Size = size,
            FileKey = key,
            Classification = Classification.Internal,
            Status = ParseStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ChatAttachments.Add(a);
        await db.SaveChangesAsync(ct);
        await Log("chat.attachment.upload", a, new { name, size }, ct);
        return View(a);
    }

    public async Task<ChatAttachmentView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var a = await db.ChatAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UploaderId == me.UserId, ct);
        return a is null ? null : View(a);
    }

    public async Task<(Stream Content, string FileName, string ContentType)?> OpenAsync(Guid id, CancellationToken ct = default)
    {
        var a = await db.ChatAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UploaderId == me.UserId, ct);
        if (a is null) return null;
        return (await storage.OpenAsync(a.FileKey, ct), a.FileName, a.ContentType);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var a = await db.ChatAttachments.FirstOrDefaultAsync(x => x.Id == id && x.UploaderId == me.UserId, ct)
                ?? throw new DomainRuleException("ATTACH_NOT_FOUND", "附件不存在");
        if (a.QaSessionId is not null)
            throw new DomainRuleException("ATTACH_BOUND", "已经随对话发出的文件跟着对话保存，不能单独撤回");
        db.ChatAttachments.Remove(a);
        await db.SaveChangesAsync(ct);
        try { await storage.DeleteAsync(a.FileKey, ct); } catch (IOException) { /* 孤儿文件不挡撤回 */ }
        await Log("chat.attachment.delete", a, new { a.FileName }, ct);
    }

    public async Task<IReadOnlyList<ChatAttachmentView>> ListForSessionAsync(Guid qaSessionId, CancellationToken ct = default)
    {
        var mine = await db.QaSessions.AnyAsync(s => s.Id == qaSessionId && s.UserId == me.UserId, ct);
        if (!mine) return [];
        var rows = await db.ChatAttachments.AsNoTracking()
            .Where(a => a.QaSessionId == qaSessionId).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        return rows.Select(View).ToList();
    }

    public async Task<int> BindAsync(Guid qaSessionId, IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;
        var take = ids.Distinct().Take(AttachmentLimits.MaxPerMessage).ToList();
        var rows = await db.ChatAttachments
            .Where(a => take.Contains(a.Id) && a.UploaderId == me.UserId && a.QaSessionId == null && a.ProjectId == null)
            .ToListAsync(ct);
        foreach (var a in rows) a.QaSessionId = qaSessionId;
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    internal static ChatAttachmentView View(ChatAttachment a)
        => new(a.Id, a.FileName, a.Size, a.Status, a.Error, a.CharCount, a.QaSessionId, a.CreatedAt);

    private Task Log(string action, ChatAttachment a, object detail, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "chat_attachment", TargetId: a.Id.ToString(), Detail: detail), ct);
}
