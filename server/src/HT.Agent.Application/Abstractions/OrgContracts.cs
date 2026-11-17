using HT.Agent.Domain.Entities;

namespace HT.Agent.Application.Abstractions;

/// <summary>组织架构（部门树）与按部门的审批。</summary>
public interface IOrgService
{
    // ── 部门树（管理员）──
    Task<IReadOnlyList<DeptRow>> TreeAsync(CancellationToken ct = default);
    Task<DeptRow> CreateAsync(string name, Guid? parentId, CancellationToken ct = default);
    Task UpdateAsync(Guid id, string? name, Guid? parentId, bool moveParent, CancellationToken ct = default);
    Task SetHeadAsync(Guid id, Guid? headUserId, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task SetUserDepartmentAsync(Guid userId, Guid? departmentId, CancellationToken ct = default);

    // ── 审批范围 ──
    /// <summary>当前用户能处理哪些人提交的事项。All=true 为系统管理员（全部）。</summary>
    Task<ApprovalScope> ScopeAsync(CancellationToken ct = default);
    /// <summary>某人是不是部门主管（主管自己上传的直接通过）。</summary>
    Task<bool> IsHeadAsync(Guid userId, CancellationToken ct = default);
    /// <summary>某人提交的事项交给谁：主管名；null 表示交系统管理员。本人是主管时 AutoApprove=true。</summary>
    Task<ApproverInfo> ApproverForAsync(Guid userId, CancellationToken ct = default);

    // ── 新增客户 ──
    Task<CustomerRequestRow> RequestCustomerAsync(string name, string? reason, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerRequestRow>> PendingCustomerRequestsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CustomerRequestRow>> MyCustomerRequestsAsync(CancellationToken ct = default);
    Task ApproveCustomerAsync(Guid id, string? finalName, CancellationToken ct = default);
    Task RejectCustomerAsync(Guid id, string reason, CancellationToken ct = default);
    /// <summary>主管/管理员直接往客户名单里加。</summary>
    Task AddCustomerAsync(string name, CancellationToken ct = default);
}

public record DeptRow(Guid Id, string Name, Guid? ParentId, Guid? HeadUserId, string? HeadName, int SortOrder, int MemberCount);

public record ApprovalScope(bool All, IReadOnlySet<Guid> UserIds, bool IsHead)
{
    public bool Covers(Guid userId) => All || UserIds.Contains(userId);
    public bool IsApprover => All || IsHead;
}

public record ApproverInfo(bool AutoApprove, string? ApproverName);

public record CustomerRequestRow(Guid Id, string Name, string? Reason, Guid RequestedById, string RequesterName, string? RequesterDept,
    DateTimeOffset RequestedAt, CustomerRequestStatus Status, string? FinalName, string? ReviewNote, DateTimeOffset? ReviewedAt,
    IReadOnlyList<string> Similar);
