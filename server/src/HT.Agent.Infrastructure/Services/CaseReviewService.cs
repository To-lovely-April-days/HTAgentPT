using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HT.Agent.Infrastructure.Services;

/// <summary>总部审核台（FR-8.4/8.5）。只在总部节点有意义——审核人账号不存在于任何公司部署（3.4）。
/// 通过即渲染入共享库容器文档并标注来源公司，随下次共享库同步下发各公司。</summary>
public class CaseReviewService(
    AppDbContext db,
    IAuditWriter audit,
    ICurrentUser me,
    IOptions<NodeOptions> node) : ICaseReviewService
{
    public async Task ReceiveAsync(SubmittedCase s, CancellationToken ct = default)
    {
        EnsureHq();
        var existing = await db.FaultCases.FirstOrDefaultAsync(c => c.Id == s.CaseId, ct);
        if (existing is null)
        {
            var hqCompany = await db.Companies.AsNoTracking().OrderBy(c => c.CreatedAt).FirstAsync(ct);
            existing = new FaultCase
            {
                Id = s.CaseId,
                CaseNo = s.CaseNo,
                CompanyId = hqCompany.Id,
                DeviceModel = s.DeviceModel,
                AlarmCode = s.AlarmCode,
                Phenomenon = s.Phenomenon,
                CauseAnalysis = s.CauseAnalysis,
                Steps = s.Steps,
                SpareParts = s.SpareParts,
                Result = s.Result,
                Extra = s.Extra,
                SourceCompany = s.SourceCompany, // 来源公司标注（FR-8.5）
                CreatedById = Guid.Empty,
                CreatedAt = s.CreatedAt,        // 录入时间保留原值
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.FaultCases.Add(existing);
        }
        else
        {
            // 重新提交（修改后再共享）：内容覆盖、回到待审
            existing.DeviceModel = s.DeviceModel;
            existing.AlarmCode = s.AlarmCode;
            existing.Phenomenon = s.Phenomenon;
            existing.CauseAnalysis = s.CauseAnalysis;
            existing.Steps = s.Steps;
            existing.SpareParts = s.SpareParts;
            existing.Result = s.Result;
            existing.Extra = s.Extra;
            existing.SourceCompany = s.SourceCompany;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.ReviewedBy = null;
            existing.ReviewedAt = null;
            existing.RejectReason = null;
        }
        existing.SyncStatus = CaseSyncStatus.Pending;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("case.receive", AuditResult.Success,
            Username: $"sync-node({s.SourceCompany})",
            TargetType: "fault_case", TargetId: s.CaseId.ToString(),
            Detail: new { s.CaseNo, s.SourceCompany, s.SubmitterName }), ct);
    }

    public async Task<IReadOnlyList<CaseStatusRow>> StatusOfAsync(IReadOnlyList<Guid> caseIds, CancellationToken ct = default)
    {
        EnsureHq();
        return await db.FaultCases.AsNoTracking()
            .Where(c => caseIds.Contains(c.Id))
            .Select(c => new CaseStatusRow(c.Id, c.SyncStatus.ToString(), c.RejectReason))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CaseRow>> PendingAsync(CancellationToken ct = default)
    {
        EnsureHq();
        return await db.FaultCases.AsNoTracking()
            .Where(c => c.SyncStatus == CaseSyncStatus.Pending)
            .OrderBy(c => c.SubmittedAt ?? c.UpdatedAt)
            .Select(c => new CaseRow(c.Id, c.CaseNo, c.DeviceModel, c.AlarmCode, c.Phenomenon,
                c.Result, c.SyncStatus, c.SourceCompany, c.CreatedAt, c.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<CaseDetail?> GetAsync(Guid caseId, CancellationToken ct = default)
    {
        EnsureHq();
        var c = await db.FaultCases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == caseId, ct);
        if (c is null) return null;
        var extra = c.Extra is null ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(c.Extra) ?? [];
        return new CaseDetail(c.Id, c.CaseNo, c.DeviceModel, c.AlarmCode, c.Phenomenon, c.CauseAnalysis,
            c.Steps, c.SpareParts, c.Result, extra, c.SyncStatus, c.RejectReason, c.SourceCompany,
            c.CreatedById, c.CreatedAt, c.UpdatedAt, c.ChunkId);
    }

    public async Task ApproveAsync(Guid caseId, CaseEdit? edited, CancellationToken ct = default)
    {
        EnsureHq();
        var c = await MustPending(caseId, ct);
        if (edited is not null)
        {
            // 审核人可修改后通过（FR-8.4）
            c.DeviceModel = edited.DeviceModel.Trim();
            c.AlarmCode = string.IsNullOrWhiteSpace(edited.AlarmCode) ? null : edited.AlarmCode.Trim();
            c.Phenomenon = edited.Phenomenon.Trim();
            c.CauseAnalysis = edited.CauseAnalysis.Trim();
            c.Steps = edited.Steps.Trim();
            c.SpareParts = edited.SpareParts;
            c.Result = edited.Result.Trim();
            c.Extra = edited.Extra is { Count: > 0 } ? JsonSerializer.Serialize(edited.Extra) : c.Extra;
        }
        c.SyncStatus = CaseSyncStatus.Shared;
        c.ReviewedBy = me.UserId;
        c.ReviewedAt = DateTimeOffset.UtcNow;
        c.UpdatedAt = DateTimeOffset.UtcNow;

        // 回流入库（FR-8.5）：渲染进共享库案例容器，正文标注来源公司与录入时间，随下次同步下发
        var container = await GetOrCreateSharedContainerAsync(ct);
        var extra = c.Extra is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(c.Extra);
        var annotated = CaseRenderer.Render(c.CaseNo, c.DeviceModel, c.AlarmCode, c.Phenomenon,
            c.CauseAnalysis, c.Steps, c.SpareParts, c.Result, extra)
            + $"\n来源公司：{c.SourceCompany ?? "-"}　录入时间：{c.CreatedAt:yyyy-MM-dd}";
        Chunk chunk;
        if (c.ChunkId is not null &&
            await db.Chunks.FirstOrDefaultAsync(x => x.Id == c.ChunkId, ct) is { } exist)
        {
            chunk = exist;
            chunk.Text = annotated;
            chunk.SearchText = ChineseTokenizer.Tokenize(annotated);
            chunk.Embedding = null;
            chunk.EmbeddingModel = null;
        }
        else
        {
            chunk = new Chunk
            {
                DocId = container.Id,
                KbId = container.KbId,
                Classification = container.Classification,
                Seq = await db.Chunks.Where(x => x.DocId == container.Id).Select(x => (int?)x.Seq).MaxAsync(ct) + 1 ?? 0,
                SectionPath = $"共享案例 > {c.CaseNo}",
                Text = annotated,
                SearchText = ChineseTokenizer.Tokenize(annotated),
                ParseVersion = container.ParseVersion,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Chunks.Add(chunk);
        }
        await db.SaveChangesAsync(ct);
        c.ChunkId = chunk.Id;
        // 容器解析版本推进：共享库同步按 ParsedAt 增量，回流后要能被公司节点拉到
        container.ParseVersion += 1;
        container.ParsedAt = DateTimeOffset.UtcNow;
        await db.Chunks.Where(x => x.DocId == container.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ParseVersion, container.ParseVersion), ct);
        db.ParseJobs.Add(new ParseJob
        {
            Id = Guid.NewGuid(), DocId = container.Id, ChunkId = chunk.Id,
            Kind = ParseJobKind.EmbedOnly, QueuedBy = me.UserId, QueuedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await Log("case.approve", c, new { edited = edited is not null }, ct);
    }

    public async Task WithdrawAsync(Guid caseId, CancellationToken ct = default)
    {
        EnsureHq();
        var c = await db.FaultCases.FirstOrDefaultAsync(x => x.Id == caseId, ct);
        if (c is null) return; // 已不在待审队列——幂等成功，公司侧照常回退
        if (c.SyncStatus != CaseSyncStatus.Pending)
            throw new DomainRuleException("REVIEW_ALREADY_DECIDED", "该案例已有审核结论，不能撤回");
        db.FaultCases.Remove(c);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("case.withdraw", AuditResult.Success,
            Username: $"sync-node({c.SourceCompany})",
            TargetType: "fault_case", TargetId: caseId.ToString(), Detail: new { c.CaseNo }), ct);
    }

    public async Task RejectAsync(Guid caseId, string reason, CancellationToken ct = default)
    {
        EnsureHq();
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainRuleException("REJECT_REASON_REQUIRED", "驳回必须填写原因，会原样回传给提交人（FR-8.4）");
        var c = await MustPending(caseId, ct);
        c.SyncStatus = CaseSyncStatus.Rejected;
        c.RejectReason = reason.Trim();
        c.ReviewedBy = me.UserId;
        c.ReviewedAt = DateTimeOffset.UtcNow;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await Log("case.reject", c, new { reason }, ct);
    }

    private async Task<FaultCase> MustPending(Guid caseId, CancellationToken ct)
    {
        var c = await db.FaultCases.FirstOrDefaultAsync(x => x.Id == caseId, ct)
            ?? throw new DomainRuleException("CASE_NOT_FOUND", "案例不存在");
        if (c.SyncStatus != CaseSyncStatus.Pending)
            throw new DomainRuleException("CASE_NOT_PENDING", $"案例不在待审状态（当前：{c.SyncStatus}）");
        return c;
    }

    /// <summary>共享库的案例容器文档：回流案例的分块载体，随共享库同步下发各公司。</summary>
    private async Task<Document> GetOrCreateSharedContainerAsync(CancellationToken ct)
    {
        const string fileKey = "virtual:shared-cases";
        var existing = await db.Documents.FirstOrDefaultAsync(d => d.FileKey == fileKey, ct);
        if (existing is not null) return existing;
        var kb = await db.KnowledgeBases
            .Where(k => k.Tier == KnowledgeBaseTier.Shared && k.IsActive)
            .OrderBy(k => k.CreatedAt).FirstOrDefaultAsync(ct)
            ?? throw new DomainRuleException("NO_SHARED_KB", "总部节点尚无共享库");
        var container = new Document
        {
            Id = Guid.NewGuid(),
            KbId = kb.Id,
            FileName = "shared-cases.virtual",
            Title = "集团共享故障案例（系统维护）",
            FileKey = fileKey,
            FileSize = 0,
            ContentType = "application/x-ht-virtual",
            Classification = Classification.Internal,
            ParseStatus = ParseStatus.Parsed,
            ParseVersion = 1,
            UploadedById = me.IsAuthenticated ? me.UserId : Guid.Empty,
            UploadedAt = DateTimeOffset.UtcNow,
            ParsedAt = DateTimeOffset.UtcNow
        };
        db.Documents.Add(container);
        await db.SaveChangesAsync(ct);
        return container;
    }

    private void EnsureHq()
    {
        if (!node.Value.IsHeadquarters)
            throw new DomainRuleException("NOT_HQ", "案例审核只在总部节点进行（3.4：公司隔离为架构级约束）");
    }

    private Task Log(string action, FaultCase c, object? detail, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "fault_case", TargetId: c.Id.ToString(),
            Detail: detail ?? new { c.CaseNo }), ct);
}
