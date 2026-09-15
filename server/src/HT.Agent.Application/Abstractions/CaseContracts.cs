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
    /// <summary>提交前敏感检测（FR-8.3）：客户词表、项目编号格式、电话邮箱格式。</summary>
    Task<IReadOnlyList<Logic.SensitiveScanner.Hit>> CheckSensitiveAsync(Guid caseId, CancellationToken ct = default);
    /// <summary>提交总部（FR-8.3）：检测有命中且未确认时拒绝；经同步通道上传，本地状态置 Pending。</summary>
    Task SubmitAsync(Guid caseId, bool acknowledged, CancellationToken ct = default);
    /// <summary>同型归并视图（FR-8.6）：同一设备型号折叠展示，避免同类内容占满结果。</summary>
    Task<IReadOnlyList<CaseModelGroup>> SearchGroupedAsync(CaseSearchRequest req, CancellationToken ct = default);
}

public record CaseModelGroup(string DeviceModel, int Count, IReadOnlyList<CaseRow> Top);

/// <summary>总部审核（FR-8.4/8.5）。只存在于总部节点的角色可用（表 3-1）。</summary>
public interface ICaseReviewService
{
    /// <summary>接收公司节点上传的案例（同步通道，节点令牌鉴权）。</summary>
    Task ReceiveAsync(SubmittedCase submitted, CancellationToken ct = default);
    /// <summary>公司节点回查其提交案例的审核结果。</summary>
    Task<IReadOnlyList<CaseStatusRow>> StatusOfAsync(IReadOnlyList<Guid> caseIds, CancellationToken ct = default);
    Task<IReadOnlyList<CaseRow>> PendingAsync(CancellationToken ct = default);
    Task<CaseDetail?> GetAsync(Guid caseId, CancellationToken ct = default);
    /// <summary>通过（可先修改）：渲染入共享库、标注来源公司与录入时间（FR-8.5），随同步下发。</summary>
    Task ApproveAsync(Guid caseId, CaseEdit? edited, CancellationToken ct = default);
    /// <summary>驳回：原因必填，回传至提交人（FR-8.4）。</summary>
    Task RejectAsync(Guid caseId, string reason, CancellationToken ct = default);
}

public record SubmittedCase(
    Guid CaseId, string CaseNo, string SourceCompany, string SubmitterName,
    string DeviceModel, string? AlarmCode, string Phenomenon, string CauseAnalysis,
    string Steps, string? SpareParts, string Result, string? Extra, DateTimeOffset CreatedAt);

public record CaseStatusRow(Guid CaseId, string SyncStatus, string? RejectReason);

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
