using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>公开库发布（FR-2.3/2.4）。发布是复制不是移动：副本单独解析入库、原文档不变；
/// 副本密级恒为公开（公开库只承载公开内容）；确认前副本带撤回位、不参与检索，
/// 正好给「删除不宜公开的段落」留出编辑窗口。</summary>
public class PublishService(
    AppDbContext db,
    IFileStorage storage,
    IAuditWriter audit,
    ICurrentUser me) : IPublishService
{
    public async Task<PublishDraft> CreateDraftAsync(Guid sourceDocId, CancellationToken ct = default)
    {
        var source = await db.Documents.AsNoTracking().Include(d => d.Kb).Include(d => d.Metadata)
            .FirstOrDefaultAsync(d => d.Id == sourceDocId, ct)
            ?? throw new DomainRuleException("DOC_NOT_FOUND", "源文档不存在");
        if (source.Kb!.Tier == KnowledgeBaseTier.Public)
            throw new DomainRuleException("ALREADY_PUBLIC", "源文档已在公开库");
        if (source.ParseStatus != ParseStatus.Parsed)
            throw new DomainRuleException("SOURCE_NOT_PARSED", "源文档尚未解析完成，无从确认要公开什么内容");
        if (source.FileKey.StartsWith("virtual:", StringComparison.Ordinal))
            throw new DomainRuleException("VIRTUAL_DOC_MANAGED", "系统维护的虚拟条目不能发布");

        var publicKb = await db.KnowledgeBases
            .Where(k => k.Tier == KnowledgeBaseTier.Public && k.CompanyId == me.CompanyId && k.IsActive)
            .OrderBy(k => k.CreatedAt).FirstOrDefaultAsync(ct)
            ?? throw new DomainRuleException("NO_PUBLIC_KB", "本公司尚无公开库，请先在知识库管理中创建");

        // 独立副本 + 内容快照：同一份复制的文件既是副本原件也是发布快照
        string copyKey;
        await using (var src = await storage.OpenAsync(source.FileKey, ct))
            copyKey = await storage.SaveAsync(src, $"pub_{source.FileName}", ct);

        var copy = new Document
        {
            Id = Guid.NewGuid(),
            KbId = publicKb.Id,
            FileName = source.FileName,
            Title = source.Title,
            FileKey = copyKey,
            FileSize = source.FileSize,
            ContentType = source.ContentType,
            Sha256 = source.Sha256,
            Classification = Classification.Public, // 公开库只承载公开内容
            ParseStatus = ParseStatus.Queued,       // 发布即单独解析入库（FR-2.3）
            IsWithdrawn = true,                     // 确认前不对外可见（编辑窗口）
            UploadedById = me.UserId,
            UploadedAt = DateTimeOffset.UtcNow,
            Metadata = source.Metadata is null ? null : new DocMetadata
            {
                CustomerName = source.Metadata.CustomerName,
                Year = source.Metadata.Year,
                DeviceType = source.Metadata.DeviceType,
                DocCategory = source.Metadata.DocCategory,
                ProjectNo = source.Metadata.ProjectNo,
                DocVersion = source.Metadata.DocVersion
            }
        };
        db.Documents.Add(copy);
        db.ParseJobs.Add(new ParseJob
        {
            Id = Guid.NewGuid(),
            DocId = copy.Id,
            Kind = ParseJobKind.Parse,
            QueuedBy = me.UserId,
            QueuedAt = DateTimeOffset.UtcNow
        });
        var record = new PublishRecord
        {
            Id = Guid.NewGuid(),
            SourceDocId = source.Id,
            PublicDocId = copy.Id,
            OperatorId = me.UserId,
            SnapshotKey = copyKey,
            PublishedAt = DateTimeOffset.UtcNow
        };
        db.PublishRecords.Add(record);
        await db.SaveChangesAsync(ct);
        await Log("publish.draft", record, new { source = source.Title }, ct);
        return new PublishDraft(record.Id, copy.Id);
    }

    public async Task ConfirmAsync(Guid recordId, CancellationToken ct = default)
    {
        var record = await Get(recordId, ct);
        if (record.WithdrawnAt is not null)
            throw new DomainRuleException("PUBLISH_WITHDRAWN", "该发布已撤回");
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == record.PublicDocId, ct)
            ?? throw new DomainRuleException("COPY_MISSING", "公开副本不存在（可能已被撤回清理）");
        if (doc.ParseStatus != ParseStatus.Parsed)
            throw new DomainRuleException("COPY_NOT_PARSED",
                $"副本解析未完成（当前：{doc.ParseStatus}），解析完并检视分块后再确认");
        doc.IsWithdrawn = false; // 确认即对外可见
        record.ConfirmedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await Log("publish.confirm", record, null, ct);
    }

    public async Task WithdrawAsync(Guid recordId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainRuleException("WITHDRAW_REASON_REQUIRED", "撤回必须写明原因（FR-2.4）");
        var record = await Get(recordId, ct);
        if (record.WithdrawnAt is not null)
            throw new DomainRuleException("PUBLISH_WITHDRAWN", "该发布已撤回过");
        // 撤回即删除公开库中的副本并立即停止对外可见；快照文件保留（记录里的 SnapshotKey）
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == record.PublicDocId, ct);
        if (doc is not null) db.Documents.Remove(doc); // 级联删分块
        record.WithdrawnAt = DateTimeOffset.UtcNow;
        record.WithdrawnBy = me.UserId;
        record.WithdrawReason = reason.Trim();
        await db.SaveChangesAsync(ct);
        await Log("publish.withdraw", record, new { reason }, ct);
    }

    public async Task<IReadOnlyList<PublishRow>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.PublishRecords.AsNoTracking()
            .OrderByDescending(r => r.PublishedAt).Take(500)
            .Select(r => new
            {
                r.Id, r.SourceDocId, r.PublicDocId, r.OperatorId,
                r.PublishedAt, r.ConfirmedAt, r.WithdrawnAt, r.WithdrawReason,
                SourceTitle = db.Documents.Where(d => d.Id == r.SourceDocId).Select(d => d.Title).FirstOrDefault(),
                CopyStatus = db.Documents.Where(d => d.Id == r.PublicDocId)
                    .Select(d => (ParseStatus?)d.ParseStatus).FirstOrDefault(),
                OperatorName = db.Users.Where(u => u.Id == r.OperatorId).Select(u => u.DisplayName).FirstOrDefault()
            })
            .ToListAsync(ct);
        return rows.Select(r => new PublishRow(
            r.Id, r.SourceDocId, r.SourceTitle ?? "（源文档已删除）", r.PublicDocId,
            r.CopyStatus?.ToString(),
            r.WithdrawnAt is not null ? "已撤回" : r.ConfirmedAt is not null ? "已发布" : "待确认",
            r.OperatorName ?? "-", r.PublishedAt, r.ConfirmedAt, r.WithdrawnAt, r.WithdrawReason)).ToList();
    }

    private async Task<PublishRecord> Get(Guid id, CancellationToken ct)
        => await db.PublishRecords.FirstOrDefaultAsync(r => r.Id == id, ct)
           ?? throw new DomainRuleException("PUBLISH_NOT_FOUND", "发布记录不存在");

    private Task Log(string action, PublishRecord r, object? detail, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "publish_record", TargetId: r.Id.ToString(), Detail: detail), ct);
}
