using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>故障案例（表 8-1 /api/cases）。录入=售后（case.write），检索=售前+售后（case.read）。</summary>
[ApiController]
[Route("api/cases")]
[Authorize]
public class CasesController(IFaultCaseService cases) : ControllerBase
{
    [HttpPost]
    [RequirePermission(PermissionKeys.CaseWrite)]
    public async Task<IActionResult> Create([FromBody] CaseEdit edit, CancellationToken ct)
        => Ok(new { id = await cases.CreateAsync(edit, ct) });

    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionKeys.CaseWrite)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CaseEdit edit, CancellationToken ct)
    {
        await cases.UpdateAsync(id, edit, ct);
        return NoContent();
    }

    [HttpPost("search")]
    [RequirePermission(PermissionKeys.CaseRead)]
    public async Task<IActionResult> Search([FromBody] CaseSearchRequest req, CancellationToken ct)
        => Ok(await cases.SearchAsync(req, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionKeys.CaseRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => await cases.GetAsync(id, ct) is { } detail ? Ok(detail) : NotFound();

    /// <summary>同型归并（FR-8.6）。</summary>
    [HttpPost("search-grouped")]
    [RequirePermission(PermissionKeys.CaseRead)]
    public async Task<IActionResult> SearchGrouped([FromBody] CaseSearchRequest req, CancellationToken ct)
        => Ok(await cases.SearchGroupedAsync(req, ct));

    /// <summary>提交前敏感检测（FR-8.3，B4 界面的高亮提示数据源）。</summary>
    [HttpPost("{id:guid}/check-sensitive")]
    [RequirePermission(PermissionKeys.CaseWrite)]
    public async Task<IActionResult> CheckSensitive(Guid id, CancellationToken ct)
        => Ok(await cases.CheckSensitiveAsync(id, ct));

    public record SubmitBody(bool Acknowledged);

    /// <summary>提交总部（表 8-1 POST /api/cases/{id}/submit）。</summary>
    [HttpPost("{id:guid}/submit")]
    [RequirePermission(PermissionKeys.CaseWrite)]
    public async Task<IActionResult> Submit(Guid id, [FromBody] SubmitBody body, CancellationToken ct)
    {
        await cases.SubmitAsync(id, body.Acknowledged, ct);
        return NoContent();
    }

    /// <summary>撤回提交（B3 pending 态主动作）：总部仍在待审才可撤，撤回后回到本地生效。</summary>
    [HttpPost("{id:guid}/withdraw")]
    [RequirePermission(PermissionKeys.CaseWrite)]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct)
    {
        await cases.WithdrawAsync(id, ct);
        return NoContent();
    }
}
