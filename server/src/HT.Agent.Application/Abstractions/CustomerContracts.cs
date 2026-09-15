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
    /// <summary>forCustomer=true 的备注是写给客户的留言，会出现在客户侧进度页；
    /// 内部记录（技术判断、备件信息）保持 false，客户侧不透出（K5）。</summary>
    Task UpdateTicketStatusAsync(Guid ticketId, TicketStatus status, string? note, bool forCustomer = false, CancellationToken ct = default);
}

public record DeviceEdit(string CustomerNo, string DeviceNo, string Model, string? ProjectNo, DateOnly? DeliveredAt);

public record DeviceRow(Guid Id, string CustomerNo, string DeviceNo, string Model, string? ProjectNo,
    DateOnly? DeliveredAt, string? DeliveryStatus);

public record TicketRow(
    Guid Id, string TicketNo, string CustomerNo, string DeviceNo, string Description, string Contact,
    TicketStatus Status, Guid? AssigneeId, string? AssigneeName,
    IReadOnlyList<TicketTrailEntry> Trail, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record TicketTrailEntry(DateTimeOffset At, string By, string Status, string? Note, bool ForCustomer = false);

// ─────────────── 客户门户（K 组，匿名可用——登录不是前置门）───────────────

/// <summary>隔离区门户：匿名搜索限公开库；匿名报修与凭单号查进度是「没账号就报不了修」
/// 死循环的出口（账号由售后在处理报修时开通，FR-7.4）。</summary>
public interface IPortalService
{
    /// <summary>匿名自助查询：仅公开库、仅关键词路径，返回资料片段与来源（不给下载）。</summary>
    Task<IReadOnlyList<PortalHit>> SearchAsync(string query, CancellationToken ct = default);
    /// <summary>匿名报修：设备编号可不填（未开户客户不知道编号规则也要能报上）。</summary>
    Task<PortalTicketCreated> CreateTicketAsync(PortalRepairRequest req, CancellationToken ct = default);
    /// <summary>凭单号 + 提交时留的联系方式查进度；两者同时匹配才返回，防单号枚举。
    /// 客户侧只呈现状态节点与对客留言，不透出内部处理记录。</summary>
    Task<PortalTicketView?> TrackAsync(string ticketNo, string contact, CancellationToken ct = default);
}

public record PortalHit(string DocTitle, string? Section, int? PageNo, string Excerpt);
public record PortalRepairRequest(string? DeviceNo, string? Model, string Description, string Contact, string? CustomerName);
public record PortalTicketCreated(string TicketNo, DateTimeOffset CreatedAt);
public record PortalTicketNode(DateTimeOffset At, string Status, string? Message);
public record PortalTicketView(string TicketNo, string Status, string DeviceNo, DateTimeOffset CreatedAt,
    IReadOnlyList<PortalTicketNode> Nodes);
