using HT.Agent.Domain;

namespace HT.Agent.Application.Abstractions;

/// <summary>口令散列。口令只能由管理员重置（决策 2），系统内不存在自助改密路径。</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

/// <summary>令牌签发。sid 声明承载互斥会话标识（决策 4）。</summary>
public interface IJwtIssuer
{
    string Issue(Guid userId, string username, string roleCode, Guid companyId, Guid sessionId, TimeSpan lifetime);
}

/// <summary>对象存储（表 8-2：S3 兼容；开发环境为本地文件实现，接口不变）。</summary>
public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, string keyHint, CancellationToken ct = default);
    Task<Stream> OpenAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
}

/// <summary>审计写入（FR-9.1）。所有服务经此落审计，只写不改。</summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);
}

public record AuditEntry(
    string Action,
    Domain.AuditResult Result,
    Guid? UserId = null,
    string Username = "-",
    Guid? CompanyId = null,
    string? TargetType = null,
    string? TargetId = null,
    object? Detail = null,
    string? Ip = null,
    string? TerminalId = null);

/// <summary>运行时配置（FR-9.6：界面可改，改后即时生效）。带内存缓存，写入即失效。</summary>
public interface IRuntimeConfig
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default);
    Task<double> GetDoubleAsync(string key, double fallback, CancellationToken ct = default);
    Task<string> GetStringAsync(string key, string fallback, CancellationToken ct = default);
    Task SetAsync(string key, string value, Guid? updatedBy, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default);
}

/// <summary>当前请求身份。中间件填充，服务读取。</summary>
public interface ICurrentUser
{
    Guid UserId { get; }
    string Username { get; }
    string RoleCode { get; }
    Guid CompanyId { get; }
    Guid SessionId { get; }
    /// <summary>可访问密级集合（表 3-1），登录时自角色映射解析。</summary>
    IReadOnlySet<Classification> Classifications { get; }
    IReadOnlySet<string> Permissions { get; }
    string? CustomerNo { get; }
    string? Ip { get; }
    string? TerminalId { get; }
    bool IsAuthenticated { get; }
}
