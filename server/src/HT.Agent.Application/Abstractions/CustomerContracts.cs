using HT.Agent.Domain;

namespace HT.Agent.Application.Abstractions;

/// <summary>客户入口（FR-7.4、FR-8.8、K 组）与售后工单处理。</summary>
public interface ICustomerService
{
    // ── 设备（管理员维护，客户自查）─────────────────────
    Task<Guid> RegisterDeviceAsync(DeviceEdit edit, CancellationToken ct = default);
    Task<IReadOnlyList<DeviceRow>> ListDevicesAsync(string? customerNo, CancellationToken ct = default);
    /// <summary>我的设备（K3）：仅本人名下（数据范围，非权限开关）。</summary>
    Task<IReadOnlyList<DeviceRow>> MyDevicesAsync(CancellationToken ct = default);

    // ── 工单（FR-8.8：本期最简流转）────────────────────
    /// <summary>客户报修：设备编号 + 现象描述 + 联系方式 → 生成工单。</summary>
    Task<TicketRow> CreateTicketAsync(string deviceNo, string description, string contact, CancellationToken ct = default);
    /// <summary>客户查本人工单进度。</summary>
    Task<IReadOnlyList<TicketRow>> MyTicketsAsync(CancellationToken ct = default);
    /// <summary>售后侧列表。</summary>
    Task<IReadOnlyList<TicketRow>> ListTicketsAsync(TicketStatus? status, CancellationToken ct = default);
    Task AssignTicketAsync(Guid ticketId, Guid assigneeId, CancellationToken ct = default);
    Task UpdateTicketStatusAsync(Guid ticketId, TicketStatus status, string? note, CancellationToken ct = default);
}

public record DeviceEdit(string CustomerNo, string DeviceNo, string Model, string? ProjectNo, DateOnly? DeliveredAt);

public record DeviceRow(Guid Id, string CustomerNo, string DeviceNo, string Model, string? ProjectNo,
    DateOnly? DeliveredAt, string? DeliveryStatus);

public record TicketRow(
    Guid Id, string TicketNo, string CustomerNo, string DeviceNo, string Description, string Contact,
    TicketStatus Status, Guid? AssigneeId, string? AssigneeName,
    IReadOnlyList<TicketTrailEntry> Trail, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record TicketTrailEntry(DateTimeOffset At, string By, string Status, string? Note);
