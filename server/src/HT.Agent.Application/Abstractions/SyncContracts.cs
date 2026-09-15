using HT.Agent.Domain;

namespace HT.Agent.Application.Abstractions;

/// <summary>共享库同步（FR-2.2/2.6）。总部导出、公司拉取合入，单向覆盖。</summary>
public interface ISharedSyncService
{
    /// <summary>总部侧：导出共享库增量包。requesterEmbeddingTag 与本节点一致时附带向量（省去对端重算）。</summary>
    Task<SyncPackage> ExportAsync(DateTimeOffset? since, string? requesterEmbeddingTag, CancellationToken ct = default);
    /// <summary>总部侧：按文档流出原始文件（FR-2.2：同步内容含原始文件）。</summary>
    Task<(Stream Content, string FileName, string ContentType)> ExportFileAsync(Guid docId, CancellationToken ct = default);
    /// <summary>公司侧：从配置的总部地址拉取并合入。full=true 做全量（忽略增量水位）——
    /// 增量以解析时间为水位，总部「撤回后恢复」这类可见性变化只有全量能对齐（FR-2.6 的全量同步）。</summary>
    Task<SyncResult> PullAsync(bool full = false, CancellationToken ct = default);
}

public record SyncPackage(
    string BatchNo,
    DateTimeOffset GeneratedAt,
    /// <summary>总部所用向量化模型版本标识（FR-2.2：一致时可直接复用向量）。</summary>
    string EmbeddingModelTag,
    IReadOnlyList<SyncDoc> Docs,
    /// <summary>总部已删除或撤回的文档（FR-2.6）：对端标记撤回并停止参与检索。</summary>
    IReadOnlyList<Guid> WithdrawnDocIds);

public record SyncDoc(
    Guid Id, string Title, string FileName, string ContentType, long FileSize,
    Classification Classification, int ParseVersion, DateTimeOffset UpdatedAt,
    SyncMeta? Metadata, IReadOnlyList<SyncChunk> Chunks);

public record SyncMeta(string CustomerName, int Year, string DeviceType, string DocCategory,
    string? ProjectNo, string? DocVersion);

public record SyncChunk(int Seq, string? SectionPath, int? PageNo, string? Bbox, string Text,
    /// <summary>仅当模型标识匹配时随包下发；否则为空、由对端本地重算。</summary>
    float[]? Embedding);

public record SyncResult(
    string BatchNo, int DocsUpserted, int ChunksWritten, int VectorsReused, int DocsQueuedForEmbedding,
    int Withdrawn, IReadOnlyList<string> Warnings);
