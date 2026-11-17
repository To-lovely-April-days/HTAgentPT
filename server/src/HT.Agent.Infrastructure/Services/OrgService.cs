using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>组织架构树 + 按部门审批 + 新增客户申请。谁审谁的规则在 OrgTree 里（纯计算），这里只取数、落库、留痕。</summary>
public class OrgService(AppDbContext db, IAuditWriter audit, ICurrentUser me) : IOrgService
{
    private async Task<List<OrgTree.Node>> NodesAsync(CancellationToken ct)
        => await db.Departments.AsNoTracking().Where(d => d.CompanyId == me.CompanyId)
            .Select(d => new OrgTree.Node(d.Id, d.ParentId, d.HeadUserId, d.Name)).ToListAsync(ct);

    private Task Log(string action, string? targetId, object detail, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success, UserId: me.UserId, Username: me.Username,
            CompanyId: me.CompanyId, TargetType: "department", TargetId: targetId, Detail: detail), ct);

    // ── 部门树 ───────────────────────────────────────────
    public async Task<IReadOnlyList<DeptRow>> TreeAsync(CancellationToken ct = default)
    {
        var depts = await db.Departments.AsNoTracking().Where(d => d.CompanyId == me.CompanyId)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.CreatedAt).ToListAsync(ct);
        var counts = await db.Users.AsNoTracking().Where(u => u.CompanyId == me.CompanyId && u.DepartmentId != null && u.IsActive)
            .GroupBy(u => u.DepartmentId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var heads = depts.Where(d => d.HeadUserId != null).Select(d => d.HeadUserId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => heads.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return depts.Select(d => new DeptRow(d.Id, d.Name, d.ParentId, d.HeadUserId,
            d.HeadUserId is null ? null : names.GetValueOrDefault(d.HeadUserId.Value), d.SortOrder, counts.GetValueOrDefault(d.Id))).ToList();
    }

    private async Task<Department> MustFindAsync(Guid id, CancellationToken ct)
        => await db.Departments.FirstOrDefaultAsync(d => d.Id == id && d.CompanyId == me.CompanyId, ct)
           ?? throw new DomainRuleException("DEPT_NOT_FOUND", "部门不存在");

    public async Task<DeptRow> CreateAsync(string name, Guid? parentId, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new DomainRuleException("DEPT_NAME_REQUIRED", "部门名称不能为空");
        if (parentId is not null) await MustFindAsync(parentId.Value, ct);
        var order = await db.Departments.Where(d => d.CompanyId == me.CompanyId && d.ParentId == parentId)
            .Select(d => (int?)d.SortOrder).MaxAsync(ct) ?? -1;
        var d = new Department { Id = Guid.NewGuid(), CompanyId = me.CompanyId, Name = name, ParentId = parentId, SortOrder = order + 1, CreatedAt = DateTimeOffset.UtcNow };
        db.Departments.Add(d);
        await db.SaveChangesAsync(ct);
        await Log("dept.create", d.Id.ToString(), new { name, parentId }, ct);
        return new DeptRow(d.Id, d.Name, d.ParentId, null, null, d.SortOrder, 0);
    }

    public async Task UpdateAsync(Guid id, string? name, Guid? parentId, bool moveParent, CancellationToken ct = default)
    {
        var d = await MustFindAsync(id, ct);
        if (name is not null)
        {
            name = name.Trim();
            if (name.Length == 0) throw new DomainRuleException("DEPT_NAME_REQUIRED", "部门名称不能为空");
            d.Name = name;
        }
        if (moveParent)
        {
            if (parentId is not null) await MustFindAsync(parentId.Value, ct);
            if (OrgTree.WouldCycle(await NodesAsync(ct), id, parentId))
                throw new DomainRuleException("DEPT_CYCLE", "不能把部门挂到它自己或它的下级部门下面");
            d.ParentId = parentId;
        }
        await db.SaveChangesAsync(ct);
        await Log("dept.update", id.ToString(), new { name, parentId, moveParent }, ct);
    }

    public async Task SetHeadAsync(Guid id, Guid? headUserId, CancellationToken ct = default)
    {
        var d = await MustFindAsync(id, ct);
        if (headUserId is not null)
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == headUserId && x.CompanyId == me.CompanyId && x.Kind == UserKind.Employee, ct)
                    ?? throw new DomainRuleException("USER_NOT_FOUND", "只能选本公司的员工账号当主管");
            u.DepartmentId = id; // 主管本人也归到这个部门
        }
        d.HeadUserId = headUserId;
        await db.SaveChangesAsync(ct);
        await Log("dept.set_head", id.ToString(), new { dept = d.Name, headUserId }, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var d = await MustFindAsync(id, ct);
        if (await db.Departments.AnyAsync(x => x.ParentId == id, ct))
            throw new DomainRuleException("DEPT_HAS_CHILDREN", "先把下级部门删掉或挪走");
        if (await db.Users.AnyAsync(u => u.DepartmentId == id, ct))
            throw new DomainRuleException("DEPT_HAS_MEMBERS", "部门里还有人，先把人调到别的部门");
        db.Departments.Remove(d);
        await db.SaveChangesAsync(ct);
        await Log("dept.delete", id.ToString(), new { d.Name }, ct);
    }

    public async Task SetUserDepartmentAsync(Guid userId, Guid? departmentId, CancellationToken ct = default)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == userId && x.CompanyId == me.CompanyId, ct)
                ?? throw new DomainRuleException("USER_NOT_FOUND", "账号不存在");
        if (departmentId is not null) await MustFindAsync(departmentId.Value, ct);
        // 调离原部门时，若他是原部门主管，主管位置空出来
        foreach (var headed in await db.Departments.Where(x => x.HeadUserId == userId && x.Id != departmentId).ToListAsync(ct))
            headed.HeadUserId = null;
        u.DepartmentId = departmentId;
        await db.SaveChangesAsync(ct);
        await Log("user.set_department", userId.ToString(), new { user = u.Username, departmentId }, ct);
    }

    // ── 审批范围 ─────────────────────────────────────────
    public async Task<ApprovalScope> ScopeAsync(CancellationToken ct = default)
    {
        var all = me.Permissions.Contains(PermissionKeys.CorpusManage);
        var nodes = await NodesAsync(ct);
        var isHead = OrgTree.IsHead(nodes, me.UserId);
        if (!isHead) return new ApprovalScope(all, new HashSet<Guid>(), false);
        var depts = OrgTree.ScopeOf(nodes, me.UserId);
        var users = await db.Users.AsNoTracking().Where(u => u.DepartmentId != null && depts.Contains(u.DepartmentId.Value) && u.Id != me.UserId)
            .Select(u => u.Id).ToListAsync(ct);
        return new ApprovalScope(all, users.ToHashSet(), true);
    }

    public async Task<bool> IsHeadAsync(Guid userId, CancellationToken ct = default)
        => await db.Departments.AnyAsync(d => d.HeadUserId == userId, ct);

    public async Task<ApproverInfo> ApproverForAsync(Guid userId, CancellationToken ct = default)
    {
        var nodes = await NodesAsync(ct);
        if (OrgTree.IsHead(nodes, userId)) return new ApproverInfo(true, null);
        var deptId = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DepartmentId).FirstOrDefaultAsync(ct);
        var dept = OrgTree.ApproverDeptFor(nodes, deptId, userId);
        if (dept?.HeadUserId is null) return new ApproverInfo(false, null);
        var name = await db.Users.AsNoTracking().Where(u => u.Id == dept.HeadUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        return new ApproverInfo(false, name);
    }

    // ── 新增客户 ─────────────────────────────────────────
    private async Task<List<string>> CustomerNamesAsync(CancellationToken ct)
        => await db.VocabTerms.AsNoTracking().Where(v => v.VocabKey == VocabKeys.CustomerName && v.IsActive).Select(v => v.Value).ToListAsync(ct);

    private async Task<bool> CanManageCustomersDirectlyAsync(CancellationToken ct)
        => me.Permissions.Contains(PermissionKeys.MetaManage) || await IsHeadAsync(me.UserId, ct);

    public async Task<CustomerRequestRow> RequestCustomerAsync(string name, string? reason, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length < 2) throw new DomainRuleException("CUSTOMER_NAME_REQUIRED", "客户名称至少两个字");
        if (name.Length > 100) throw new DomainRuleException("CUSTOMER_NAME_TOO_LONG", "客户名称太长");
        var existing = await CustomerNamesAsync(ct);
        if (existing.Contains(name)) throw new DomainRuleException("CUSTOMER_EXISTS", "名单里已经有这个客户了，直接选就行");
        // 自己就能加（主管、管理员）：直接进名单，不走申请
        if (await CanManageCustomersDirectlyAsync(ct))
        {
            await AddCustomerAsync(name, ct);
            return new CustomerRequestRow(Guid.Empty, name, reason, me.UserId, me.Username, null, DateTimeOffset.UtcNow,
                CustomerRequestStatus.Approved, name, null, DateTimeOffset.UtcNow, []);
        }
        var dup = await db.CustomerRequests.FirstOrDefaultAsync(r => r.CompanyId == me.CompanyId && r.Name == name && r.Status == CustomerRequestStatus.Pending, ct);
        if (dup is not null) return (await RowsAsync([dup], ct))[0];
        var req = new CustomerRequest
        {
            Id = Guid.NewGuid(), CompanyId = me.CompanyId, Name = name, Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            RequestedById = me.UserId, RequestedAt = DateTimeOffset.UtcNow
        };
        db.CustomerRequests.Add(req);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("customer.request", AuditResult.Success, UserId: me.UserId, Username: me.Username,
            CompanyId: me.CompanyId, TargetType: "customer_request", TargetId: req.Id.ToString(), Detail: new { name, req.Reason }), ct);
        return (await RowsAsync([req], ct))[0];
    }

    private async Task<List<CustomerRequestRow>> RowsAsync(IReadOnlyList<CustomerRequest> list, CancellationToken ct)
    {
        var uids = list.Select(r => r.RequestedById).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => uids.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName, u.DepartmentId }).ToListAsync(ct);
        var deptIds = users.Where(u => u.DepartmentId != null).Select(u => u.DepartmentId!.Value).Distinct().ToList();
        var depts = await db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var names = list.Any(r => r.Status == CustomerRequestStatus.Pending) ? await CustomerNamesAsync(ct) : [];
        return list.Select(r =>
        {
            var u = users.FirstOrDefault(x => x.Id == r.RequestedById);
            return new CustomerRequestRow(r.Id, r.Name, r.Reason, r.RequestedById, u?.DisplayName ?? "已删除账号",
                u?.DepartmentId is null ? null : depts.GetValueOrDefault(u.DepartmentId.Value), r.RequestedAt, r.Status, r.FinalName,
                r.ReviewNote, r.ReviewedAt, r.Status == CustomerRequestStatus.Pending ? OrgTree.SimilarNames(r.Name, names) : []);
        }).ToList();
    }

    public async Task<IReadOnlyList<CustomerRequestRow>> PendingCustomerRequestsAsync(CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        if (!scope.IsApprover) return [];
        var list = await db.CustomerRequests.AsNoTracking()
            .Where(r => r.CompanyId == me.CompanyId && r.Status == CustomerRequestStatus.Pending)
            .OrderBy(r => r.RequestedAt).Take(200).ToListAsync(ct);
        return await RowsAsync(list.Where(r => scope.Covers(r.RequestedById)).ToList(), ct);
    }

    public async Task<IReadOnlyList<CustomerRequestRow>> MyCustomerRequestsAsync(CancellationToken ct = default)
    {
        var list = await db.CustomerRequests.AsNoTracking().Where(r => r.RequestedById == me.UserId)
            .OrderByDescending(r => r.RequestedAt).Take(50).ToListAsync(ct);
        return await RowsAsync(list, ct);
    }

    private async Task<CustomerRequest> PendingInScopeAsync(Guid id, CancellationToken ct)
    {
        var r = await db.CustomerRequests.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == me.CompanyId, ct)
                ?? throw new DomainRuleException("REQUEST_NOT_FOUND", "申请不存在");
        if (!(await ScopeAsync(ct)).Covers(r.RequestedById))
            throw new ForbiddenException("FORBIDDEN_APPROVAL", "这条申请不归你审批。本次请求已被记录。");
        if (r.Status != CustomerRequestStatus.Pending) throw new DomainRuleException("REQUEST_DONE", "这条申请已经处理过了");
        return r;
    }

    public async Task ApproveCustomerAsync(Guid id, string? finalName, CancellationToken ct = default)
    {
        var r = await PendingInScopeAsync(id, ct);
        var name = string.IsNullOrWhiteSpace(finalName) ? r.Name : finalName.Trim();
        if (!(await CustomerNamesAsync(ct)).Contains(name)) await AddTermAsync(name, ct);
        r.Status = CustomerRequestStatus.Approved;
        r.FinalName = name;
        r.ReviewedById = me.UserId;
        r.ReviewedAt = DateTimeOffset.UtcNow;
        // 改了名：申请人这段时间用旧名提交的文件，客户名跟着改过来
        if (name != r.Name)
        {
            var metas = await db.DocMetadatas.Where(m => m.CustomerName == r.Name && m.Document!.UploadedById == r.RequestedById).ToListAsync(ct);
            foreach (var m in metas) m.CustomerName = name;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("customer.approve", AuditResult.Success, UserId: me.UserId, Username: me.Username,
            CompanyId: me.CompanyId, TargetType: "customer_request", TargetId: id.ToString(), Detail: new { r.Name, finalName = name }), ct);
    }

    public async Task RejectCustomerAsync(Guid id, string reason, CancellationToken ct = default)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length == 0) throw new DomainRuleException("REJECT_REASON_REQUIRED", "驳回要写原因，申请人能看到");
        var r = await PendingInScopeAsync(id, ct);
        r.Status = CustomerRequestStatus.Rejected;
        r.ReviewNote = reason.Length > 500 ? reason[..500] : reason;
        r.ReviewedById = me.UserId;
        r.ReviewedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("customer.reject", AuditResult.Success, UserId: me.UserId, Username: me.Username,
            CompanyId: me.CompanyId, TargetType: "customer_request", TargetId: id.ToString(), Detail: new { r.Name, reason }), ct);
    }

    public async Task AddCustomerAsync(string name, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length < 2) throw new DomainRuleException("CUSTOMER_NAME_REQUIRED", "客户名称至少两个字");
        if (!await CanManageCustomersDirectlyAsync(ct))
            throw new ForbiddenException("FORBIDDEN_CUSTOMER", "只有部门主管和管理员能直接新增客户。本次请求已被记录。");
        if ((await CustomerNamesAsync(ct)).Contains(name)) return;
        await AddTermAsync(name, ct);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("customer.add", AuditResult.Success, UserId: me.UserId, Username: me.Username,
            CompanyId: me.CompanyId, TargetType: "vocab", Detail: new { name }), ct);
    }

    private async Task AddTermAsync(string name, CancellationToken ct)
    {
        // 停用过的同名项直接启用，不重复建
        var off = await db.VocabTerms.FirstOrDefaultAsync(v => v.VocabKey == VocabKeys.CustomerName && v.Value == name, ct);
        if (off is not null) { off.IsActive = true; return; }
        var order = await db.VocabTerms.Where(v => v.VocabKey == VocabKeys.CustomerName).Select(v => (int?)v.SortOrder).MaxAsync(ct) ?? -1;
        db.VocabTerms.Add(new VocabTerm { Id = Guid.NewGuid(), VocabKey = VocabKeys.CustomerName, Value = name, SortOrder = order + 1 });
    }
}
