namespace HT.Agent.Domain.Entities;

/// <summary>公司。新公司复制是既定目标（10.4），公司信息一律不硬编码。</summary>
public class Company
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? ShortName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>角色。角色与可访问密级的映射可配置，新增角色无需改动代码（FR-7.2）。</summary>
public class Role
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    /// <summary>可访问密级集合（表 3-1）。</summary>
    public List<Classification> Classifications { get; set; } = [];
    /// <summary>功能权限键集合（表 3-2 功能权限矩阵，见 PermissionKeys）。</summary>
    public List<string> Permissions { get; set; } = [];
    /// <summary>系统内置角色不可删除（售前/售后/客户/管理员/总部审核人）。</summary>
    public bool IsSystem { get; set; }
    /// <summary>管理员问答默认关闭，确需开通时单独授权并记录（3.2/3.4，歧义 2 的既定处理）。</summary>
    public string? Note { get; set; }
}

/// <summary>用户（表 7-1 app_user）。绑定角色与所属公司；客户账号额外绑定客户编号。</summary>
public class AppUser
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string DisplayName { get; set; }
    public string? EmployeeNo { get; set; }
    public string? Department { get; set; }
    public required string PasswordHash { get; set; }
    public UserKind Kind { get; set; } = UserKind.Employee;
    /// <summary>客户账号绑定的客户编号，仅可见本人名下项目（FR-7.4）。</summary>
    public string? CustomerNo { get; set; }
    public Guid? RoleId { get; set; }
    public Role? Role { get; set; }
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    /// <summary>停用后立即失去全部访问权（FR-7.1）。</summary>
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? DeactivatedAt { get; set; }
    /// <summary>同账号多终端互斥（决策 4）：仅此会话标识有效，新登录顶掉旧会话。</summary>
    public Guid? ActiveSessionId { get; set; }
    public string? ActiveTerminalId { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    /// <summary>不做自助改密（决策 2）：口令只能由管理员重置，重置本身入审计。</summary>
    public DateTimeOffset? PasswordResetAt { get; set; }
    public Guid? PasswordResetBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>远程接入终端绑定（FR-9.7）：账号一人一号并与终端绑定，接入记录纳入审计。</summary>
public class UserTerminal
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public required string TerminalId { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset BoundAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? LastIp { get; set; }
}
