using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

public class UserAdminService(
    AppDbContext db,
    IPasswordHasher hasher,
    IAuditWriter audit,
    ICurrentUser me) : IUserAdminService
{
    public async Task<IReadOnlyList<UserRow>> ListAsync(bool includeInactive, CancellationToken ct = default)
    {
        return await db.Users.AsNoTracking()
            .Include(u => u.Role).Include(u => u.Company)
            .Where(u => includeInactive || u.IsActive)
            .OrderBy(u => u.Username)
            .Select(u => new UserRow(
                u.Id, u.Username, u.DisplayName, u.EmployeeNo, u.Department,
                u.Role == null ? null : u.Role.Code, u.Role == null ? null : u.Role.Name,
                u.Company == null ? "-" : u.Company.Name,
                u.Kind, u.CustomerNo, u.IsActive, u.LastLoginAt, u.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<Guid> CreateAsync(CreateUserRequest req, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(u => u.Username == req.Username, ct))
            throw new DomainRuleException("USERNAME_TAKEN", $"账号 {req.Username} 已存在");
        if (req.Kind == UserKind.Customer && string.IsNullOrWhiteSpace(req.CustomerNo))
            throw new DomainRuleException("CUSTOMER_NO_REQUIRED", "客户账号必须绑定客户编号（FR-7.4）");

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = req.Username,
            DisplayName = req.DisplayName,
            PasswordHash = hasher.Hash(req.InitialPassword),
            RoleId = req.RoleId,
            CompanyId = req.CompanyId,
            Kind = req.Kind,
            EmployeeNo = req.EmployeeNo,
            Department = req.Department,
            CustomerNo = req.CustomerNo,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await Log("user.create", user, new { req.Username, req.RoleId, req.Kind }, ct);
        return user.Id;
    }

    public async Task AssignRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default)
    {
        var user = await Get(userId, ct);
        var role = await db.Roles.FindAsync([roleId], ct)
            ?? throw new DomainRuleException("ROLE_NOT_FOUND", "角色不存在");
        var old = user.RoleId;
        user.RoleId = roleId;
        // 角色变化影响可访问密级，旧会话立即作废，重登后按新角色生效
        user.ActiveSessionId = null;
        await db.SaveChangesAsync(ct);
        await Log("user.assign_role", user, new { from = old, to = roleId, roleCode = role.Code }, ct);
    }

    public async Task DeactivateAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await Get(userId, ct);
        if (user.Id == me.UserId)
            throw new DomainRuleException("SELF_DEACTIVATE", "不能停用自己的账号");
        user.IsActive = false;
        user.DeactivatedAt = DateTimeOffset.UtcNow;
        // 停用后立即失去全部访问权（FR-7.1）：清会话，进行中的令牌下一次请求即 401
        user.ActiveSessionId = null;
        user.ActiveTerminalId = null;
        await db.SaveChangesAsync(ct);
        await Log("user.deactivate", user, null, ct);
    }

    public async Task<int> DeactivateBatchAsync(IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Contains(me.UserId))
            throw new DomainRuleException("SELF_DEACTIVATE", "批量停用名单里不能包含自己");
        var users = await db.Users.Where(u => userIds.Contains(u.Id) && u.IsActive).ToListAsync(ct);
        foreach (var user in users)
        {
            user.IsActive = false;
            user.DeactivatedAt = DateTimeOffset.UtcNow;
            user.ActiveSessionId = null;
            user.ActiveTerminalId = null;
        }
        await db.SaveChangesAsync(ct);
        foreach (var user in users)
            await Log("user.deactivate", user, new { batch = true }, ct);
        return users.Count;
    }

    public async Task ReactivateAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await Get(userId, ct);
        user.IsActive = true;
        user.DeactivatedAt = null;
        await db.SaveChangesAsync(ct);
        await Log("user.reactivate", user, null, ct);
    }

    public async Task ResetPasswordAsync(Guid userId, string newPassword, CancellationToken ct = default)
    {
        if (newPassword.Length < 8)
            throw new DomainRuleException("PASSWORD_TOO_SHORT", "口令至少 8 位");
        var user = await Get(userId, ct);
        user.PasswordHash = hasher.Hash(newPassword);
        // 重置留痕（决策 2：无自助改密，重置必须可追溯到经手人与时间）
        user.PasswordResetAt = DateTimeOffset.UtcNow;
        user.PasswordResetBy = me.UserId;
        user.ActiveSessionId = null; // 旧会话作废
        await db.SaveChangesAsync(ct);
        await Log("user.reset_password", user, null, ct);
    }

    /// <summary>导出历史操作记录（FR-7.5）。个人中心自助导出与此共用同一查询——两个入口一份数据（C3 设计）。</summary>
    public async Task<IReadOnlyList<AuditRow>> ExportUserAuditAsync(
        Guid userId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default)
    {
        var q = db.AuditLogs.AsNoTracking().Where(a => a.UserId == userId);
        if (from is not null) q = q.Where(a => a.At >= from);
        if (to is not null) q = q.Where(a => a.At < to);
        var rows = await q.OrderByDescending(a => a.At).Take(50_000)
            .Select(a => new AuditRow(a.Id, a.At, a.Username, a.Action, a.TargetType, a.TargetId,
                a.Detail, a.Result, a.Ip, a.TerminalId))
            .ToListAsync(ct);
        await audit.WriteAsync(new AuditEntry("user.export_audit", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "app_user", TargetId: userId.ToString(),
            Detail: new { rows = rows.Count, from, to }), ct);
        return rows;
    }

    public async Task BindTerminalAsync(Guid userId, string terminalId, string name, CancellationToken ct = default)
    {
        var user = await Get(userId, ct);
        var existing = await db.UserTerminals
            .FirstOrDefaultAsync(t => t.UserId == userId && t.TerminalId == terminalId, ct);
        if (existing is not null)
        {
            existing.IsActive = true;
            existing.Name = name;
        }
        else
        {
            db.UserTerminals.Add(new UserTerminal
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TerminalId = terminalId,
                Name = name,
                BoundAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync(ct);
        await Log("user.bind_terminal", user, new { terminalId, name }, ct);
    }

    public async Task UnbindTerminalAsync(Guid terminalBindingId, CancellationToken ct = default)
    {
        var binding = await db.UserTerminals.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.Id == terminalBindingId, ct)
            ?? throw new DomainRuleException("BINDING_NOT_FOUND", "绑定不存在");
        binding.IsActive = false;
        await db.SaveChangesAsync(ct);
        await Log("user.unbind_terminal", binding.User!, new { binding.TerminalId }, ct);
    }

    private async Task<AppUser> Get(Guid userId, CancellationToken ct)
        => await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
           ?? throw new DomainRuleException("USER_NOT_FOUND", "用户不存在");

    private Task Log(string action, AppUser target, object? detail, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "app_user", TargetId: target.Id.ToString(), Detail: detail), ct);
}

public class RoleService(AppDbContext db, IAuditWriter audit, ICurrentUser me) : IRoleService
{
    public async Task<IReadOnlyList<RoleRow>> ListAsync(CancellationToken ct = default)
        => await db.Roles.AsNoTracking().OrderBy(r => r.Code)
            .Select(r => new RoleRow(r.Id, r.Code, r.Name, r.Classifications, r.Permissions, r.IsSystem))
            .ToListAsync(ct);

    public async Task<Guid> CreateAsync(RoleEdit edit, CancellationToken ct = default)
    {
        if (await db.Roles.AnyAsync(r => r.Code == edit.Code, ct))
            throw new DomainRuleException("ROLE_CODE_TAKEN", $"角色代码 {edit.Code} 已存在");
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Code = edit.Code,
            Name = edit.Name,
            Classifications = edit.Classifications.ToList(),
            Permissions = edit.Permissions.ToList()
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        await Log("role.create", role, ct);
        return role.Id;
    }

    public async Task UpdateAsync(Guid roleId, RoleEdit edit, CancellationToken ct = default)
    {
        var role = await db.Roles.FindAsync([roleId], ct)
            ?? throw new DomainRuleException("ROLE_NOT_FOUND", "角色不存在");
        if (role.IsSystem && role.Code != edit.Code)
            throw new DomainRuleException("SYSTEM_ROLE_CODE", "内置角色代码不可改（密级与权限映射可改，FR-7.2）");
        role.Code = edit.Code;
        role.Name = edit.Name;
        role.Classifications = edit.Classifications.ToList();
        role.Permissions = edit.Permissions.ToList();
        await db.SaveChangesAsync(ct);
        await Log("role.update", role, ct);
    }

    private Task Log(string action, Role role, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "role", TargetId: role.Id.ToString(),
            Detail: new { role.Code, role.Classifications, role.Permissions }), ct);
}
