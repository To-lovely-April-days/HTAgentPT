using HT.Agent.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>隔离区客户门户（K 组）。全部匿名——登录只是「我的设备」的入口，不是前置门。
/// 客户登录后走 /api/auth/login 与 /api/customer/*，与内网同源鉴权。</summary>
[ApiController]
[Route("api/portal")]
[AllowAnonymous]
public class PortalController(IPortalService portal) : ControllerBase
{
    public record SearchBody(string Query);

    /// <summary>K1 自助查询：仅公开资料，返回片段与来源；不提供下载（表 9-1 客户功能无下载项）。</summary>
    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] SearchBody body, CancellationToken ct)
        => Ok(await portal.SearchAsync(body.Query, ct));

    /// <summary>K4 匿名报修：单号是提交到开户之间唯一的凭据，客户端要把它做成页面主体。</summary>
    [HttpPost("tickets")]
    public async Task<IActionResult> Repair([FromBody] PortalRepairRequest body, CancellationToken ct)
        => Ok(await portal.CreateTicketAsync(body, ct));

    /// <summary>K5 凭单号查进度：单号 + 提交时联系方式同时匹配。查不到统一说没找到，
    /// 不区分原因（防枚举）。</summary>
    [HttpGet("tickets/{ticketNo}")]
    public async Task<IActionResult> Track(string ticketNo, [FromQuery] string contact, CancellationToken ct)
        => await portal.TrackAsync(ticketNo, contact, ct) is { } v
            ? Ok(v)
            : NotFound(new { code = "TICKET_NOT_FOUND", message = "没有找到匹配的报修单。请核对报修单号与提交时留下的联系电话。" });
}
