using System.Text;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>条款库（FR-5.18）：报价与合同的条款不接受模型生成，仅从已审定条款中选取；
/// 已审定条款锁定，修改走「拟修改→新版本待审→通过后替代」，旧版停用保留——
/// 用旧版出过的合同要能查到当时原文。</summary>
public class ClauseService(AppDbContext db, IAuditWriter audit, ICurrentUser me) : IClauseService
{
    public async Task<IReadOnlyList<ClauseRow>> ListAsync(string? category, string? status, CancellationToken ct = default)
    {
        var q = db.Clauses.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(c => c.Category == category);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ClauseStatus>(status, true, out var st))
            q = q.Where(c => c.Status == st);
        var rows = await q.OrderBy(c => c.Category).ThenBy(c => c.Code).ThenByDescending(c => c.CreatedAt)
            .Take(1000)
            .Select(c => new
            {
                c.Id, c.Category, c.Code, c.Title, c.Text, c.Status, c.EffectiveDate, c.SupersedesId, c.CreatedAt,
                ApprovedByName = c.ApprovedBy == null ? null
                    : db.Users.Where(u => u.Id == c.ApprovedBy).Select(u => u.DisplayName).FirstOrDefault()
            })
            .ToListAsync(ct);
        return rows.Select(c => new ClauseRow(c.Id, c.Category, c.Code, c.Title, c.Text,
            c.Status.ToString(), c.ApprovedByName, c.EffectiveDate, c.SupersedesId, c.CreatedAt)).ToList();
    }

    public async Task<Guid> DraftAsync(ClauseEdit edit, CancellationToken ct = default)
    {
        Validate(edit);
        var clause = new Clause
        {
            Id = Guid.NewGuid(),
            Category = edit.Category.Trim(),
            Code = edit.Code.Trim(),
            Title = edit.Title.Trim(),
            Text = edit.Text.Trim(),
            Status = ClauseStatus.PendingReview, // 新增须经指定人员确认（FR-5.18）
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Clauses.Add(clause);
        await db.SaveChangesAsync(ct);
        await Log("clause.draft", clause, null, ct);
        return clause.Id;
    }

    public async Task<Guid> ReviseAsync(Guid clauseId, ClauseEdit edit, CancellationToken ct = default)
    {
        Validate(edit);
        var old = await Get(clauseId, ct);
        if (old.Status != ClauseStatus.Active)
            throw new DomainRuleException("CLAUSE_NOT_ACTIVE", "只有已审定条款走拟修改；未审定的直接改草稿");
        var revision = new Clause
        {
            Id = Guid.NewGuid(),
            Category = edit.Category.Trim(),
            Code = edit.Code.Trim(),
            Title = edit.Title.Trim(),
            Text = edit.Text.Trim(),
            Status = ClauseStatus.PendingReview,
            SupersedesId = old.Id, // 通过后旧版停用但保留
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Clauses.Add(revision);
        await db.SaveChangesAsync(ct);
        await Log("clause.revise", revision, new { supersedes = old.Id }, ct);
        return revision.Id;
    }

    public async Task ApproveAsync(Guid clauseId, CancellationToken ct = default)
    {
        var clause = await Get(clauseId, ct);
        if (clause.Status != ClauseStatus.PendingReview)
            throw new DomainRuleException("CLAUSE_NOT_PENDING", "只有待审条款可以审定");
        clause.Status = ClauseStatus.Active;
        clause.ApprovedBy = me.UserId;
        clause.EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow);
        if (clause.SupersedesId is not null)
        {
            var old = await db.Clauses.FirstOrDefaultAsync(c => c.Id == clause.SupersedesId, ct);
            if (old is not null) old.Status = ClauseStatus.Retired; // 新版生效、旧版停用保留
        }
        await db.SaveChangesAsync(ct);
        await Log("clause.approve", clause, new { superseded = clause.SupersedesId }, ct);
    }

    public async Task RejectAsync(Guid clauseId, CancellationToken ct = default)
    {
        var clause = await Get(clauseId, ct);
        if (clause.Status != ClauseStatus.PendingReview)
            throw new DomainRuleException("CLAUSE_NOT_PENDING", "只有待审条款可以驳回");
        clause.Status = ClauseStatus.Draft;
        await db.SaveChangesAsync(ct);
        await Log("clause.reject", clause, null, ct);
    }

    public async Task<ClauseAssembly> AssembleAsync(IReadOnlyList<Guid> clauseIds, CancellationToken ct = default)
    {
        if (clauseIds.Count == 0)
            throw new DomainRuleException("CLAUSES_EMPTY", "没有选择条款");
        var clauses = await db.Clauses.AsNoTracking()
            .Where(c => clauseIds.Contains(c.Id)).ToListAsync(ct);
        if (clauses.Count != clauseIds.Distinct().Count())
            throw new DomainRuleException("CLAUSE_NOT_FOUND", "有条款不存在");
        var inactive = clauses.Where(c => c.Status != ClauseStatus.Active).Select(c => c.Code).ToList();
        if (inactive.Count > 0)
            throw new DomainRuleException("CLAUSE_NOT_APPROVED",
                $"以下条款未经审定，不能进入报价合同（FR-5.18）：{string.Join("、", inactive)}");

        // 按选择顺序拼装，编号重排
        var ordered = clauseIds.Select(id => clauses.First(c => c.Id == id)).ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < ordered.Count; i++)
        {
            sb.AppendLine($"第{ToCn(i + 1)}条　{ordered[i].Title}");
            sb.AppendLine(ordered[i].Text.Trim());
            sb.AppendLine();
        }
        await audit.WriteAsync(new AuditEntry("clause.assemble", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new { clauses = ordered.Select(c => c.Code) }), ct);
        var rows = ordered.Select(c => new ClauseRow(c.Id, c.Category, c.Code, c.Title, c.Text,
            c.Status.ToString(), null, c.EffectiveDate, c.SupersedesId, c.CreatedAt)).ToList();
        return new ClauseAssembly(rows, sb.ToString().TrimEnd());
    }

    private static string ToCn(int n)
    {
        string[] d = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十"];
        if (n <= 10) return d[n];
        if (n < 20) return "十" + d[n - 10];
        return d[n / 10] + "十" + (n % 10 == 0 ? "" : d[n % 10]);
    }

    private static void Validate(ClauseEdit e)
    {
        if (string.IsNullOrWhiteSpace(e.Category) || string.IsNullOrWhiteSpace(e.Code) ||
            string.IsNullOrWhiteSpace(e.Title) || string.IsNullOrWhiteSpace(e.Text))
            throw new DomainRuleException("CLAUSE_FIELDS", "类别、编号、标题、正文都必填");
    }

    private async Task<Clause> Get(Guid id, CancellationToken ct)
        => await db.Clauses.FirstOrDefaultAsync(c => c.Id == id, ct)
           ?? throw new DomainRuleException("CLAUSE_NOT_FOUND", "条款不存在");

    private Task Log(string action, Clause c, object? detail, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "clause", TargetId: c.Id.ToString(),
            Detail: detail ?? new { c.Category, c.Code, status = c.Status.ToString() }), ct);
}
