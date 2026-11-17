using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>员工上传 + 管理员审核。
///
/// 规则：
/// - 除外部客户外的内部角色都能上传（corpus.upload），文件自动进本公司的内部资料库、内部密级；
/// - 上传后是「待审核」，审核通过前不能解析，也就不会出现在任何人的检索结果里；
/// - 管理员通过 → 自动排队解析；驳回 → 必须写原因，上传人在「文件管理」里看得到；
/// - 员工只看得到自己上传的文件；待审核/已驳回的可以撤回（原件一起删）；
/// - 每一步写审计。</summary>
public class CorpusReviewService(
    AppDbContext db, IDocumentService docs, IAuditWriter audit, ICurrentUser me) : ICorpusReviewService
{
    public async Task<UploadResult> UploadMineAsync(UploadDocumentRequest req, Stream content, CancellationToken ct = default)
    {
        // 本公司的内部资料库：同公司优先，再按建库先后，取第一个启用中的
        var kbId = await db.KnowledgeBases.AsNoTracking()
            .Where(k => k.IsActive && k.Tier == KnowledgeBaseTier.Private && (k.CompanyId == null || k.CompanyId == me.CompanyId))
            .OrderBy(k => k.CompanyId == me.CompanyId ? 0 : 1).ThenBy(k => k.CreatedAt)
            .Select(k => (Guid?)k.Id).FirstOrDefaultAsync(ct)
            ?? throw new DomainRuleException("NO_INTERNAL_KB", "还没有可用的内部资料库，请联系管理员先建一个");
        return await docs.UploadAsync(req with
        {
            KbId = kbId,
            Classification = Classification.Internal,
            ChunkStrategyOverride = null,
            Review = ReviewStatus.Pending
        }, content, ct);
    }

    public async Task<IReadOnlyList<MyFileRow>> ListMineAsync(CancellationToken ct = default)
    {
        var uid = me.UserId;
        return await db.Documents.AsNoTracking()
            .Where(d => d.UploadedById == uid && !d.FileKey.StartsWith("virtual:"))
            .OrderByDescending(d => d.UploadedAt).Take(500)
            .Select(d => new MyFileRow(d.Id, d.Title, d.FileName, d.FileSize, d.UploadedAt,
                d.Metadata == null ? null : d.Metadata.DocCategory,
                d.Metadata == null ? null : d.Metadata.CustomerName,
                d.Metadata == null ? null : d.Metadata.ProjectNo,
                d.Kb!.Name, d.Review, d.ReviewNote, d.ReviewedAt, d.ParseStatus, d.ParseError,
                db.Chunks.Count(c => c.DocId == d.Id)))
            .ToListAsync(ct);
    }

    public async Task WithdrawAsync(Guid docId, CancellationToken ct = default)
    {
        var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == docId, ct);
        // 别人的文件按「不存在」处理，不透露有这份文件
        if (doc is null || doc.UploadedById != me.UserId)
            throw new DomainRuleException("DOC_NOT_FOUND", "文件不存在");
        if (doc.Review == ReviewStatus.Approved)
            throw new DomainRuleException("DOC_ALREADY_APPROVED", "已审核通过的文件不能自己撤回，需要删除请联系管理员");
        await docs.DeleteDocumentAsync(docId, ct);
        await audit.WriteAsync(new AuditEntry("doc.withdraw", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "document", TargetId: docId.ToString(), Detail: new { doc.Title, review = doc.Review.ToString() }), ct);
    }

    public async Task<int> ApproveAsync(IReadOnlyList<Guid> docIds, CancellationToken ct = default)
    {
        var pending = await db.Documents.Where(d => docIds.Contains(d.Id) && d.Review == ReviewStatus.Pending).ToListAsync(ct);
        if (pending.Count == 0) return 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var d in pending)
        {
            d.Review = ReviewStatus.Approved;
            d.ReviewNote = null;
            d.ReviewedById = me.UserId;
            d.ReviewedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("doc.approve", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new { count = pending.Count, titles = pending.Select(d => d.Title).Take(20).ToArray() }), ct);
        // 通过即排队解析，不用再点一次
        await docs.QueueParseAsync(pending.Select(d => d.Id).ToList(), ct);
        return pending.Count;
    }

    public async Task<int> RejectAsync(IReadOnlyList<Guid> docIds, string reason, CancellationToken ct = default)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length == 0)
            throw new DomainRuleException("REJECT_REASON_REQUIRED", "驳回要写原因，上传人能看到");
        if (reason.Length > 500) reason = reason[..500];
        var pending = await db.Documents.Where(d => docIds.Contains(d.Id) && d.Review == ReviewStatus.Pending).ToListAsync(ct);
        if (pending.Count == 0) return 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var d in pending)
        {
            d.Review = ReviewStatus.Rejected;
            d.ReviewNote = reason;
            d.ReviewedById = me.UserId;
            d.ReviewedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("doc.reject", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new { count = pending.Count, reason, titles = pending.Select(d => d.Title).Take(20).ToArray() }), ct);
        return pending.Count;
    }
}
