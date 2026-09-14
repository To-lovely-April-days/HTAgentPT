using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>项目台账（FR-3.3/3.4）。结构化查询、不经模型；
/// 合同金额是字段级机密（表 4-6、3.4 第五条）——裁剪在服务层做，前端拿不到就是拿不到。</summary>
public class ProjectService(AppDbContext db, IVocabService vocab, IAuditWriter audit, ICurrentUser me) : IProjectService
{
    private bool AmountVisible => me.Classifications.Contains(Classification.Confidential);

    public async Task<ProjectSearchResult> SearchAsync(ProjectSearchRequest req, CancellationToken ct = default)
    {
        var amountVisible = AmountVisible;
        // 无金额可见权限时金额筛选条件被忽略——若参与过滤，结果集合的变化本身就会泄露金额区间
        var amountFilterIgnored = !amountVisible && (req.AmountMin is not null || req.AmountMax is not null);

        var q = db.Projects.AsNoTracking().Where(p => p.CompanyId == me.CompanyId);
        if (!string.IsNullOrWhiteSpace(req.CustomerName)) q = q.Where(p => p.CustomerName.Contains(req.CustomerName));
        if (req.YearFrom is not null) q = q.Where(p => p.Year >= req.YearFrom);
        if (req.YearTo is not null) q = q.Where(p => p.Year <= req.YearTo);
        if (!string.IsNullOrWhiteSpace(req.DeviceType)) q = q.Where(p => p.DeviceType == req.DeviceType);
        if (req.DeliveryStatus is not null) q = q.Where(p => p.DeliveryStatus == req.DeliveryStatus);
        if (!string.IsNullOrWhiteSpace(req.Keyword))
            q = q.Where(p => p.ProjectNo.Contains(req.Keyword) ||
                             (p.DeviceModel != null && p.DeviceModel.Contains(req.Keyword)) ||
                             (p.SpecParams != null && p.SpecParams.Contains(req.Keyword)));
        if (amountVisible)
        {
            if (req.AmountMin is not null) q = q.Where(p => p.ContractAmount >= req.AmountMin);
            if (req.AmountMax is not null) q = q.Where(p => p.ContractAmount <= req.AmountMax);
        }

        var rows = await q.OrderByDescending(p => p.Year).ThenByDescending(p => p.UpdatedAt)
            .Take(Math.Clamp(req.Limit, 1, 1000))
            .Select(p => new ProjectRow(p.ProjectNo, p.CustomerName, p.Year, p.DeviceType, p.DeviceModel,
                p.SpecParams, amountVisible ? p.ContractAmount : null, p.DeliveryStatus, p.OwnerId, p.UpdatedAt))
            .ToListAsync(ct);

        await audit.WriteAsync(new AuditEntry("project.search", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new { req.CustomerName, req.YearFrom, req.YearTo, req.DeviceType, rows = rows.Count, amountVisible }), ct);
        return new ProjectSearchResult(rows, amountVisible, amountFilterIgnored);
    }

    public async Task<ProjectDetail?> GetAsync(string projectNo, CancellationToken ct = default)
    {
        var amountVisible = AmountVisible;
        var p = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectNo == projectNo && x.CompanyId == me.CompanyId, ct);
        if (p is null) return null;

        // 关联文档按调用者密级过滤：列表本身也不能暴露拿不到的机密文档存在
        var docs = await db.DocMetadatas.AsNoTracking()
            .Where(m => m.ProjectNo == projectNo)
            .Join(db.Documents.AsNoTracking(), m => m.DocumentId, d => d.Id, (m, d) => new { m, d })
            .Where(x => me.Classifications.Contains(x.d.Classification))
            .OrderByDescending(x => x.d.UploadedAt)
            .Select(x => new ProjectDocRow(x.d.Id, x.d.Title, x.m.DocCategory, x.d.Classification,
                x.d.ParseStatus, x.d.UploadedAt))
            .ToListAsync(ct);

        await audit.WriteAsync(new AuditEntry("project.get", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "project", TargetId: projectNo), ct);
        var row = new ProjectRow(p.ProjectNo, p.CustomerName, p.Year, p.DeviceType, p.DeviceModel,
            p.SpecParams, amountVisible ? p.ContractAmount : null, p.DeliveryStatus, p.OwnerId, p.UpdatedAt);
        return new ProjectDetail(row, docs);
    }

    public async Task CreateAsync(ProjectEdit edit, CancellationToken ct = default)
    {
        await ValidateAsync(edit, ct);
        if (await db.Projects.AnyAsync(p => p.ProjectNo == edit.ProjectNo, ct))
            throw new DomainRuleException("PROJECT_EXISTS", $"项目编号 {edit.ProjectNo} 已存在");
        db.Projects.Add(new Project
        {
            ProjectNo = edit.ProjectNo.Trim(),
            CompanyId = me.CompanyId,
            CustomerName = edit.CustomerName.Trim(),
            Year = edit.Year,
            DeviceType = edit.DeviceType,
            DeviceModel = edit.DeviceModel,
            SpecParams = edit.SpecParams,
            ContractAmount = edit.ContractAmount,
            DeliveryStatus = edit.DeliveryStatus,
            OwnerId = edit.OwnerId,
            DocPath = edit.DocPath,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await Log("project.create", edit.ProjectNo, ct);
    }

    public async Task UpdateAsync(string projectNo, ProjectEdit edit, CancellationToken ct = default)
    {
        await ValidateAsync(edit, ct);
        var p = await db.Projects.FirstOrDefaultAsync(x => x.ProjectNo == projectNo && x.CompanyId == me.CompanyId, ct)
            ?? throw new DomainRuleException("PROJECT_NOT_FOUND", "项目不存在");
        p.CustomerName = edit.CustomerName.Trim();
        p.Year = edit.Year;
        p.DeviceType = edit.DeviceType;
        p.DeviceModel = edit.DeviceModel;
        p.SpecParams = edit.SpecParams;
        p.ContractAmount = edit.ContractAmount;
        p.DeliveryStatus = edit.DeliveryStatus;
        p.OwnerId = edit.OwnerId;
        p.DocPath = edit.DocPath;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await Log("project.update", projectNo, ct);
    }

    private async Task ValidateAsync(ProjectEdit edit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(edit.ProjectNo))
            throw new DomainRuleException("PROJECT_NO_REQUIRED", "项目编号必填（表 4-6 主键）");
        if (string.IsNullOrWhiteSpace(edit.CustomerName))
            throw new DomainRuleException("CUSTOMER_REQUIRED", "客户名称必填");
        if (edit.Year < 1990 || edit.Year > 2100)
            throw new DomainRuleException("YEAR_INVALID", "年份须为四位数字");
        if (!await vocab.IsValidAsync(VocabKeys.DeviceType, edit.DeviceType, ct))
            throw new DomainRuleException("DEVICE_TYPE_INVALID", $"设备类型「{edit.DeviceType}」不在受控词表中");
    }

    private Task Log(string action, string projectNo, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "project", TargetId: projectNo), ct);
}
