using HT.Agent.Domain;

namespace HT.Agent.Application.Abstractions;

/// <summary>项目台账（FR-3.3/3.4）：结构化查询，结果以表格返回，不经模型生成。</summary>
public interface IProjectService
{
    /// <summary>组合筛选：客户、年份、设备类型、金额区间。金额字段按角色裁剪（表 4-6：
    /// 合同金额属机密密级）——无机密权限的调用者拿到的行里没有金额，金额筛选条件也被忽略并如实标记。</summary>
    Task<ProjectSearchResult> SearchAsync(ProjectSearchRequest req, CancellationToken ct = default);
    Task<ProjectDetail?> GetAsync(string projectNo, CancellationToken ct = default);
    Task CreateAsync(ProjectEdit edit, CancellationToken ct = default);
    Task UpdateAsync(string projectNo, ProjectEdit edit, CancellationToken ct = default);
}

public record ProjectSearchRequest(
    string? CustomerName = null,
    int? YearFrom = null,
    int? YearTo = null,
    string? DeviceType = null,
    decimal? AmountMin = null,
    decimal? AmountMax = null,
    DeliveryStatus? DeliveryStatus = null,
    string? Keyword = null,
    int Limit = 200);

public record ProjectSearchResult(
    IReadOnlyList<ProjectRow> Rows,
    /// <summary>金额列是否在本次结果中（由角色可访问密级决定，不由前端决定）。</summary>
    bool AmountVisible,
    /// <summary>调用者带了金额筛选但无金额可见权限时为 true：条件被忽略，不参与过滤。</summary>
    bool AmountFilterIgnored);

public record ProjectRow(
    string ProjectNo, string CustomerName, int Year, string DeviceType, string? DeviceModel,
    string? SpecParams, decimal? ContractAmount, DeliveryStatus? DeliveryStatus,
    Guid? OwnerId, DateTimeOffset UpdatedAt);

public record ProjectDetail(
    ProjectRow Row,
    /// <summary>台账与文档通过项目编号关联（FR-3.3）：该项目下的资料清单，按调用者密级过滤。</summary>
    IReadOnlyList<ProjectDocRow> Documents);

public record ProjectDocRow(Guid DocId, string Title, string DocCategory, Classification Classification,
    ParseStatus ParseStatus, DateTimeOffset UploadedAt);

public record ProjectEdit(
    string ProjectNo, string CustomerName, int Year, string DeviceType, string? DeviceModel,
    string? SpecParams, decimal? ContractAmount, DeliveryStatus? DeliveryStatus, Guid? OwnerId, string? DocPath);
