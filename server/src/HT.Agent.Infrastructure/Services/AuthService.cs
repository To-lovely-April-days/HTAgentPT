using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

public class AuthService(
    AppDbContext db,
    IPasswordHasher hasher,
    IJwtIssuer jwt,
    IRuntimeConfig config,
    IAuditWriter audit) : IAuthService
{
    public async Task<LoginResult> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        var user = await db.Users.Include(u => u.Role).Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Username == req.Username, ct);

        if (user is null || !hasher.Verify(req.Password, user.PasswordHash))
        {
            await audit.WriteAsync(new AuditEntry("auth.login", AuditResult.Denied,
                UserId: user?.Id, Username: req.Username,
                Detail: new { reason = LoginReasonCodes.BadCredentials }, Ip: req.Ip, TerminalId: req.TerminalId), ct);
            return new LoginResult(false, ReasonCode: LoginReasonCodes.BadCredentials, Message: "账号或口令不正确");
        }
        if (!user.IsActive)
        {
            await Deny(user, req, LoginReasonCodes.AccountDisabled, ct);
            return new LoginResult(false, ReasonCode: LoginReasonCodes.AccountDisabled,
                Message: "账号已停用。如需恢复请联系系统管理员。");
        }
        if (user.Role is null)
        {
            await Deny(user, req, LoginReasonCodes.NoRole, ct);
            return new LoginResult(false, ReasonCode: LoginReasonCodes.NoRole,
                Message: "账号尚未指派角色，暂无法使用。请联系系统管理员指派。");
        }

        // 终端绑定（FR-9.7）：绑定过终端的员工账号只允许从绑定终端接入；客户账号走公网入口不绑终端。
        if (user.Kind == UserKind.Employee)
        {
            var bound = await db.UserTerminals
                .Where(t => t.UserId == user.Id && t.IsActive)
                .ToListAsync(ct);
            if (bound.Count > 0)
            {
                var hit = bound.FirstOrDefault(t => t.TerminalId == req.TerminalId);
                if (hit is null)
                {
                    await Deny(user, req, LoginReasonCodes.TerminalUnbound, ct);
                    return new LoginResult(false, ReasonCode: LoginReasonCodes.TerminalUnbound,
                        Message: "该终端未绑定到你的账号。要绑新终端请联系系统管理员。");
                }
                hit.LastSeenAt = DateTimeOffset.UtcNow;
                hit.LastIp = req.Ip;
            }
        }

        // 同账号多终端互斥（决策 4）：新登录顶掉旧会话；旧会话下一次请求收到 SESSION_SUPERSEDED。
        var superseded = user.ActiveSessionId is not null && user.ActiveTerminalId != req.TerminalId;
        var sessionId = Guid.NewGuid();
        // 条件更新防丢失写：管理员在「读用户→发令牌」窗口内做了停用或重置口令时，
        // 谓词失配写入 0 行，登录按凭据失效处理——不能让旧口令的登录把刚失效的会话复活。
        var written = await db.Users
            .Where(u => u.Id == user.Id && u.IsActive && u.PasswordHash == user.PasswordHash)
            .ExecuteUpdateAsync(x => x
                .SetProperty(u => u.ActiveSessionId, sessionId)
                .SetProperty(u => u.ActiveTerminalId, req.TerminalId)
                .SetProperty(u => u.LastLoginAt, DateTimeOffset.UtcNow), ct);
        if (written == 0)
        {
            await Deny(user, req, LoginReasonCodes.BadCredentials, ct);
            return new LoginResult(false, ReasonCode: LoginReasonCodes.BadCredentials,
                Message: "账号状态刚发生变化，请重试或联系管理员");
        }
        await db.SaveChangesAsync(ct); // 终端 LastSeen 等其余跟踪变更

        var lifetime = TimeSpan.FromMinutes(await config.GetIntAsync(ConfigKeys.JwtLifetimeMinutes, 480, ct));
        var token = jwt.Issue(user.Id, user.Username, user.Role.Code, user.CompanyId, sessionId, lifetime);

        await audit.WriteAsync(new AuditEntry("auth.login", AuditResult.Success,
            UserId: user.Id, Username: user.Username, CompanyId: user.CompanyId,
            Detail: new { supersededOther = superseded }, Ip: req.Ip, TerminalId: req.TerminalId), ct);

        var profile = new UserProfile(
            user.Id, user.Username, user.DisplayName, user.Role.Code, user.Role.Name,
            user.CompanyId, user.Company?.Name ?? "-",
            user.Role.Classifications, user.Role.Permissions, user.CustomerNo);
        return new LoginResult(true, Token: token, Profile: profile, SupersededOther: superseded);
    }

    public async Task LogoutAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return;
        user.ActiveSessionId = null;
        user.ActiveTerminalId = null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("auth.logout", AuditResult.Success,
            UserId: user.Id, Username: user.Username, CompanyId: user.CompanyId), ct);
    }

    private Task Deny(Domain.Entities.AppUser user, LoginRequest req, string reason, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry("auth.login", AuditResult.Denied,
            UserId: user.Id, Username: user.Username, CompanyId: user.CompanyId,
            Detail: new { reason }, Ip: req.Ip, TerminalId: req.TerminalId), ct);
}
