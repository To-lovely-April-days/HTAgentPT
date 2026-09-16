using HT.Agent.Domain;

namespace HT.Agent.Application.Abstractions;

/// <summary>知识库管理（M2）。</summary>
public interface IKnowledgeBaseService
{
    Task<IReadOnlyList<KbRow>> ListAsync(CancellationToken ct = default);
    Task<Guid> CreateAsync(string name, KnowledgeBaseTier tier, ChunkStrategy defaultStrategy, string? description, CancellationToken ct = default);
    /// <summary>tier 创建后不可修改（FR-2.1）；本方法只改名称、描述与默认切分策略。</summary>
    Task UpdateAsync(Guid id, string name, ChunkStrategy defaultStrategy, string? description, CancellationToken ct = default);
}

/// <summary>库级统计（FR-2.5）：文档数、分块数、最近更新。</summary>
public record KbRow(Guid Id, string Name, KnowledgeBaseTier Tier, ChunkStrategy DefaultChunkStrategy,
    string? Description, bool IsActive, int DocCount, long ChunkCount, DateTimeOffset? LastUpdatedAt);

/// <summary>词表维护（FR-3.2）。</summary>
public interface IVocabService
{
    Task<IReadOnlyList<VocabRow>> ListAsync(string vocabKey, CancellationToken ct = default);
    Task<Guid> AddAsync(string vocabKey, string value, string? aliases, CancellationToken ct = default);
    Task DisableAsync(Guid id, CancellationToken ct = default);
    Task<bool> IsValidAsync(string vocabKey, string value, CancellationToken ct = default);
}

public record VocabRow(Guid Id, string VocabKey, string Value, string? Aliases, bool IsActive, int SortOrder);

/// <summary>语料管理（M1）。</summary>
public interface IDocumentService
{
    /// <summary>上传：必须选择知识库与密级，未选不允许提交（FR-1.1）；元数据必填校验（FR-3.1）；
    /// 受控字段查词表（FR-3.2）。上传后不自动解析（FR-1.2）。</summary>
    Task<UploadResult> UploadAsync(UploadDocumentRequest req, Stream content, CancellationToken ct = default);
    Task<IReadOnlyList<DocRow>> ListAsync(Guid? kbId, ParseStatus? status, string? search, CancellationToken ct = default);
    /// <summary>批量提交解析，进入队列（FR-1.2）。</summary>
    Task<int> QueueParseAsync(IReadOnlyList<Guid> docIds, CancellationToken ct = default);
    /// <summary>更换切分策略后重新解析：原分块作废重建，期间不参与检索（FR-1.6）。</summary>
    Task ReparseAsync(Guid docId, ChunkStrategy? newStrategy, CancellationToken ct = default);
    Task<IReadOnlyList<ChunkRow>> GetChunksAsync(Guid docId, CancellationToken ct = default);
    /// <summary>编辑单块：仅重算该块向量，不触发整份重解析（FR-1.5）。</summary>
    Task EditChunkAsync(long chunkId, string newText, CancellationToken ct = default);
    Task DeleteChunkAsync(long chunkId, CancellationToken ct = default);
    /// <summary>合并相邻分块（FR-1.5）：同文档、seq 连续；合并块重算向量。</summary>
    Task<long> MergeChunksAsync(IReadOnlyList<long> chunkIds, CancellationToken ct = default);
    /// <summary>按字符偏移拆分单块（FR-1.5）：新块逐块重算向量，后续块 seq 顺延。</summary>
    Task<IReadOnlyList<long>> SplitChunkAsync(long chunkId, IReadOnlyList<int> offsets, CancellationToken ct = default);
    /// <summary>按条件批量修正元数据（FR-3.6），受控字段仍查词表；返回受影响文档数。</summary>
    Task<int> BatchUpdateMetadataAsync(MetadataBatchFilter filter, MetadataBatchSet set, CancellationToken ct = default);
    Task<(Stream Content, string FileName, string ContentType)> DownloadAsync(Guid docId, CancellationToken ct = default);
    /// <summary>文档解析抽取的图片清单（FR-4.9）：与下载原件同一套密级/归属/数据范围校验。</summary>
    Task<IReadOnlyList<DocImageRow>> ListImagesAsync(Guid docId, CancellationToken ct = default);
    /// <summary>取单张图片内容（同上校验）。</summary>
    Task<(Stream Content, string ContentType)> OpenImageAsync(Guid docId, long imageId, CancellationToken ct = default);
}

public record DocImageRow(long Id, string? Caption, int? PageNo, string? Bbox, int Seq);

/// <summary>批量修正的筛选条件（全部可选，但至少给一个，避免误伤全库）。</summary>
public record MetadataBatchFilter(Guid? KbId = null, string? CustomerName = null,
    string? DocCategory = null, int? Year = null, string? DeviceType = null);

/// <summary>批量修正要写入的值（只写非空项）。</summary>
public record MetadataBatchSet(string? CustomerName = null, string? DeviceType = null,
    string? DocCategory = null, int? Year = null, string? ProjectNo = null);

public record UploadDocumentRequest(
    Guid KbId, string FileName, string ContentType, long FileSize,
    Classification Classification, ChunkStrategy? ChunkStrategyOverride,
    // 表 4-5 元数据
    string CustomerName, int Year, string DeviceType, string DocCategory,
    string? ProjectNo, Guid? OwnerId, string? DocVersion, DateOnly? EffectiveDate,
    string? Title);

public record UploadResult(Guid DocId, string FileKey);

public record DocRow(Guid Id, string Title, string FileName, Guid KbId, string KbName,
    Classification Classification, ParseStatus ParseStatus, string? ParseError,
    long FileSize, DateTimeOffset UploadedAt, string? DocCategory, string? CustomerName, string? ProjectNo);

public record ChunkRow(long Id, int Seq, string? SectionPath, int? PageNo, string? Bbox, string Text,
    string? EmbeddingModel, bool IsActive);

/// <summary>词表校验失败等业务规则错误，接口层映射为 422。</summary>
public class DomainRuleException(string code, string message) : Exception(message)
{
    public string Code => code;
}

/// <summary>越权访问，接口层映射为 403 并已由抛出方写入审计（FR-7.3）。</summary>
public class ForbiddenException(string code, string message) : Exception(message)
{
    public string Code => code;
}
