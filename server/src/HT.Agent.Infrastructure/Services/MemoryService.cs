using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>长期记忆。所有读写都按当前用户过滤——别人的记忆按「不存在」处理。审计只记动作，不记内容。</summary>
public class MemoryService(AppDbContext db, IRuntimeConfig config, IAuditWriter audit, ICurrentUser me) : IMemoryService
{
    private async Task<bool> GlobalOnAsync(CancellationToken ct)
        => (await config.GetStringAsync(ConfigKeys.MemoryEnabled, "on", ct)).Trim().ToLowerInvariant() is "on" or "true";

    private async Task<int> MaxAsync(CancellationToken ct) => Math.Clamp(await config.GetIntAsync(ConfigKeys.MemoryMax, 50, ct), 5, 500);

    private Task Log(string action, Guid? id, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success, UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "memory", TargetId: id?.ToString()), ct);

    public async Task<MemoryState> MineAsync(CancellationToken ct = default)
    {
        var off = await db.Users.AsNoTracking().Where(u => u.Id == me.UserId).Select(u => u.MemoryOff).FirstOrDefaultAsync(ct);
        var items = await db.UserMemories.AsNoTracking().Where(m => m.UserId == me.UserId).OrderBy(m => m.CreatedAt)
            .Select(m => new MemoryRow(m.Id, m.Kind, m.Text, m.Source, m.CreatedAt, m.UpdatedAt)).ToListAsync(ct);
        return new MemoryState(await GlobalOnAsync(ct), !off, await MaxAsync(ct), items);
    }

    public async Task<MemoryRow> AddAsync(string text, string kind, string source, CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        if (MemoryGuard.Reject(text) is { } why) throw new DomainRuleException("MEMORY_REJECTED", why);
        var count = await db.UserMemories.CountAsync(m => m.UserId == me.UserId, ct);
        if (count >= await MaxAsync(ct)) throw new DomainRuleException("MEMORY_FULL", "记忆已经满了，先删掉几条不用的");
        // 一模一样的已经记过就不重复记
        var same = await db.UserMemories.FirstOrDefaultAsync(m => m.UserId == me.UserId && m.Text == text, ct);
        if (same is not null) return new MemoryRow(same.Id, same.Kind, same.Text, same.Source, same.CreatedAt, same.UpdatedAt);
        var now = DateTimeOffset.UtcNow;
        var m = new UserMemory { Id = Guid.NewGuid(), UserId = me.UserId, Kind = MemoryGuard.NormalizeKind(kind), Text = text,
            Source = source == "manual" ? "manual" : "chat", CreatedAt = now, UpdatedAt = now };
        db.UserMemories.Add(m);
        await db.SaveChangesAsync(ct);
        await Log("memory.add", m.Id, ct);
        return new MemoryRow(m.Id, m.Kind, m.Text, m.Source, m.CreatedAt, m.UpdatedAt);
    }

    public async Task UpdateAsync(Guid id, string text, string kind, CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        if (MemoryGuard.Reject(text) is { } why) throw new DomainRuleException("MEMORY_REJECTED", why);
        var m = await db.UserMemories.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.UserId, ct)
                ?? throw new DomainRuleException("MEMORY_NOT_FOUND", "这条记忆不存在");
        m.Text = text;
        m.Kind = MemoryGuard.NormalizeKind(kind);
        m.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await Log("memory.update", id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var m = await db.UserMemories.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.UserId, ct);
        if (m is null) return;
        db.UserMemories.Remove(m);
        await db.SaveChangesAsync(ct);
        await Log("memory.delete", id, ct);
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var u = await db.Users.FirstAsync(x => x.Id == me.UserId, ct);
        u.MemoryOff = !enabled;
        await db.SaveChangesAsync(ct);
        await Log(enabled ? "memory.enable" : "memory.disable", null, ct);
    }

    public async Task<bool> ActiveAsync(CancellationToken ct = default)
        => await GlobalOnAsync(ct) && !await db.Users.AsNoTracking().Where(u => u.Id == me.UserId).Select(u => u.MemoryOff).FirstOrDefaultAsync(ct);

    public async Task<(int Users, int Total)> StatsAsync(CancellationToken ct = default)
    {
        var companyUsers = db.Users.Where(u => u.CompanyId == me.CompanyId).Select(u => u.Id);
        var q = db.UserMemories.AsNoTracking().Where(m => companyUsers.Contains(m.UserId));
        return (await q.Select(m => m.UserId).Distinct().CountAsync(ct), await q.CountAsync(ct));
    }
}
