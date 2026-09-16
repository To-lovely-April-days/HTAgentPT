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
    /// <summary>共享库写保护（FR-2.2：同步为单向覆盖，本地不得直接修改共享库内容）。
    /// 上传、分块编辑/删除/合并/拆分、重解析、提交解析、元数据修改全部经此校验。</summary>
    private async Task EnsureKbWritableAsync(Guid kbId, CancellationToken ct)
    {
        if (node.Value.IsHeadquarters) return;
        var tier = await db.KnowledgeBases.Where(k => k.Id == kbId).Select(k => k.Tier).FirstAsync(ct);
        if (tier == KnowledgeBaseTier.Shared)
            throw new DomainRuleException("SHARED_KB_READONLY", "共享库由总部下发，本地不得直接修改（FR-2.2）");
    }

    /// <summary>虚拟容器文档（如故障案例容器）的分块由对应业务模块托管：
    /// 语料管理的解析与分块写操作动了它，案例行与分块就会失联。</summary>
    private async Task EnsureNotVirtualAsync(Guid docId, CancellationToken ct)
    {
        var fileKey = await db.Documents.Where(d => d.Id == docId).Select(d => d.FileKey).FirstOrDefaultAsync(ct);
        if (fileKey is not null && fileKey.StartsWith("virtual:", StringComparison.Ordinal))
            throw new DomainRuleException("VIRTUAL_DOC_MANAGED",
                "该文档由业务模块自动维护（故障案例等），请在对应模块中修改内容");
    }

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
        foreach (var kbId in docs.Select(d => d.KbId).Distinct())
            await EnsureKbWritableAsync(kbId, ct);
        foreach (var doc in docs.Where(d => d.FileKey.StartsWith("virtual:", StringComparison.Ordinal)))
            throw new DomainRuleException("VIRTUAL_DOC_MANAGED", $"「{doc.Title}」由业务模块自动维护，不参与解析");
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
        await EnsureNotVirtualAsync(docId, ct);
        await EnsureKbWritableAsync(doc.KbId, ct);
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
            .Select(c => new ChunkRow(c.Id, c.Seq, c.SectionPath, c.PageNo, c.Bbox, c.Text, c.EmbeddingModel, c.IsActive))
            .ToListAsync(ct);

    public async Task EditChunkAsync(long chunkId, string newText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newText))
            throw new DomainRuleException("CHUNK_TEXT_EMPTY", "分块内容不能为空；要移除请用删除");
        var chunk = await db.Chunks.FirstOrDefaultAsync(c => c.Id == chunkId, ct)
            ?? throw new DomainRuleException("CHUNK_NOT_FOUND", "分块不存在");
        await EnsureNotVirtualAsync(chunk.DocId, ct);
        await EnsureKbWritableAsync(chunk.KbId, ct);
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
        await EnsureNotVirtualAsync(chunk.DocId, ct);
        await EnsureKbWritableAsync(chunk.KbId, ct);
        chunk.IsActive = false;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("chunk.delete", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "chunk", TargetId: chunkId.ToString()), ct);
    }

    /// <summary>合并相邻分块（FR-1.5）：同文档、激活态、seq 连续。合并块继承首块的章节与页码。
    /// 校验与改写在同一事务内、行锁保护下进行；合并后整篇 seq 重排，不留空洞——
    /// 否则相邻性检查会把空洞两侧的块永远判为「不相邻」。</summary>
    public async Task<long> MergeChunksAsync(IReadOnlyList<long> chunkIds, CancellationToken ct = default)
    {
        if (chunkIds.Count < 2)
            throw new DomainRuleException("MERGE_NEED_TWO", "合并至少选两块");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ids = chunkIds.ToArray();
        // 行锁后再校验：并发的合并/拆分/删除要么排队要么看到已变化的世界
        var chunks = await db.Chunks
            .FromSqlInterpolated($"SELECT * FROM chunk WHERE id = ANY({ids}) FOR UPDATE")
            .OrderBy(c => c.Seq).ToListAsync(ct);
        if (chunks.Count != chunkIds.Count || chunks.Any(c => !c.IsActive))
            throw new DomainRuleException("MERGE_CHUNK_MISSING", "有分块不存在或已删除");
        if (chunks.Select(c => c.DocId).Distinct().Count() != 1)
            throw new DomainRuleException("MERGE_CROSS_DOC", "只能合并同一文档内的分块");
        for (var i = 1; i < chunks.Count; i++)
            if (chunks[i].Seq != chunks[i - 1].Seq + 1)
                throw new DomainRuleException("MERGE_NOT_ADJACENT", "只能合并 seq 连续的相邻分块");
        await EnsureNotVirtualAsync(chunks[0].DocId, ct);
        await EnsureKbWritableAsync(chunks[0].KbId, ct);

        var head = chunks[0];
        var removed = chunks.Count - 1;
        head.Text = string.Join("\n", chunks.Select(c => c.Text));
        head.SearchText = ChineseTokenizer.Tokenize(head.Text);
        head.Embedding = null;
        head.EmbeddingModel = null;
        db.Chunks.RemoveRange(chunks.Skip(1));
        db.ParseJobs.Add(new ParseJob
        {
            Id = Guid.NewGuid(), DocId = head.DocId, ChunkId = head.Id,
            Kind = ParseJobKind.EmbedOnly, QueuedBy = me.UserId, QueuedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        // 后续块前移补洞（被删块之后的所有块）
        await db.Chunks.Where(c => c.DocId == head.DocId && c.Seq > head.Seq + removed)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Seq, c => c.Seq - removed), ct);
        await tx.CommitAsync(ct);
        await audit.WriteAsync(new AuditEntry("chunk.merge", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "chunk", TargetId: head.Id.ToString(), Detail: new { merged = chunkIds }), ct);
        return head.Id;
    }

    /// <summary>拆分单块（FR-1.5）：按字符偏移切开，后续分块 seq 顺延，各新块逐块重算向量。</summary>
    public async Task<IReadOnlyList<long>> SplitChunkAsync(long chunkId, IReadOnlyList<int> offsets, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // 行锁内取最新值：并发的另一个结构编辑改过 Seq/Text 后，这里拿到的才是真实基准
        var chunk = (await db.Chunks
                .FromSqlInterpolated($"SELECT * FROM chunk WHERE id = {chunkId} AND is_active FOR UPDATE")
                .ToListAsync(ct)).FirstOrDefault()
            ?? throw new DomainRuleException("CHUNK_NOT_FOUND", "分块不存在");
        await EnsureNotVirtualAsync(chunk.DocId, ct);
        await EnsureKbWritableAsync(chunk.KbId, ct);
        IReadOnlyList<string> parts;
        try { parts = ChunkSplitter.Split(chunk.Text, offsets); }
        catch (ArgumentException ex) { throw new DomainRuleException("SPLIT_INVALID", ex.Message); }

        // 后续块 seq 让位
        await db.Chunks.Where(c => c.DocId == chunk.DocId && c.IsActive && c.Seq > chunk.Seq)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Seq, c => c.Seq + parts.Count - 1), ct);

        chunk.Text = parts[0];
        chunk.SearchText = ChineseTokenizer.Tokenize(parts[0]);
        chunk.Embedding = null;
        chunk.EmbeddingModel = null;
        var newChunks = new List<Chunk>();
        for (var i = 1; i < parts.Count; i++)
        {
            newChunks.Add(new Chunk
            {
                DocId = chunk.DocId, KbId = chunk.KbId, Classification = chunk.Classification,
                Seq = chunk.Seq + i, SectionPath = chunk.SectionPath, PageNo = chunk.PageNo,
                Text = parts[i], SearchText = ChineseTokenizer.Tokenize(parts[i]),
                ParseVersion = chunk.ParseVersion, CreatedAt = DateTimeOffset.UtcNow
            });
        }
        db.Chunks.AddRange(newChunks);
        await db.SaveChangesAsync(ct);

        var ids = new List<long> { chunk.Id };
        ids.AddRange(newChunks.Select(c => c.Id));
        foreach (var id in ids)
            db.ParseJobs.Add(new ParseJob
            {
                Id = Guid.NewGuid(), DocId = chunk.DocId, ChunkId = id,
                Kind = ParseJobKind.EmbedOnly, QueuedBy = me.UserId, QueuedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await audit.WriteAsync(new AuditEntry("chunk.split", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "chunk", TargetId: chunkId.ToString(), Detail: new { parts = parts.Count }), ct);
        return ids;
    }

    /// <summary>批量修正元数据（FR-3.6）：历史资料归集阶段的集中整理。受控字段仍走词表校验。</summary>
    public async Task<int> BatchUpdateMetadataAsync(MetadataBatchFilter filter, MetadataBatchSet set, CancellationToken ct = default)
    {
        if (filter.KbId is null && string.IsNullOrWhiteSpace(filter.CustomerName) &&
            string.IsNullOrWhiteSpace(filter.DocCategory) && filter.Year is null &&
            string.IsNullOrWhiteSpace(filter.DeviceType))
            throw new DomainRuleException("BATCH_FILTER_EMPTY", "批量修正必须至少给一个筛选条件，不允许全库改写");
        var hasSet = !string.IsNullOrWhiteSpace(set.CustomerName) || !string.IsNullOrWhiteSpace(set.DeviceType) ||
                     !string.IsNullOrWhiteSpace(set.DocCategory) || set.Year is not null ||
                     !string.IsNullOrWhiteSpace(set.ProjectNo);
        if (!hasSet)
            throw new DomainRuleException("BATCH_SET_EMPTY", "没有要写入的值");
        if (!string.IsNullOrWhiteSpace(set.DeviceType) && !await vocab.IsValidAsync(VocabKeys.DeviceType, set.DeviceType, ct))
            throw new DomainRuleException("META_DEVICE_TYPE", $"设备类型「{set.DeviceType}」不在受控词表中");
        if (!string.IsNullOrWhiteSpace(set.DocCategory) && !await vocab.IsValidAsync(VocabKeys.DocCategory, set.DocCategory, ct))
            throw new DomainRuleException("META_DOC_CATEGORY", $"文档类别「{set.DocCategory}」不在受控词表中");
        if (!string.IsNullOrWhiteSpace(set.ProjectNo) && !await db.Projects.AnyAsync(p => p.ProjectNo == set.ProjectNo, ct))
            throw new DomainRuleException("META_PROJECT_NOT_FOUND", $"项目编号 {set.ProjectNo} 不在台账中");

        var q = db.DocMetadatas.AsQueryable();
        // 共享库元数据由总部下发，本地批改不触碰（FR-2.2）
        if (!node.Value.IsHeadquarters)
            q = q.Where(m => db.Documents.Any(d => d.Id == m.DocumentId &&
                db.KnowledgeBases.Any(k => k.Id == d.KbId && k.Tier != KnowledgeBaseTier.Shared)));
        if (filter.KbId is not null)
            q = q.Where(m => db.Documents.Any(d => d.Id == m.DocumentId && d.KbId == filter.KbId));
        if (!string.IsNullOrWhiteSpace(filter.CustomerName)) q = q.Where(m => m.CustomerName == filter.CustomerName);
        if (!string.IsNullOrWhiteSpace(filter.DocCategory)) q = q.Where(m => m.DocCategory == filter.DocCategory);
        if (filter.Year is not null) q = q.Where(m => m.Year == filter.Year);
        if (!string.IsNullOrWhiteSpace(filter.DeviceType)) q = q.Where(m => m.DeviceType == filter.DeviceType);

        var rows = await q.ToListAsync(ct);
        foreach (var m in rows)
        {
            if (!string.IsNullOrWhiteSpace(set.CustomerName)) m.CustomerName = set.CustomerName;
            if (!string.IsNullOrWhiteSpace(set.DeviceType)) m.DeviceType = set.DeviceType;
            if (!string.IsNullOrWhiteSpace(set.DocCategory)) m.DocCategory = set.DocCategory;
            if (set.Year is not null) m.Year = set.Year.Value;
            if (!string.IsNullOrWhiteSpace(set.ProjectNo)) m.ProjectNo = set.ProjectNo;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("meta.batch_update", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new { filter, set, affected = rows.Count }), ct);
        return rows.Count;
    }

    public async Task<(Stream Content, string FileName, string ContentType)> DownloadAsync(Guid docId, CancellationToken ct = default)
    {
        var doc = await db.Documents.AsNoTracking().Include(d => d.Kb)
            .FirstOrDefaultAsync(d => d.Id == docId, ct)
            ?? throw new DomainRuleException("DOC_NOT_FOUND", "文档不存在");

        // 下载口与检索 SQL 同一套过滤（FR-4.4：密级、公司归属与数据范围三项同过）：
        // 检索挡住的东西，换个接口不能就拿得到。
        var deny = DownloadDenyReason(doc);
        if (deny is not null)
        {
            await audit.WriteAsync(new AuditEntry("doc.download", AuditResult.Denied,
                UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                TargetType: "document", TargetId: docId.ToString(),
                Detail: new { reason = deny }), ct);
            throw new ForbiddenException("FORBIDDEN_DOCUMENT", "你没有查看该文档的权限。本次请求已被记录。");
        }

        if (doc.FileKey.StartsWith("virtual:", StringComparison.Ordinal))
            throw new DomainRuleException("VIRTUAL_DOC_NO_FILE", "该条目由业务模块自动维护，没有可下载的原始文件");

        var stream = await storage.OpenAsync(doc.FileKey, ct);
        await audit.WriteAsync(new AuditEntry("doc.download", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "document", TargetId: docId.ToString()), ct);
        return (stream, doc.FileName, doc.ContentType);
    }

    public async Task<IReadOnlyList<DocImageRow>> ListImagesAsync(Guid docId, CancellationToken ct = default)
    {
        await EnsureDocAccessAsync(docId, "doc.images", ct);
        return await db.DocImages.AsNoTracking()
            .Where(i => i.DocId == docId).OrderBy(i => i.Seq)
            .Select(i => new DocImageRow(i.Id, i.Caption, i.PageNo, i.Bbox, i.Seq))
            .ToListAsync(ct);
    }

    public async Task<(Stream Content, string ContentType)> OpenImageAsync(Guid docId, long imageId, CancellationToken ct = default)
    {
        await EnsureDocAccessAsync(docId, "doc.image_view", ct);
        var img = await db.DocImages.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == imageId && i.DocId == docId, ct)
            ?? throw new DomainRuleException("IMAGE_NOT_FOUND", "图片不存在");
        return (await storage.OpenAsync(img.FileKey, ct), img.ContentType);
    }

    public async Task DeleteDocumentAsync(Guid docId, CancellationToken ct = default)
    {
        var doc = await db.Documents.Include(d => d.Kb)
            .FirstOrDefaultAsync(d => d.Id == docId, ct)
            ?? throw new DomainRuleException("DOC_NOT_FOUND", "文档不存在");
        if (doc.Kb!.Tier == KnowledgeBaseTier.Shared && !node.Value.IsHeadquarters)
            throw new DomainRuleException("SHARED_KB_READONLY",
                "共享库内容由总部下发，本地不能直接删除——由总部撤回后随同步移除（FR-2.2）");
        if (doc.FileKey.StartsWith("virtual:", StringComparison.Ordinal))
            throw new DomainRuleException("VIRTUAL_DOC_NO_DELETE",
                "该条目由业务模块自动维护（故障案例等），请在对应模块中处理");
        if (await db.ParseJobs.AnyAsync(j => j.DocId == docId && j.Status == JobStatus.Running, ct))
            throw new DomainRuleException("DOC_PARSING", "该文档正在解析中，等本轮结束或失败后再删除");

        // 存储文件先收集后删：数据库行（分块/图片/元数据/任务）随文档级联删除
        var fileKeys = new List<string> { doc.FileKey };
        fileKeys.AddRange(await db.DocImages.Where(i => i.DocId == docId).Select(i => i.FileKey).ToListAsync(ct));
        var title = doc.Title;
        db.Documents.Remove(doc);
        await db.SaveChangesAsync(ct);
        foreach (var key in fileKeys)
            try { await storage.DeleteAsync(key, ct); } catch (IOException) { /* 孤儿文件不挡删除 */ }

        await audit.WriteAsync(new AuditEntry("doc.delete", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "document", TargetId: docId.ToString(),
            Detail: new { title, kb = doc.Kb!.Name, files = fileKeys.Count }), ct);
    }

    /// <summary>图片口与下载口同一套过滤（FR-4.4）：检索挡住的东西，换个接口不能就拿得到。</summary>
    private async Task EnsureDocAccessAsync(Guid docId, string action, CancellationToken ct)
    {
        var doc = await db.Documents.AsNoTracking().Include(d => d.Kb)
            .FirstOrDefaultAsync(d => d.Id == docId, ct)
            ?? throw new DomainRuleException("DOC_NOT_FOUND", "文档不存在");
        var deny = DownloadDenyReason(doc);
        if (deny is null) return;
        await audit.WriteAsync(new AuditEntry(action, AuditResult.Denied,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "document", TargetId: docId.ToString(),
            Detail: new { reason = deny }), ct);
        throw new ForbiddenException("FORBIDDEN_DOCUMENT", "你没有查看该文档的权限。本次请求已被记录。");
    }

    /// <summary>下载拒绝原因（进审计详情）；null 为放行。对外消息统一模糊，不泄露文档属性。</summary>
    private string? DownloadDenyReason(Document doc)
    {
        if (doc.IsWithdrawn)
            return "withdrawn"; // 撤回即停止对外可见（FR-2.4/2.6）
        if (!me.Classifications.Contains(doc.Classification))
            return "classification_out_of_scope";
        var kb = doc.Kb!;
        // 公司归属：私有库与公开库属本公司；共享库属集团
        if (kb.Tier != KnowledgeBaseTier.Shared && kb.CompanyId != me.CompanyId)
            return "company_out_of_scope";
        // 数据范围（表 3-1）：总部审核人无任何私有库通道；仅公开权限的角色（客户）只可及公开库
        if (kb.Tier == KnowledgeBaseTier.Private && me.RoleCode == RoleCodes.HqReviewer)
            return "reviewer_no_private_access";
        var hasInternalScope = me.Permissions.Contains(PermissionKeys.QaInternal) ||
                               me.Permissions.Contains(PermissionKeys.CorpusManage) ||
                               me.Permissions.Contains(PermissionKeys.KbManage);
        if (kb.Tier != KnowledgeBaseTier.Public && !hasInternalScope)
            return "tier_out_of_scope"; // 客户拿到 docId 也下不了私有库里密级标公开的未发布文档
        return null;
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
