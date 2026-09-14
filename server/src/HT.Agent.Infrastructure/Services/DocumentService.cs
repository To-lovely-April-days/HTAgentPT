using System.Security.Cryptography;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HT.Agent.Infrastructure.Services;

public class NodeOptions
{
    /// <summary>是否总部节点。H 组界面就是同一份代码以总部配置渲染的结果（3.4 第一条）。
    /// 非总部节点不得向共享库写入（FR-2.2：同步为单向覆盖）。</summary>
    public bool IsHeadquarters { get; set; }
}

public class DocumentService(
    AppDbContext db,
    IFileStorage storage,
    IVocabService vocab,
    IRuntimeConfig config,
    IAuditWriter audit,
    ICurrentUser me,
    IOptions<NodeOptions> node) : IDocumentService
{
    /// <summary>文档类别为这些取值时项目编号必填并须在台账中存在（表 4-5）。</summary>
    private static readonly string[] ProjectLinkedCategories =
        ["方案", "合同", "报价", "图纸", "交付报告", "故障记录"];

    public async Task<UploadResult> UploadAsync(UploadDocumentRequest req, Stream content, CancellationToken ct = default)
    {
        var kb = await db.KnowledgeBases.FindAsync([req.KbId], ct)
            ?? throw new DomainRuleException("KB_NOT_FOUND", "必须选择目标知识库（FR-1.1）");
        if (!kb.IsActive)
            throw new DomainRuleException("KB_INACTIVE", "知识库已停用");
        if (kb.Tier == KnowledgeBaseTier.Shared && !node.Value.IsHeadquarters)
            throw new DomainRuleException("SHARED_KB_READONLY",
                "共享库由总部下发，本地不得直接写入（FR-2.2）");

        var maxMb = await config.GetIntAsync(ConfigKeys.UploadMaxFileMb, 200, ct);
        if (req.FileSize > maxMb * 1024L * 1024L)
            throw new DomainRuleException("FILE_TOO_LARGE", $"超出单文件上限 {maxMb} MB（上限可在系统设置调整）");

        // 元数据必填与受控词表校验（FR-3.1/3.2）：必填项未填不允许提交
        if (string.IsNullOrWhiteSpace(req.CustomerName))
            throw new DomainRuleException("META_CUSTOMER_REQUIRED", "客户名称必填");
        if (req.Year < 1990 || req.Year > 2100)
            throw new DomainRuleException("META_YEAR_INVALID", "年份须为四位数字");
        if (!await vocab.IsValidAsync(VocabKeys.DeviceType, req.DeviceType, ct))
            throw new DomainRuleException("META_DEVICE_TYPE", $"设备类型「{req.DeviceType}」不在受控词表中，需管理员先维护词表");
        if (!await vocab.IsValidAsync(VocabKeys.DocCategory, req.DocCategory, ct))
            throw new DomainRuleException("META_DOC_CATEGORY", $"文档类别「{req.DocCategory}」不在受控词表中");

        if (ProjectLinkedCategories.Contains(req.DocCategory))
        {
            if (string.IsNullOrWhiteSpace(req.ProjectNo))
                throw new DomainRuleException("META_PROJECT_REQUIRED",
                    $"文档类别为「{req.DocCategory}」时必须关联项目编号（表 4-5）");
            if (!await db.Projects.AnyAsync(p => p.ProjectNo == req.ProjectNo, ct))
                throw new DomainRuleException("META_PROJECT_NOT_FOUND",
                    $"项目编号 {req.ProjectNo} 不在台账中，请先在台账登记");
        }

        // 保存原文件并计算摘要（FR-1.8：原始文件存入对象存储并保留）
        using var sha = SHA256.Create();
        await using var hashing = new CryptoStream(content, sha, CryptoStreamMode.Read);
        var fileKey = await storage.SaveAsync(hashing, req.FileName, ct);
        var digest = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

        var doc = new Document
        {
            Id = Guid.NewGuid(),
            KbId = kb.Id,
            FileName = req.FileName,
            Title = string.IsNullOrWhiteSpace(req.Title) ? Path.GetFileNameWithoutExtension(req.FileName) : req.Title!,
            FileKey = fileKey,
            FileSize = req.FileSize,
            ContentType = req.ContentType,
            Sha256 = digest,
            Classification = req.Classification,
            ChunkStrategyOverride = req.ChunkStrategyOverride,
            ParseStatus = ParseStatus.NotParsed, // 上传后不自动解析（FR-1.2）
            UploadedById = me.UserId,
            UploadedAt = DateTimeOffset.UtcNow,
            Metadata = new DocMetadata
            {
                CustomerName = req.CustomerName.Trim(),
                Year = req.Year,
                DeviceType = req.DeviceType,
                DocCategory = req.DocCategory,
                ProjectNo = string.IsNullOrWhiteSpace(req.ProjectNo) ? null : req.ProjectNo,
                OwnerId = req.OwnerId,
                DocVersion = req.DocVersion,
                EffectiveDate = req.EffectiveDate
            }
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("doc.upload", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "document", TargetId: doc.Id.ToString(),
            Detail: new { req.FileName, kb = kb.Name, req.Classification, req.DocCategory }), ct);
        return new UploadResult(doc.Id, fileKey);
    }

    public async Task<IReadOnlyList<DocRow>> ListAsync(Guid? kbId, ParseStatus? status, string? search, CancellationToken ct = default)
    {
        var q = db.Documents.AsNoTracking().Include(d => d.Kb).Include(d => d.Metadata).AsQueryable();
        if (kbId is not null) q = q.Where(d => d.KbId == kbId);
        if (status is not null) q = q.Where(d => d.ParseStatus == status);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(d => d.Title.Contains(search) || d.FileName.Contains(search));
        return await q.OrderByDescending(d => d.UploadedAt).Take(500)
            .Select(d => new DocRow(d.Id, d.Title, d.FileName, d.KbId, d.Kb!.Name,
                d.Classification, d.ParseStatus, d.ParseError, d.FileSize, d.UploadedAt,
                d.Metadata == null ? null : d.Metadata.DocCategory,
                d.Metadata == null ? null : d.Metadata.CustomerName,
                d.Metadata == null ? null : d.Metadata.ProjectNo))
            .ToListAsync(ct);
    }

    public async Task<int> QueueParseAsync(IReadOnlyList<Guid> docIds, CancellationToken ct = default)
    {
        var maxBatch = await config.GetIntAsync(ConfigKeys.UploadMaxBatch, 50, ct);
        if (docIds.Count > maxBatch)
            throw new DomainRuleException("BATCH_TOO_LARGE", $"一次最多提交 {maxBatch} 份");
        var docs = await db.Documents.Where(d => docIds.Contains(d.Id)).ToListAsync(ct);
        var queued = 0;
        foreach (var doc in docs)
        {
            if (doc.ParseStatus is ParseStatus.Queued or ParseStatus.Parsing or ParseStatus.Reparsing) continue;
            doc.ParseStatus = ParseStatus.Queued;
            doc.ParseError = null;
            db.ParseJobs.Add(new ParseJob
            {
                Id = Guid.NewGuid(),
                DocId = doc.Id,
                Kind = ParseJobKind.Parse,
                QueuedBy = me.UserId,
                QueuedAt = DateTimeOffset.UtcNow
            });
            queued++;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("doc.queue_parse", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new { requested = docIds.Count, queued }), ct);
        return queued;
    }

    public async Task ReparseAsync(Guid docId, ChunkStrategy? newStrategy, CancellationToken ct = default)
    {
        var doc = await db.Documents.FindAsync([docId], ct)
            ?? throw new DomainRuleException("DOC_NOT_FOUND", "文档不存在");
        if (newStrategy is not null) doc.ChunkStrategyOverride = newStrategy;
        doc.ParseStatus = ParseStatus.Queued;
        doc.ParseError = null;
        db.ParseJobs.Add(new ParseJob
        {
            Id = Guid.NewGuid(),
            DocId = doc.Id,
            Kind = ParseJobKind.Reparse,
            QueuedBy = me.UserId,
            QueuedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("doc.reparse", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "document", TargetId: docId.ToString(), Detail: new { newStrategy }), ct);
    }

    public async Task<IReadOnlyList<ChunkRow>> GetChunksAsync(Guid docId, CancellationToken ct = default)
        => await db.Chunks.AsNoTracking()
            .Where(c => c.DocId == docId && c.IsActive)
            .OrderBy(c => c.Seq)
            .Select(c => new ChunkRow(c.Id, c.Seq, c.SectionPath, c.PageNo, c.Text, c.EmbeddingModel, c.IsActive))
            .ToListAsync(ct);

    public async Task EditChunkAsync(long chunkId, string newText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newText))
            throw new DomainRuleException("CHUNK_TEXT_EMPTY", "分块内容不能为空；要移除请用删除");
        var chunk = await db.Chunks.FirstOrDefaultAsync(c => c.Id == chunkId, ct)
            ?? throw new DomainRuleException("CHUNK_NOT_FOUND", "分块不存在");
        chunk.Text = newText;
        chunk.SearchText = ChineseTokenizer.Tokenize(newText);
        chunk.Embedding = null; // 仅重算该块向量，不触发整份重解析（FR-1.5）
        chunk.EmbeddingModel = null;
        db.ParseJobs.Add(new ParseJob
        {
            Id = Guid.NewGuid(),
            DocId = chunk.DocId,
            ChunkId = chunk.Id,
            Kind = ParseJobKind.EmbedOnly,
            QueuedBy = me.UserId,
            QueuedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("chunk.edit", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "chunk", TargetId: chunkId.ToString()), ct);
    }

    public async Task DeleteChunkAsync(long chunkId, CancellationToken ct = default)
    {
        var chunk = await db.Chunks.FirstOrDefaultAsync(c => c.Id == chunkId, ct)
            ?? throw new DomainRuleException("CHUNK_NOT_FOUND", "分块不存在");
        chunk.IsActive = false;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("chunk.delete", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "chunk", TargetId: chunkId.ToString()), ct);
    }

    public async Task<(Stream Content, string FileName, string ContentType)> DownloadAsync(Guid docId, CancellationToken ct = default)
    {
        var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == docId, ct)
            ?? throw new DomainRuleException("DOC_NOT_FOUND", "文档不存在");
        // 按权限校验（表 8-1 /api/files/{id}）：密级不在角色可及范围内即拒绝并审计
        if (!me.Classifications.Contains(doc.Classification))
        {
            await audit.WriteAsync(new AuditEntry("doc.download", AuditResult.Denied,
                UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                TargetType: "document", TargetId: docId.ToString(),
                Detail: new { doc.Classification, reason = "classification_out_of_scope" }), ct);
            throw new DomainRuleException("FORBIDDEN_CLASSIFICATION",
                $"该文档密级为{ClassificationName(doc.Classification)}，你的角色不在可见范围内");
        }
        var stream = await storage.OpenAsync(doc.FileKey, ct);
        await audit.WriteAsync(new AuditEntry("doc.download", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "document", TargetId: docId.ToString()), ct);
        return (stream, doc.FileName, doc.ContentType);
    }

    internal static string ClassificationName(Classification c) => c switch
    {
        Classification.Confidential => "机密",
        Classification.Internal => "内部",
        _ => "公开"
    };
}

/// <summary>受控词表键（FR-3.2）。</summary>
public static class VocabKeys
{
    public const string DeviceType = "device_type";
    public const string DocCategory = "doc_category";
    public const string CustomerName = "customer_name";
    public const string TermDomain = "term_domain";
}
