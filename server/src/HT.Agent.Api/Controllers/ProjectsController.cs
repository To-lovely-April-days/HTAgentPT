using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/projects")]
[Authorize]
public class ProjectsController(IProjectService projects, ICurrentUser me) : ControllerBase
{
    /// <summary>台账读取：检索权限（售前/售后）或元数据管理权限（E9 台账维护方）皆可——
    /// 维护台账的人看不了台账列表，维护无从下手。金额裁剪仍在服务层按密级做。</summary>
    private bool CanRead =>
        me.Permissions.Contains(PermissionKeys.ProjectSearch) ||
        me.Permissions.Contains(PermissionKeys.MetaManage);

    /// <summary>台账检索（表 8-1 POST /api/projects/search）：结构化结果，不经模型（FR-3.4）。
    /// 金额列由服务层按角色裁剪，响应里的 amountVisible 告知前端要不要渲染这一列。</summary>
    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] ProjectSearchRequest req, CancellationToken ct)
        => CanRead ? Ok(await projects.SearchAsync(req, ct))
                   : StatusCode(403, new { code = "FORBIDDEN", message = "你的角色不能查询项目台账。" });

    /// <summary>项目详情 + 关联文档（A3 界面）。</summary>
    [HttpGet("{projectNo}")]
    public async Task<IActionResult> Get(string projectNo, CancellationToken ct)
        => !CanRead ? StatusCode(403, new { code = "FORBIDDEN", message = "你的角色不能查询项目台账。" })
            : await projects.GetAsync(projectNo, ct) is { } detail ? Ok(detail) : NotFound();

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
