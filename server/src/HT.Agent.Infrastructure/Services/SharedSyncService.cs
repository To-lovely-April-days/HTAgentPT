using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace HT.Agent.Infrastructure.Services;

/// <summary>共享库同步（FR-2.2）：总部导出 → 公司拉取合入，单向覆盖。
/// 同步内容为原始文件、元数据与分块文本；向量默认在本地按本机模型重新生成，
/// 仅当双方向量化模型版本标识一致时随包复用（省去重算）。
/// 这是公司节点上共享库唯一合法的写入通道——其余写路径全被 FR-2.2 挡住。</summary>
public class SharedSyncService(
    AppDbContext db,
    IHttpClientFactory httpFactory,
    IEmbeddingClient embedder,
    IFileStorage storage,
    IRuntimeConfig config,
    IAuditWriter audit,
    ICurrentUser me,
    IOptions<NodeOptions> node) : ISharedSyncService
{
    /// <summary>节点间线格式：Web 默认 + 字符串枚举，与 MVC 出站序列化一致。</summary>
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // ── 总部侧导出 ─────────────────────────────────────────────

    public async Task<SyncPackage> ExportAsync(DateTimeOffset? since, string? requesterEmbeddingTag, CancellationToken ct = default)
    {
        if (!node.Value.IsHeadquarters)
            throw new DomainRuleException("NOT_HQ", "只有总部节点提供共享库导出");

        var localTag = (await embedder.EmbedAsync(["_tag_probe_"], ct)).ModelTag;
        var includeVectors = requesterEmbeddingTag == localTag;

        var sharedKbIds = await db.KnowledgeBases.AsNoTracking()
            .Where(k => k.Tier == KnowledgeBaseTier.Shared).Select(k => k.Id).ToListAsync(ct);

        var docsQuery = db.Documents.AsNoTracking().Include(d => d.Metadata)
            .Where(d => sharedKbIds.Contains(d.KbId) && d.ParseStatus == ParseStatus.Parsed && !d.IsWithdrawn);
        if (since is not null)
            docsQuery = docsQuery.Where(d => (d.ParsedAt ?? d.UploadedAt) > since);
        var docs = await docsQuery.ToListAsync(ct);

        var syncDocs = new List<SyncDoc>();
        foreach (var d in docs)
        {
            var chunks = await db.Chunks.AsNoTracking()
                .Where(c => c.DocId == d.Id && c.IsActive)
                .OrderBy(c => c.Seq)
                .Select(c => new { c.Seq, c.SectionPath, c.PageNo, c.Bbox, c.Text, c.Embedding, c.EmbeddingModel })
                .ToListAsync(ct);
            syncDocs.Add(new SyncDoc(
                d.Id, d.Title, d.FileName, d.ContentType, d.FileSize, d.Classification,
                d.ParseVersion, d.ParsedAt ?? d.UploadedAt,
                d.Metadata is null ? null : new SyncMeta(
                    d.Metadata.CustomerName, d.Metadata.Year, d.Metadata.DeviceType,
                    d.Metadata.DocCategory, d.Metadata.ProjectNo, d.Metadata.DocVersion),
                chunks.Select(c => new SyncChunk(c.Seq, c.SectionPath, c.PageNo, c.Bbox, c.Text,
                    includeVectors && c.EmbeddingModel == localTag ? c.Embedding?.ToArray() : null)).ToList()));
        }

        var withdrawn = await db.Documents.AsNoTracking()
            .Where(d => sharedKbIds.Contains(d.KbId) && d.IsWithdrawn)
            .Select(d => d.Id).ToListAsync(ct);

        var batchNo = $"SB-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        await audit.WriteAsync(new AuditEntry("sync.export", AuditResult.Success,
            UserId: me.IsAuthenticated ? me.UserId : null, Username: me.IsAuthenticated ? me.Username : "sync-node",
            Detail: new { batchNo, docs = syncDocs.Count, withdrawn = withdrawn.Count, includeVectors, since }), ct);
        return new SyncPackage(batchNo, DateTimeOffset.UtcNow, localTag, syncDocs, withdrawn);
    }

    public async Task<(Stream Content, string FileName, string ContentType)> ExportFileAsync(Guid docId, CancellationToken ct = default)
    {
        if (!node.Value.IsHeadquarters)
            throw new DomainRuleException("NOT_HQ", "只有总部节点提供共享库文件导出");
        var doc = await db.Documents.AsNoTracking().Include(d => d.Kb)
            .FirstOrDefaultAsync(d => d.Id == docId, ct)
            ?? throw new DomainRuleException("DOC_NOT_FOUND", "文档不存在");
        if (doc.Kb!.Tier != KnowledgeBaseTier.Shared)
            throw new DomainRuleException("NOT_SHARED", "只导出共享库文档");
        return (await storage.OpenAsync(doc.FileKey, ct), doc.FileName, doc.ContentType);
    }

    // ── 公司侧拉取合入 ───────────────────────────────────────────

    public async Task<SyncResult> PullAsync(bool full = false, CancellationToken ct = default)
    {
        var hqUrl = (await config.GetStringAsync(ConfigKeys.SyncHqUrl, "", ct)).TrimEnd('/');
        var token = await config.GetStringAsync(ConfigKeys.SyncToken, "", ct);
        if (hqUrl.Length == 0)
            throw new DomainRuleException("SYNC_NOT_CONFIGURED", "尚未配置总部节点地址（sync.hq_url）");

        var localTag = (await embedder.EmbedAsync(["_tag_probe_"], ct)).ModelTag;
        var lastSync = full
            ? null
            : await db.SyncLogs.AsNoTracking()
                .Where(l => l.Kind == "shared_pull" && l.Ok == true)
                .OrderByDescending(l => l.StartedAt)
                .Select(l => (DateTimeOffset?)l.StartedAt)
                .FirstOrDefaultAsync(ct);

        var http = httpFactory.CreateClient("sync");
        SyncPackage package;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{hqUrl}/api/sync/shared/export{(lastSync is null ? "" : $"?since={Uri.EscapeDataString(lastSync.Value.ToString("O"))}")}");
            req.Headers.Add("X-Sync-Token", token);
            req.Headers.Add("X-Embedding-Tag", localTag);
            var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new DomainRuleException("SYNC_HQ_REFUSED", $"总部节点拒绝（HTTP {(int)resp.StatusCode}）：检查 sync.token 与总部的 sync.accept_token");
            // 出站由 MVC 以字符串枚举序列化，入站读取必须用同一约定
            package = await resp.Content.ReadFromJsonAsync<SyncPackage>(WireJson, ct)
                ?? throw new DomainRuleException("SYNC_EMPTY", "总部返回空包");
        }
        catch (HttpRequestException ex)
        {
            throw new DomainRuleException("SYNC_HQ_UNREACHABLE", $"总部节点不可达：{ex.Message}。请求未被丢弃，稍后重试即可（10.3）");
        }

        var log = new SyncLog
        {
            Id = Guid.NewGuid(),
            Kind = "shared_pull",
            BatchNo = package.BatchNo,
            StartedAt = DateTimeOffset.UtcNow
        };
        db.SyncLogs.Add(log);
        await db.SaveChangesAsync(ct);

        var kb = await GetOrCreateLocalSharedKbAsync(ct);
        var warnings = new List<string>();
        int upserted = 0, chunksWritten = 0, vectorsReused = 0, queuedEmbed = 0, withdrawn = 0;

        foreach (var sd in package.Docs)
        {
            ct.ThrowIfCancellationRequested();
            // 原始文件（FR-2.2）：新文档或版本变化时拉取
            var doc = await db.Documents.Include(d => d.Metadata).FirstOrDefaultAsync(d => d.Id == sd.Id, ct);
            string? fileKey = doc?.FileKey;
            var isVirtual = sd.ContentType == "application/x-ht-virtual";
            if (isVirtual)
            {
                fileKey = $"virtual:synced:{sd.Id}"; // 无原始文件；前缀保持语料管理写保护生效
            }
            else if (doc is null || doc.ParseVersion != sd.ParseVersion)
            {
                try
                {
                    using var freq = new HttpRequestMessage(HttpMethod.Get, $"{hqUrl}/api/sync/shared/files/{sd.Id}");
                    freq.Headers.Add("X-Sync-Token", token);
                    var fresp = await http.SendAsync(freq, HttpCompletionOption.ResponseHeadersRead, ct);
                    fresp.EnsureSuccessStatusCode();
                    await using var fs = await fresp.Content.ReadAsStreamAsync(ct);
                    fileKey = await storage.SaveAsync(fs, sd.FileName, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    warnings.Add($"{sd.Title}：原始文件拉取失败（{ex.Message}），分块已合入、原件待下次同步补拉");
                    fileKey ??= $"pending-sync:{sd.Id}";
                }
            }

            if (doc is null)
            {
                doc = new Document
                {
                    Id = sd.Id,
                    KbId = kb.Id,
                    FileName = sd.FileName,
                    Title = sd.Title,
                    FileKey = fileKey!,
                    FileSize = sd.FileSize,
                    ContentType = sd.ContentType,
                    Classification = sd.Classification,
                    ParseStatus = ParseStatus.Parsed,
                    ParseVersion = sd.ParseVersion,
                    UploadedById = me.UserId,
                    UploadedAt = sd.UpdatedAt,
                    ParsedAt = sd.UpdatedAt
                };
                db.Documents.Add(doc);
            }
            else
            {
                // 单向覆盖：本地状态无条件对齐总部
                doc.KbId = kb.Id;
                doc.Title = sd.Title;
                doc.FileName = sd.FileName;
                doc.FileKey = fileKey!;
                doc.FileSize = sd.FileSize;
                doc.ContentType = sd.ContentType;
                doc.Classification = sd.Classification;
                doc.ParseStatus = ParseStatus.Parsed;
                doc.ParseVersion = sd.ParseVersion;
                doc.IsWithdrawn = false;
                doc.ParsedAt = sd.UpdatedAt;
            }
            if (sd.Metadata is not null)
            {
                doc.Metadata ??= new DocMetadata { DocumentId = sd.Id, CustomerName = "-", Year = 0, DeviceType = "-", DocCategory = "-" };
                doc.Metadata.CustomerName = sd.Metadata.CustomerName;
                doc.Metadata.Year = sd.Metadata.Year;
                doc.Metadata.DeviceType = sd.Metadata.DeviceType;
                doc.Metadata.DocCategory = sd.Metadata.DocCategory;
                doc.Metadata.ProjectNo = sd.Metadata.ProjectNo;
                doc.Metadata.DocVersion = sd.Metadata.DocVersion;
            }
            await db.SaveChangesAsync(ct);

            // 分块整替（快照语义）：向量按标识决定复用或重算（FR-2.2）
            await db.Chunks.Where(c => c.DocId == sd.Id).ExecuteDeleteAsync(ct);
            var needEmbed = false;
            foreach (var c in sd.Chunks)
            {
                var reuse = c.Embedding is not null && package.EmbeddingModelTag == localTag;
                if (reuse) vectorsReused++;
                else needEmbed = true;
                db.Chunks.Add(new Chunk
                {
                    DocId = sd.Id,
                    KbId = kb.Id,
                    Classification = sd.Classification,
                    Seq = c.Seq,
                    SectionPath = c.SectionPath,
                    PageNo = c.PageNo,
                    Bbox = c.Bbox,
                    Text = c.Text,
                    SearchText = ChineseTokenizer.Tokenize(c.Text),
                    Embedding = reuse ? new Vector(c.Embedding!) : null,
                    EmbeddingModel = reuse ? localTag : null,
                    ParseVersion = sd.ParseVersion,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                chunksWritten++;
            }
            if (needEmbed)
            {
                db.ParseJobs.Add(new ParseJob
                {
                    Id = Guid.NewGuid(),
                    DocId = sd.Id,
                    Kind = ParseJobKind.EmbedDoc,
                    QueuedBy = me.UserId,
                    QueuedAt = DateTimeOffset.UtcNow
                });
                queuedEmbed++;
            }
            await db.SaveChangesAsync(ct);
            upserted++;
        }

        // 同步删除（FR-2.6）：标记撤回并停止参与检索；原文件保留至下次全量同步后清理
        foreach (var id in package.WithdrawnDocIds)
        {
            var n = await db.Documents.Where(d => d.Id == id && !d.IsWithdrawn)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.IsWithdrawn, true), ct);
            withdrawn += n;
        }
        if (full)
        {
            // 全量对齐：总部快照里既不在文档列表也不在撤回列表的本地共享文档，一律标撤
            var known = package.Docs.Select(d => d.Id).Concat(package.WithdrawnDocIds).ToList();
            withdrawn += await db.Documents
                .Where(d => d.KbId == kb.Id && !d.IsWithdrawn && !known.Contains(d.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.IsWithdrawn, true), ct);
        }

        // 案例审核状态回传（FR-8.4/8.5）：本公司在总部待审的案例，随同一通道回查结果
        var caseUpdates = 0;
        var pendingIds = await db.FaultCases
            .Where(c => c.SyncStatus == CaseSyncStatus.Pending)
            .Select(c => c.Id).ToListAsync(ct);
        if (pendingIds.Count > 0)
        {
            try
            {
                using var sreq = new HttpRequestMessage(HttpMethod.Get,
                    $"{hqUrl}/api/sync/cases/status?ids={string.Join(',', pendingIds)}");
                sreq.Headers.Add("X-Sync-Token", token);
                var sresp = await http.SendAsync(sreq, ct);
                if (sresp.IsSuccessStatusCode)
                {
                    var statuses = await sresp.Content.ReadFromJsonAsync<List<CaseStatusRow>>(WireJson, ct) ?? [];
                    foreach (var st in statuses)
                    {
                        var c = await db.FaultCases.FirstOrDefaultAsync(x => x.Id == st.CaseId, ct);
                        if (c is null || !Enum.TryParse<CaseSyncStatus>(st.SyncStatus, out var parsed)) continue;
                        if (parsed == c.SyncStatus) continue;
                        c.SyncStatus = parsed;
                        c.RejectReason = st.RejectReason; // 驳回原因原样回传
                        caseUpdates++;
                    }
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (HttpRequestException)
            {
                warnings.Add("案例审核状态回查失败，下次同步补查");
            }
        }

        log.FinishedAt = DateTimeOffset.UtcNow;
        log.Ok = true;
        log.Detail = System.Text.Json.JsonSerializer.Serialize(new
        {
            upserted, chunksWritten, vectorsReused, queuedEmbed, withdrawn, caseUpdates,
            hqTag = package.EmbeddingModelTag, localTag, warnings
        });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("sync.pull", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new { package.BatchNo, upserted, vectorsReused, queuedEmbed, withdrawn }), ct);
        return new SyncResult(package.BatchNo, upserted, chunksWritten, vectorsReused, queuedEmbed, withdrawn, warnings);
    }

    private async Task<KnowledgeBase> GetOrCreateLocalSharedKbAsync(CancellationToken ct)
    {
        var kb = await db.KnowledgeBases
            .Where(k => k.Tier == KnowledgeBaseTier.Shared && k.IsActive)
            .OrderBy(k => k.CreatedAt).FirstOrDefaultAsync(ct);
        if (kb is not null) return kb;
        kb = new KnowledgeBase
        {
            Id = Guid.NewGuid(),
            Name = "集团共享库",
            Tier = KnowledgeBaseTier.Shared,
            CompanyId = null,
            DefaultChunkStrategy = ChunkStrategy.General,
            Description = "总部下发，本地只读（FR-2.2 单向覆盖）",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.KnowledgeBases.Add(kb);
        await db.SaveChangesAsync(ct);
        return kb;
    }
}
