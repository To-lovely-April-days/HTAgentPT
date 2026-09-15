using HT.Agent.Domain;

namespace HT.Agent.Application.Abstractions;

/// <summary>故障案例（M8 本地闭环）。录入后本公司立即可检索，不依赖审核（FR-8.2）。</summary>
public interface IFaultCaseService
{
    Task<Guid> CreateAsync(CaseEdit edit, CancellationToken ct = default);
    /// <summary>修改时同步更新对应分块（FR-8.2 末句）。</summary>
    Task UpdateAsync(Guid caseId, CaseEdit edit, CancellationToken ct = default);
    /// <summary>按设备型号、报警代码、故障现象关键词检索，按相关度与时间排序（FR-8.7）。</summary>
    Task<IReadOnlyList<CaseRow>> SearchAsync(CaseSearchRequest req, CancellationToken ct = default);
    Task<CaseDetail?> GetAsync(Guid caseId, CancellationToken ct = default);
}

/// <summary>录入表单（表 4-1 FR-8.1 的七个结构化字段 + 可配置扩展字段）。</summary>
public record CaseEdit(
    string DeviceModel,
    string Phenomenon,
    string? AlarmCode,
    string CauseAnalysis,
    string Steps,
    string? SpareParts,
    string Result,
    /// <summary>扩展字段（FR-8.1：表单字段可配置），键值对原样存 jsonb 并参与渲染。</summary>
    Dictionary<string, string>? Extra = null);

public record CaseSearchRequest(
    string? DeviceModel = null,
    string? AlarmCode = null,
    string? Keyword = null,
    CaseSyncStatus? SyncStatus = null,
    int Limit = 100);

public record CaseRow(
    Guid Id, string CaseNo, string DeviceModel, string? AlarmCode, string Phenomenon,
    string Result, CaseSyncStatus SyncStatus, string? SourceCompany,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record CaseDetail(
    Guid Id, string CaseNo, string DeviceModel, string? AlarmCode, string Phenomenon,
    string CauseAnalysis, string Steps, string? SpareParts, string Result,
    Dictionary<string, string> Extra, CaseSyncStatus SyncStatus, string? RejectReason,
    string? SourceCompany, Guid CreatedById, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    long? ChunkId);
