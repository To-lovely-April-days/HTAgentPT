namespace HT.Agent.Application.Abstractions;

/// <summary>公开库发布（FR-2.3/2.4）：发布=独立副本单独解析入库，原文档不变；
/// 副本确认前可编辑、不对外可见；撤回删除副本并记录原因，快照保留。</summary>
public interface IPublishService
{
    Task<PublishDraft> CreateDraftAsync(Guid sourceDocId, CancellationToken ct = default);
    /// <summary>确认发布：副本解析完成后方可确认，确认即对外可见。</summary>
    Task ConfirmAsync(Guid recordId, CancellationToken ct = default);
    /// <summary>撤回（FR-2.4）：删除公开库副本、立即停止对外可见，记录原因。</summary>
    Task WithdrawAsync(Guid recordId, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<PublishRow>> ListAsync(CancellationToken ct = default);
}

public record PublishDraft(Guid RecordId, Guid PublicDocId);

public record PublishRow(
    Guid RecordId, Guid SourceDocId, string SourceTitle, Guid PublicDocId, string? PublicDocStatus,
    string Status, string OperatorName, DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt, DateTimeOffset? WithdrawnAt, string? WithdrawReason);
