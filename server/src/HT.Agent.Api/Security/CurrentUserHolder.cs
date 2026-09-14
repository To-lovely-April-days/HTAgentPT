using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;

namespace HT.Agent.Api.Security;

/// <summary>请求作用域的当前身份。SessionGuard 中间件在校验通过后填充。</summary>
public class CurrentUserHolder : ICurrentUser
{
    private static readonly IReadOnlySet<Classification> EmptyCls = new HashSet<Classification>();
    private static readonly IReadOnlySet<string> EmptyPerms = new HashSet<string>();

    public Guid UserId { get; private set; }
    public string Username { get; private set; } = "-";
    public string RoleCode { get; private set; } = "-";
    public Guid CompanyId { get; private set; }
    public Guid SessionId { get; private set; }
    public IReadOnlySet<Classification> Classifications { get; private set; } = EmptyCls;
    public IReadOnlySet<string> Permissions { get; private set; } = EmptyPerms;
    public string? CustomerNo { get; private set; }
    public string? Ip { get; private set; }
    public string? TerminalId { get; private set; }
    public bool IsAuthenticated { get; private set; }

    public void Populate(Guid userId, string username, string roleCode, Guid companyId, Guid sessionId,
        IEnumerable<Classification> classifications, IEnumerable<string> permissions,
        string? customerNo, string? ip, string? terminalId)
    {
        UserId = userId;
        Username = username;
        RoleCode = roleCode;
        CompanyId = companyId;
        SessionId = sessionId;
        Classifications = classifications.ToHashSet();
        Permissions = permissions.ToHashSet();
        CustomerNo = customerNo;
        Ip = ip;
        TerminalId = terminalId;
        IsAuthenticated = true;
    }
}
