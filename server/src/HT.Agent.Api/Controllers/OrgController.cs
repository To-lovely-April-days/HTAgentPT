using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

/// <summary>组织架构树（用户与权限 › 组织架构）。只有能管用户的人能改。</summary>
[ApiController]
[Route("api/departments")]
[Authorize]
[RequirePermission(PermissionKeys.UserManage)]
public class DepartmentsController(IOrgService org) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Tree(CancellationToken ct) => Ok(await org.TreeAsync(ct));

    public record CreateBody(string Name, Guid? ParentId);
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBody b, CancellationToken ct) => Ok(await org.CreateAsync(b.Name, b.ParentId, ct));

    /// <summary>改名和/或换上级。moveParent=true 时按 parentId 挪（null 挪到最上层）。</summary>
    public record UpdateBody(string? Name, Guid? ParentId, bool MoveParent);
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateBody b, CancellationToken ct)
    {
        await org.UpdateAsync(id, b.Name, b.ParentId, b.MoveParent, ct);
        return NoContent();
    }

    public record HeadBody(Guid? UserId);
    [HttpPut("{id:guid}/head")]
    public async Task<IActionResult> SetHead(Guid id, [FromBody] HeadBody b, CancellationToken ct)
    {
        await org.SetHeadAsync(id, b.UserId, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await org.DeleteAsync(id, ct);
        return NoContent();
    }

    public record MemberBody(Guid? DepartmentId);
    [HttpPut("members/{userId:guid}")]
    public async Task<IActionResult> SetMember(Guid userId, [FromBody] MemberBody b, CancellationToken ct)
    {
        await org.SetUserDepartmentAsync(userId, b.DepartmentId, ct);
        return NoContent();
    }
}

/// <summary>审批（部门主管和系统管理员）：本部门及下级部门同事上传的文件、新增客户申请。
/// 不挂固定权限：能不能审、能审谁，由组织架构决定（服务里按范围过滤，越界的请求拒绝并留痕）。</summary>
[ApiController]
[Route("api/approvals")]
[Authorize]
public class ApprovalsController(IOrgService org, IDocumentService docs, ICorpusReviewService review, AppDbContext db, ICurrentUser current) : ControllerBase
{
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var scope = await org.ScopeAsync(ct);
        if (!scope.IsApprover) return Ok(new { isApprover = false, files = 0, customers = 0 });
        var files = (await FilesInScopeAsync(scope, ct)).Count;
        var customers = (await org.PendingCustomerRequestsAsync(ct)).Count;
        return Ok(new { isApprover = true, isAdmin = scope.All, files, customers });
    }

    private async Task<List<DocRow>> FilesInScopeAsync(ApprovalScope scope, CancellationToken ct)
    {
        var rows = (await docs.ListAsync(null, null, null, ct, ReviewStatus.Pending)).Where(r => scope.Covers(r.UploadedById)).ToList();
        if (rows.Count == 0) return rows;
        // 客户名还在申请里（名单里还没有）：提示「客户待确认」，先处理客户申请
        var names = rows.Select(r => r.CustomerName).Where(n => n != null).Distinct().ToList();
        var pending = await db.CustomerRequests.AsNoTracking()
            .Where(r => r.Status == Domain.Entities.CustomerRequestStatus.Pending && names.Contains(r.Name)).Select(r => r.Name).ToListAsync(ct);
        return rows.Select(r => r.CustomerName != null && pending.Contains(r.CustomerName)
            ? r with { ReviewHint = $"客户「{r.CustomerName}」待确认" } : r).ToList();
    }

    [HttpGet("files")]
    public async Task<IActionResult> Files(CancellationToken ct)
    {
        var scope = await org.ScopeAsync(ct);
        return Ok(scope.IsApprover ? await FilesInScopeAsync(scope, ct) : []);
    }

    public record FilesBody(List<Guid> DocIds);
    public record RejectFilesBody(List<Guid> DocIds, string Reason);

    [HttpPost("files/approve")]
    public async Task<IActionResult> Approve([FromBody] FilesBody b, CancellationToken ct)
        => Ok(new { approved = await review.ApproveAsync(b.DocIds, ct) });

    [HttpPost("files/reject")]
    public async Task<IActionResult> Reject([FromBody] RejectFilesBody b, CancellationToken ct)
        => Ok(new { rejected = await review.RejectAsync(b.DocIds, b.Reason, ct) });

    [HttpGet("customers")]
    public async Task<IActionResult> Customers(CancellationToken ct) => Ok(await org.PendingCustomerRequestsAsync(ct));

    public record ApproveCustomerBody(string? Name);
    [HttpPost("customers/{id:guid}/approve")]
    public async Task<IActionResult> ApproveCustomer(Guid id, [FromBody] ApproveCustomerBody b, CancellationToken ct)
    {
        await org.ApproveCustomerAsync(id, b.Name, ct);
        return NoContent();
    }

    public record RejectBody(string Reason);
    [HttpPost("customers/{id:guid}/reject")]
    public async Task<IActionResult> RejectCustomer(Guid id, [FromBody] RejectBody b, CancellationToken ct)
    {
        await org.RejectCustomerAsync(id, b.Reason, ct);
        return NoContent();
    }

    public record AddCustomerBody(string Name);
    [HttpPost("customers")]
    public async Task<IActionResult> AddCustomer([FromBody] AddCustomerBody b, CancellationToken ct)
    {
        await org.AddCustomerAsync(b.Name, ct);
        return NoContent();
    }

    /// <summary>我处理过的：最近审过的文件和客户申请。</summary>
    [HttpGet("history")]
    public async Task<IActionResult> History(CancellationToken ct)
    {
        var me = current.UserId;
        var files = await db.Documents.AsNoTracking().Where(d => d.ReviewedById == me && d.ReviewedAt != null)
            .OrderByDescending(d => d.ReviewedAt).Take(50)
            .Select(d => new { kind = "file", id = d.Id, title = d.Title, review = d.Review, note = d.ReviewNote, at = d.ReviewedAt, d.UploadedById })
            .ToListAsync(ct);
        var customers = await db.CustomerRequests.AsNoTracking().Where(r => r.ReviewedById == me)
            .OrderByDescending(r => r.ReviewedAt).Take(50)
            .Select(r => new { kind = "customer", id = r.Id, title = r.FinalName ?? r.Name, review = r.Status.ToString(), note = r.ReviewNote, at = r.ReviewedAt, UploadedById = r.RequestedById })
            .ToListAsync(ct);
        var who = files.Select(f => f.UploadedById).Concat(customers.Select(c => c.UploadedById)).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => who.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return Ok(files.Select(f => new { f.kind, f.id, f.title, review = f.review.ToString(), f.note, f.at, by = names.GetValueOrDefault(f.UploadedById) })
            .Concat(customers.Select(c => new { c.kind, c.id, c.title, c.review, c.note, c.at, by = names.GetValueOrDefault(c.UploadedById) }))
            .OrderByDescending(x => x.at).Take(60));
    }
}
