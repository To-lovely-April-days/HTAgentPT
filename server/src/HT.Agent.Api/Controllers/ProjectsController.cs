using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/projects")]
[Authorize]
public class ProjectsController(IProjectService projects) : ControllerBase
{
    /// <summary>台账检索（表 8-1 POST /api/projects/search）：结构化结果，不经模型（FR-3.4）。
    /// 金额列由服务层按角色裁剪，响应里的 amountVisible 告知前端要不要渲染这一列。</summary>
    [HttpPost("search")]
    [RequirePermission(PermissionKeys.ProjectSearch)]
    public async Task<IActionResult> Search([FromBody] ProjectSearchRequest req, CancellationToken ct)
        => Ok(await projects.SearchAsync(req, ct));

    /// <summary>项目详情 + 关联文档（A3 界面）。</summary>
    [HttpGet("{projectNo}")]
    [RequirePermission(PermissionKeys.ProjectSearch)]
    public async Task<IActionResult> Get(string projectNo, CancellationToken ct)
        => await projects.GetAsync(projectNo, ct) is { } detail ? Ok(detail) : NotFound();

    /// <summary>台账维护（E9 界面）归元数据管理权限。</summary>
    [HttpPost]
    [RequirePermission(PermissionKeys.MetaManage)]
    public async Task<IActionResult> Create([FromBody] ProjectEdit edit, CancellationToken ct)
    {
        await projects.CreateAsync(edit, ct);
        return NoContent();
    }

    [HttpPut("{projectNo}")]
    [RequirePermission(PermissionKeys.MetaManage)]
    public async Task<IActionResult> Update(string projectNo, [FromBody] ProjectEdit edit, CancellationToken ct)
    {
        await projects.UpdateAsync(projectNo, edit, ct);
        return NoContent();
    }
}
