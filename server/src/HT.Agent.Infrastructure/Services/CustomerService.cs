using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>客户入口与工单（FR-7.4、FR-8.8）。客户的数据范围是 CustomerNo 行级过滤——
/// 与台账同一条原则：范围收敛在查询里，不靠前端少画一个入口。</summary>
public class CustomerService(
    AppDbContext db,
    IRuntimeConfig config,
    IAuditWriter audit,
    ICurrentUser me) : ICustomerService
{
    public async Task<Guid> RegisterDeviceAsync(DeviceEdit edit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(edit.CustomerNo) || string.IsNullOrWhiteSpace(edit.DeviceNo))
            throw new DomainRuleException("DEVICE_FIELDS", "客户编号与设备编号必填");
        if (!string.IsNullOrWhiteSpace(edit.ProjectNo) &&
            !await db.Projects.AnyAsync(p => p.ProjectNo == edit.ProjectNo, ct))
            throw new DomainRuleException("PROJECT_NOT_FOUND", $"项目编号 {edit.ProjectNo} 不在台账中");
        if (await db.CustomerDevices.AnyAsync(d => d.CustomerNo == edit.CustomerNo && d.DeviceNo == edit.DeviceNo, ct))
            throw new DomainRuleException("DEVICE_DUP", "该客户名下已有同号设备");
        var device = new CustomerDevice
        {
            Id = Guid.NewGuid(),
            CustomerNo = edit.CustomerNo.Trim(),
            DeviceNo = edit.DeviceNo.Trim(),
            Model = edit.Model.Trim(),
            ProjectNo = string.IsNullOrWhiteSpace(edit.ProjectNo) ? null : edit.ProjectNo,
            DeliveredAt = edit.DeliveredAt
        };
        db.CustomerDevices.Add(device);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("device.register", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "customer_device", TargetId: device.Id.ToString(),
            Detail: new { edit.CustomerNo, edit.DeviceNo, edit.Model }), ct);
        return device.Id;
    }

    public Task<IReadOnlyList<DeviceRow>> ListDevicesAsync(string? customerNo, CancellationToken ct = default)
        => QueryDevices(string.IsNullOrWhiteSpace(customerNo) ? null : customerNo, ct);

    public Task<IReadOnlyList<DeviceRow>> MyDevicesAsync(CancellationToken ct = default)
        => string.IsNullOrEmpty(me.CustomerNo)
            ? throw new DomainRuleException("NOT_CUSTOMER", "当前账号未绑定客户编号")
            : QueryDevices(me.CustomerNo, ct);

    private async Task<IReadOnlyList<DeviceRow>> QueryDevices(string? customerNo, CancellationToken ct)
    {
        var q = db.CustomerDevices.AsNoTracking().AsQueryable();
        if (customerNo is not null) q = q.Where(d => d.CustomerNo == customerNo);
        return await q.OrderBy(d => d.CustomerNo).ThenBy(d => d.DeviceNo).Take(500)
            .Select(d => new DeviceRow(d.Id, d.CustomerNo, d.DeviceNo, d.Model, d.ProjectNo, d.DeliveredAt,
                d.ProjectNo == null
                    ? null
                    : db.Projects.Where(p => p.ProjectNo == d.ProjectNo && p.DeliveryStatus != null)
                        .Select(p => p.DeliveryStatus!.ToString()).FirstOrDefault()))
            .ToListAsync(ct);
    }

    public async Task<TicketRow> CreateTicketAsync(string deviceNo, string description, string contact, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(me.CustomerNo))
            throw new DomainRuleException("NOT_CUSTOMER", "当前账号未绑定客户编号");
        if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(contact))
            throw new DomainRuleException("TICKET_FIELDS", "现象描述与联系方式必填（FR-8.8）");
        // 报修的设备必须在本人名下——报修入口不能成为探测他人设备号的口子
        var owns = await db.CustomerDevices.AnyAsync(
            d => d.CustomerNo == me.CustomerNo && d.DeviceNo == deviceNo, ct);
        if (!owns)
            throw new DomainRuleException("DEVICE_NOT_YOURS", "该设备不在你的名下。设备编号见铭牌，如有疑问请联系售后。");

        var prefix = await config.GetStringAsync(ConfigKeys.TicketNoPrefix, "RT", ct);
        var year = DateTimeOffset.UtcNow.Year;
        for (var attempt = 0; ; attempt++)
        {
            var seq = await db.Tickets.CountAsync(t => t.TicketNo.StartsWith($"{prefix}-{year}-"), ct) + 1 + attempt;
            var ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                TicketNo = $"{prefix}-{year}-{seq:D4}",
                CustomerNo = me.CustomerNo!,
                DeviceNo = deviceNo.Trim(),
                Description = description.Trim(),
                Contact = contact.Trim(),
                Status = TicketStatus.Submitted,
                Updates = JsonSerializer.Serialize(new List<TicketTrailEntry>
                {
                    new(DateTimeOffset.UtcNow, me.Username, TicketStatus.Submitted.ToString(), "客户提交报修")
                }),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Tickets.Add(ticket);
            try
            {
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync(new AuditEntry("ticket.create", AuditResult.Success,
                    UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                    TargetType: "ticket", TargetId: ticket.Id.ToString(),
                    Detail: new { ticket.TicketNo, deviceNo }), ct);
                return await ToRowAsync(ticket, ct);
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<IReadOnlyList<TicketRow>> MyTicketsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(me.CustomerNo))
            throw new DomainRuleException("NOT_CUSTOMER", "当前账号未绑定客户编号");
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.CustomerNo == me.CustomerNo)
            .OrderByDescending(t => t.UpdatedAt).Take(100).ToListAsync(ct);
        var rows = new List<TicketRow>();
        foreach (var t in tickets) rows.Add(await ToRowAsync(t, ct));
        return rows;
    }

    public async Task<IReadOnlyList<TicketRow>> ListTicketsAsync(TicketStatus? status, CancellationToken ct = default)
    {
        var q = db.Tickets.AsNoTracking().AsQueryable();
        if (status is not null) q = q.Where(t => t.Status == status);
        var tickets = await q.OrderByDescending(t => t.UpdatedAt).Take(300).ToListAsync(ct);
        var rows = new List<TicketRow>();
        foreach (var t in tickets) rows.Add(await ToRowAsync(t, ct));
        return rows;
    }

    public async Task AssignTicketAsync(Guid ticketId, Guid assigneeId, CancellationToken ct = default)
    {
        var ticket = await GetTicket(ticketId, ct);
        var assignee = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == assigneeId && u.IsActive, ct)
            ?? throw new DomainRuleException("ASSIGNEE_NOT_FOUND", "被指派人不存在或已停用");
        ticket.AssigneeId = assigneeId;
        if (ticket.Status == TicketStatus.Submitted) ticket.Status = TicketStatus.Assigned;
        AppendTrail(ticket, ticket.Status, $"指派给 {assignee.DisplayName}");
        await db.SaveChangesAsync(ct);
        await LogTicket("ticket.assign", ticket, new { assigneeId, assignee.DisplayName }, ct);
    }

    public async Task UpdateTicketStatusAsync(Guid ticketId, TicketStatus status, string? note, CancellationToken ct = default)
    {
        var ticket = await GetTicket(ticketId, ct);
        ticket.Status = status;
        AppendTrail(ticket, status, note);
        await db.SaveChangesAsync(ct);
        await LogTicket("ticket.status", ticket, new { status = status.ToString(), note }, ct);
    }

    private void AppendTrail(Ticket ticket, TicketStatus status, string? note)
    {
        var trail = ticket.Updates is null
            ? []
            : JsonSerializer.Deserialize<List<TicketTrailEntry>>(ticket.Updates) ?? new List<TicketTrailEntry>();
        trail.Add(new TicketTrailEntry(DateTimeOffset.UtcNow, me.Username, status.ToString(), note));
        ticket.Updates = JsonSerializer.Serialize(trail);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task<TicketRow> ToRowAsync(Ticket t, CancellationToken ct)
    {
        var trail = t.Updates is null
            ? new List<TicketTrailEntry>()
            : JsonSerializer.Deserialize<List<TicketTrailEntry>>(t.Updates) ?? [];
        string? assigneeName = null;
        if (t.AssigneeId is not null)
            assigneeName = await db.Users.AsNoTracking().Where(u => u.Id == t.AssigneeId)
                .Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        return new TicketRow(t.Id, t.TicketNo, t.CustomerNo, t.DeviceNo, t.Description, t.Contact,
            t.Status, t.AssigneeId, assigneeName, trail, t.CreatedAt, t.UpdatedAt);
    }

    private async Task<Ticket> GetTicket(Guid id, CancellationToken ct)
        => await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct)
           ?? throw new DomainRuleException("TICKET_NOT_FOUND", "工单不存在");

    private Task LogTicket(string action, Ticket t, object? detail, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "ticket", TargetId: t.Id.ToString(), Detail: detail), ct);
}
