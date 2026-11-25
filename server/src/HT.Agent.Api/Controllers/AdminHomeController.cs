using HT.Agent.Api.Security;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>后台首页按角色拼块要的数（现有接口算不出来的那几样）。每样各挂各的权限：
/// 各部门用量、回答质量概况要「审计与统计」；价格库概况要「管理价格库」；本部门不挂固定权限，按组织架构认主管。</summary>
[ApiController]
[Route("api/admin-home")]
[Authorize]
public class AdminHomeController(AdminHomeService svc) : ControllerBase
{
    [HttpGet("departments")]
    [RequirePermission(PermissionKeys.AuditRead)]
    public async Task<IActionResult> Departments([FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await svc.DepartmentsAsync(Math.Clamp(days, 1, 365), ct));

    [HttpGet("quality")]
    [RequirePermission(PermissionKeys.AuditRead)]
    public async Task<IActionResult> Quality([FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await svc.QualityAsync(Math.Clamp(days, 1, 90), ct));

    [HttpGet("my-dept")]
    public async Task<IActionResult> MyDept([FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await svc.MyDeptAsync(Math.Clamp(days, 1, 365), ct));

    [HttpGet("prices")]
    [RequirePermission(PermissionKeys.PriceManage)]
    public async Task<IActionResult> Prices(CancellationToken ct)
        => Ok(await svc.PricesAsync(ct));
}
