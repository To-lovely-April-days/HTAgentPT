using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/roles")]
[Authorize]
[RequirePermission(PermissionKeys.UserManage)]
public class RolesController(IRoleService roles) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await roles.ListAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RoleEdit edit, CancellationToken ct)
        => Ok(new { id = await roles.CreateAsync(edit, ct) });

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] RoleEdit edit, CancellationToken ct)
    {
        await roles.UpdateAsync(id, edit, ct);
        return NoContent();
    }
}
