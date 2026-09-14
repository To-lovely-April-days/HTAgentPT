using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HT.Agent.Infrastructure.Services;

/// <summary>审计写入（FR-9.1：只写不改）。用独立作用域落库，业务事务失败不吞掉审计，审计失败不拖垮业务。</summary>
public class AuditWriter(IServiceScopeFactory scopeFactory) : IAuditWriter
{
    public async Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AuditLogs.Add(new AuditLog
        {
            At = DateTimeOffset.UtcNow,
            UserId = entry.UserId,
            Username = entry.Username,
            CompanyId = entry.CompanyId,
            Action = entry.Action,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            Detail = entry.Detail is null ? null : JsonSerializer.Serialize(entry.Detail),
            Result = entry.Result,
            Ip = entry.Ip,
            TerminalId = entry.TerminalId
        });
        await db.SaveChangesAsync(ct);
    }
}
