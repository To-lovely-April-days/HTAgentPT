using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>客户自助入口（K 组，customer.self）：我的设备、报修、本人工单进度。</summary>
[ApiController]
[Route("api/customer")]
[Authorize]
[RequirePermission(PermissionKeys.CustomerSelf)]
public class CustomerController(ICustomerService customers) : ControllerBase
{
    [HttpGet("my-devices")]
    public async Task<IActionResult> MyDevices(CancellationToken ct)
        => Ok(await customers.MyDevicesAsync(ct));

    public record RepairBody(string DeviceNo, string Description, string Contact);

    /// <summary>报修（FR-8.8）：设备编号 + 现象描述 + 联系方式 → 生成工单。</summary>
    [HttpPost("tickets")]
    public async Task<IActionResult> Repair([FromBody] RepairBody body, CancellationToken ct)
        => Ok(await customers.CreateTicketAsync(body.DeviceNo, body.Description, body.Contact, ct));

    [HttpGet("tickets")]
    public async Task<IActionResult> MyTickets(CancellationToken ct)
        => Ok(await customers.MyTicketsAsync(ct));
}

/// <summary>售后工单处理（FR-8.8：查看、指派、更新状态）。</summary>
[ApiController]
[Route("api/tickets")]
[Authorize]
[RequirePermission(PermissionKeys.TicketHandle)]
public class TicketsController(ICustomerService customers) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] TicketStatus? status, CancellationToken ct)
        => Ok(await customers.ListTicketsAsync(status, ct));

    public record AssignBody(Guid AssigneeId);

    [HttpPost("{id:guid}/assign")]
    public async Task<IActionResult> Assign(Guid id, [FromBody] AssignBody body, CancellationToken ct)
    {
        await customers.AssignTicketAsync(id, body.AssigneeId, ct);
        return NoContent();
    }

    public record StatusBody(TicketStatus Status, string? Note);

    [HttpPost("{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] StatusBody body, CancellationToken ct)
    {
        await customers.UpdateTicketStatusAsync(id, body.Status, body.Note, ct);
        return NoContent();
    }
}

/// <summary>客户设备台账维护（管理员/元数据权限）。</summary>
[ApiController]
[Route("api/customer-devices")]
[Authorize]
[RequirePermission(PermissionKeys.MetaManage)]
public class CustomerDevicesController(ICustomerService customers) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Register([FromBody] DeviceEdit edit, CancellationToken ct)
        => Ok(new { id = await customers.RegisterDeviceAsync(edit, ct) });

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? customerNo, CancellationToken ct)
        => Ok(await customers.ListDevicesAsync(customerNo, ct));
}
