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
}
