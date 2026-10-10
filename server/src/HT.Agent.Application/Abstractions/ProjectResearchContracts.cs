using HT.Agent.Domain;

namespace HT.Agent.Application.Abstractions;

/// <summary>把台账型自然语言问题拆成结构化查询和资料检索线索。</summary>
public interface IProjectResearchAgent
{
    Task<ProjectQueryPlan> PlanAsync(string question, string history, CancellationToken ct = default);
    Task<ProjectResearchResult> ExecuteAsync(ProjectQueryPlan plan, CancellationToken ct = default);
}

public interface IProjectQueryPlanner
{
    Task<ProjectQueryPlan> PlanAsync(string question, string history, CancellationToken ct = default);
}

public sealed record ProjectQueryPlan(
    ProjectSearchRequest? Query,
    string RetrievalQuery,
    IReadOnlyList<string> UnresolvedConditions,
    bool AllowRecent = false);

public sealed record ProjectResearchResult(
    ProjectResearchTable? Table,
    string Evidence,
    string Detail);

/// <summary>项目台账表格。地点/区域条件可由企业资料关联项目编号后回填，资料仍是地点事实的依据。</summary>
public sealed record ProjectResearchTable(
    ProjectResearchFilters Filters,
    IReadOnlyList<ProjectRow> Rows,
    bool AmountVisible,
    ProjectDetail? Detail,
    string Note,
    bool IsCandidate,
    bool Truncated);

public sealed record ProjectResearchFilters(
    string? Customer = null,
    string? DeviceType = null,
    int? YearFrom = null,
    int? YearTo = null,
    string? Keyword = null,
    string? LocationHint = null,
    DeliveryStatus? DeliveryStatus = null,
    decimal? AmountMin = null,
    decimal? AmountMax = null);
