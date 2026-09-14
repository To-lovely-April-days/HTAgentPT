using HT.Agent.Domain;

namespace HT.Agent.Application.Abstractions;

/// <summary>登录与会话（M7）。</summary>
public interface IAuthService
{
    Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task LogoutAsync(Guid userId, CancellationToken ct = default);
}

public record LoginRequest(string Username, string Password, string TerminalId, string? TerminalName, string? Ip);

/// <summary>登录结果。失败时 ReasonCode 供前端呈现对应异常态（C1/C4 系列），不复用泛泛的「登录失败」。</summary>
public record LoginResult(
    bool Ok,
    string? Token = null,
    string? ReasonCode = null,
    string? Message = null,
    UserProfile? Profile = null,
    /// <summary>本次登录是否把另一处在线会话顶了下线（决策 4：互斥且明确提示）。</summary>
    bool SupersededOther = false);

public static class LoginReasonCodes
{
    public const string BadCredentials = "BAD_CREDENTIALS";
    /// <summary>账号已停用（FR-7.1：停用后立即失去全部访问权）。</summary>
    public const string AccountDisabled = "ACCOUNT_DISABLED";
    /// <summary>账号已建但尚未指派角色，登录后无处可去。</summary>
    public const string NoRole = "NO_ROLE";
    /// <summary>终端未绑定（FR-9.7：账号与终端绑定）。</summary>
    public const string TerminalUnbound = "TERMINAL_UNBOUND";
}

/// <summary>会话被判无效时的原因码，随 401 返回（C4 系列异常态）。</summary>
public static class SessionEndCodes
{
    /// <summary>其他终端登录，本会话被顶下线（决策 4：明确提示，不是静默失效）。</summary>
    public const string Superseded = "SESSION_SUPERSEDED";
    public const string Expired = "SESSION_EXPIRED";
    public const string Disabled = "ACCOUNT_DISABLED";
}

public record UserProfile(
    Guid Id, string Username, string DisplayName, string RoleCode, string RoleName,
    Guid CompanyId, string CompanyName,
    IReadOnlyList<Classification> Classifications,
    IReadOnlyList<string> Permissions,
    string? CustomerNo);

/// <summary>用户管理（FR-7.1/7.5、决策 2）。全部动作入审计。</summary>
public interface IUserAdminService
{
    Task<IReadOnlyList<UserRow>> ListAsync(bool includeInactive, CancellationToken ct = default);
    Task<Guid> CreateAsync(CreateUserRequest req, CancellationToken ct = default);
    Task AssignRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default);
    /// <summary>停用后立即失去全部访问权：清 ActiveSessionId，进行中的令牌下一次请求即 401。</summary>
    Task DeactivateAsync(Guid userId, CancellationToken ct = default);
    Task ReactivateAsync(Guid userId, CancellationToken ct = default);
    /// <summary>管理员重置口令（决策 2：无自助改密）。重置入审计，记录经手人。</summary>
    Task ResetPasswordAsync(Guid userId, string newPassword, CancellationToken ct = default);
    /// <summary>导出该用户历史操作记录（FR-7.5 离职回收）。与个人中心自助导出字段完全一致。</summary>
    Task<IReadOnlyList<AuditRow>> ExportUserAuditAsync(Guid userId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default);
    Task BindTerminalAsync(Guid userId, string terminalId, string name, CancellationToken ct = default);
    Task UnbindTerminalAsync(Guid terminalBindingId, CancellationToken ct = default);
}

public record UserRow(
    Guid Id, string Username, string DisplayName, string? EmployeeNo, string? Department,
    string? RoleCode, string? RoleName, string CompanyName, UserKind Kind, string? CustomerNo,
    bool IsActive, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt);

public record CreateUserRequest(
    string Username, string DisplayName, string InitialPassword,
    Guid? RoleId, Guid CompanyId, UserKind Kind = UserKind.Employee,
    string? EmployeeNo = null, string? Department = null, string? CustomerNo = null);

public record AuditRow(
    long Id, DateTimeOffset At, string Username, string Action,
    string? TargetType, string? TargetId, string? Detail, AuditResult Result, string? Ip, string? TerminalId);

/// <summary>角色配置（FR-7.2：角色与可访问密级映射可配置，新增角色无需改代码）。</summary>
public interface IRoleService
{
    Task<IReadOnlyList<RoleRow>> ListAsync(CancellationToken ct = default);
    Task<Guid> CreateAsync(RoleEdit edit, CancellationToken ct = default);
    Task UpdateAsync(Guid roleId, RoleEdit edit, CancellationToken ct = default);
}

public record RoleRow(Guid Id, string Code, string Name, IReadOnlyList<Classification> Classifications,
    IReadOnlyList<string> Permissions, bool IsSystem);

public record RoleEdit(string Code, string Name, IReadOnlyList<Classification> Classifications,
    IReadOnlyList<string> Permissions);
