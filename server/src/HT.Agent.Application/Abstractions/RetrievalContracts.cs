using HT.Agent.Domain;

namespace HT.Agent.Application.Abstractions;

/// <summary>检索服务（M4 第 3 步）：混合检索与权限过滤在同一次数据库查询内完成（FR-4.3/4.4）。</summary>
public interface IRetrievalService
{
    Task<RetrievalResult> RetrieveAsync(RetrievalRequest req, CancellationToken ct = default);
}

public record RetrievalRequest(
    string Query,
    /// <summary>限定知识库范围；空为该用户可及的全部库。</summary>
    IReadOnlyList<Guid>? KbIds = null,
    /// <summary>元数据筛选（FR-4.5）：客户、年份、设备类型，与检索条件联合生效。</summary>
    string? CustomerName = null,
    int? Year = null,
    string? DeviceType = null,
    string? DocCategory = null);

public record RetrievalResult(
    bool AboveThreshold,
    IReadOnlyList<RetrievedChunk> Chunks,
    /// <summary>低于阈值时给出的可能相关文档名（FR-4.7）。</summary>
    IReadOnlyList<string> PossiblyRelatedDocs,
    double TopScore,
    string RewrittenQuery);

public record RetrievedChunk(
    long ChunkId, Guid DocId, string DocTitle, string? SectionPath, int? PageNo,
    string Text, double FusedScore, double RerankScore, Classification Classification);

/// <summary>问答服务（FR-4.8/4.9/4.12）。</summary>
public interface IQaService
{
    /// <summary>流式问答。事件流：meta（改写与命中）、delta（增量文本）、sources、done / no_result。</summary>
    IAsyncEnumerable<QaEvent> AskStreamAsync(QaRequest req, CancellationToken ct = default);
}

public record QaRequest(Guid? SessionId, string Question, RetrievalRequest Retrieval,
    /// <summary>用户手动纠正的意图（FR-4.1）。空则由路由器判定。</summary>
    string? ForcedIntent = null);

/// <summary>SSE 事件。Kind: meta | delta | sources | no_result | done | error。</summary>
public record QaEvent(string Kind, object Payload);
