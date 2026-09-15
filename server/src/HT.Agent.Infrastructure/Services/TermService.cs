using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>术语表（FR-3.5、FR-6.4）。管理员直接新增即时生效；
/// 翻译中的提交进入待审，经确认后才参与注入——不让个人口味悄悄改动全公司的译名。</summary>
public class TermService(AppDbContext db, IAuditWriter audit, ICurrentUser me) : ITermService
{
    public async Task<IReadOnlyList<TermRow>> ListAsync(string? domain, string? status, CancellationToken ct = default)
    {
        var q = db.Terms.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(domain)) q = q.Where(t => t.Domain == domain);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<TermStatus>(status, true, out var st))
            q = q.Where(t => t.Status == st);
        return await q.OrderBy(t => t.Domain).ThenBy(t => t.Zh).Take(2000)
            .Select(t => new TermRow(t.Id, t.Domain, t.Zh, t.En, t.Note, t.Status.ToString(), t.SubmittedBy, t.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<Guid> AddAsync(TermEdit edit, CancellationToken ct = default)
    {
        Validate(edit);
        if (await ExistsAsync(edit, ct))
            throw new DomainRuleException("TERM_DUP", $"术语「{edit.Zh} / {edit.En}」已存在");
        var term = NewTerm(edit, TermStatus.Approved, approvedBy: me.UserId);
        db.Terms.Add(term);
        await db.SaveChangesAsync(ct);
        await Log("term.add", term, ct);
        return term.Id;
    }

    public async Task<int> ImportAsync(IReadOnlyList<TermEdit> rows, CancellationToken ct = default)
    {
        if (rows.Count > 5000)
            throw new DomainRuleException("TERM_IMPORT_TOO_LARGE", "单次最多导入 5000 条");
        var imported = 0;
        foreach (var edit in rows)
        {
            Validate(edit);
            if (await ExistsAsync(edit, ct)) continue; // 重复对照跳过
            db.Terms.Add(NewTerm(edit, TermStatus.Approved, approvedBy: me.UserId));
            imported++;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("term.import", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new { requested = rows.Count, imported }), ct);
        return imported;
    }

    public async Task<Guid> SuggestAsync(TermEdit edit, CancellationToken ct = default)
    {
        Validate(edit);
        if (await ExistsAsync(edit, ct))
            throw new DomainRuleException("TERM_DUP", "同样的对照已在术语表中");
        var term = NewTerm(edit, TermStatus.Pending, approvedBy: null);
        term.SubmittedBy = me.UserId;
        db.Terms.Add(term);
        await db.SaveChangesAsync(ct);
        await Log("term.suggest", term, ct);
        return term.Id;
    }

    public async Task ApproveAsync(Guid termId, CancellationToken ct = default)
    {
        var term = await Get(termId, ct);
        term.Status = TermStatus.Approved;
        term.ApprovedBy = me.UserId;
        await db.SaveChangesAsync(ct);
        await Log("term.approve", term, ct);
    }

    public async Task RejectAsync(Guid termId, CancellationToken ct = default)
    {
        var term = await Get(termId, ct);
        term.Status = TermStatus.Rejected;
        term.ApprovedBy = me.UserId;
        await db.SaveChangesAsync(ct);
        await Log("term.reject", term, ct);
    }

    private async Task<Term> Get(Guid id, CancellationToken ct)
        => await db.Terms.FirstOrDefaultAsync(t => t.Id == id, ct)
           ?? throw new DomainRuleException("TERM_NOT_FOUND", "术语不存在");

    private Task<bool> ExistsAsync(TermEdit e, CancellationToken ct)
        => db.Terms.AnyAsync(t => t.Domain == e.Domain && t.Zh == e.Zh && t.En == e.En
                                  && t.Status != TermStatus.Rejected, ct);

    private static void Validate(TermEdit e)
    {
        if (string.IsNullOrWhiteSpace(e.Domain))
            throw new DomainRuleException("TERM_DOMAIN_REQUIRED", "术语领域必填（FR-3.5：按领域分类）");
        if (string.IsNullOrWhiteSpace(e.Zh) || string.IsNullOrWhiteSpace(e.En))
            throw new DomainRuleException("TERM_PAIR_REQUIRED", "中文与英文对照都必填");
    }

    private Term NewTerm(TermEdit e, TermStatus status, Guid? approvedBy) => new()
    {
        Id = Guid.NewGuid(),
        Domain = e.Domain.Trim(),
        Zh = e.Zh.Trim(),
        En = e.En.Trim(),
        Note = e.Note,
        Status = status,
        ApprovedBy = approvedBy,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private Task Log(string action, Term term, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "term", TargetId: term.Id.ToString(),
            Detail: new { term.Domain, term.Zh, term.En, status = term.Status.ToString() }), ct);
}
