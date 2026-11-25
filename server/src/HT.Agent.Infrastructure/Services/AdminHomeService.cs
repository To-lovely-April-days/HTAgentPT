using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>后台首页按角色拼的那几块里，现有接口算不出来的数：各部门用量、本部门（部门主管）、价格库概况、回答质量概况。
/// 权限在控制器上挂；本部门这块不挂固定权限，按组织架构认主管，只算他管的人。</summary>
public class AdminHomeService(AppDbContext db, ICurrentUser me, IOrgService org)
{
    public record DeptResult(int Days, IReadOnlyList<AdminHomeLogic.DeptUsage> Rows);

    /// <summary>调用记录：本期加上期（审计里成功的、能认出功能的那些）。</summary>
    private async Task<List<AdminHomeLogic.Call>> CallsAsync(DateTimeOffset since, IReadOnlyCollection<Guid>? users, CancellationToken ct)
    {
        var q = db.AuditLogs.AsNoTracking().Where(a => a.At >= since && a.Result == AuditResult.Success && a.UserId != null);
        if (users is not null) q = q.Where(a => users.Contains(a.UserId!.Value));
        var rows = await q.Select(a => new UsageDaily.Event(a.At, a.Action, a.UserId, a.Detail)).ToListAsync(ct);
        return rows.Select(e => (e, m: UsageDaily.ModuleOf(e))).Where(x => x.m is not null)
            .Select(x => new AdminHomeLogic.Call(x.e.UserId!.Value, x.e.At, x.m!)).ToList();
    }

    public async Task<DeptResult> DepartmentsAsync(int days, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-days); var prevFrom = from.AddDays(-days);
        var users = await db.Users.AsNoTracking().Where(u => u.IsActive && u.Kind == UserKind.Employee)
            .Select(u => new { u.Id, u.DepartmentId }).ToListAsync(ct);
        var names = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var calls = await CallsAsync(prevFrom, null, ct);
        return new DeptResult(days, AdminHomeLogic.Departments(users.Select(u => (u.Id, u.DepartmentId)), names, calls, from, prevFrom));
    }

    public record MyDeptResult(bool IsHead, string Name, int Days, int Members, int Active, int Calls, int PrevCalls, int NotHelpful,
        IReadOnlyList<AdminHomeLogic.PersonUsage> People, IReadOnlyList<ModuleCount> Modules);
    public record ModuleCount(string Name, int N);

    /// <summary>部门主管看本部门：他管的部门（含下级）里的人，加上他自己。不是主管的返回 IsHead=false。</summary>
    public async Task<MyDeptResult> MyDeptAsync(int days, CancellationToken ct)
    {
        var scope = await org.ScopeAsync(ct);
        if (!scope.IsHead) return new MyDeptResult(false, "", days, 0, 0, 0, 0, 0, [], []);
        var ids = scope.UserIds.Append(me.UserId).Distinct().ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.IsActive)
            .Select(u => new { u.Id, u.DisplayName }).ToListAsync(ct);
        var deptNames = await db.Departments.AsNoTracking().Where(d => d.HeadUserId == me.UserId)
            .OrderBy(d => d.SortOrder).Select(d => d.Name).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-days); var prevFrom = from.AddDays(-days);
        // 「最近一次用」看得远一点（一年），本期、上期的数在里面切
        var calls = await CallsAsync(now.AddDays(-365), ids, ct);
        var cur = calls.Where(c => c.At >= from).ToList();
        var notHelpful = await db.QaMessages.AsNoTracking()
            .CountAsync(m => m.At >= from && m.Helpful == false && ids.Contains(m.Session!.UserId), ct);
        return new MyDeptResult(true, string.Join("、", deptNames), days, people.Count,
            cur.Select(c => c.UserId).Distinct().Count(), cur.Count, calls.Count(c => c.At >= prevFrom && c.At < from), notHelpful,
            AdminHomeLogic.People(people.Select(p => (p.Id, p.DisplayName)), calls, from),
            AdminHomeLogic.Modules(calls, from).Select(x => new ModuleCount(x.Module, x.N)).ToList());
    }

    public record PriceVersionRow(string File, string Table, DateOnly Date, DateTimeOffset UploadedAt, int Items);
    public record PriceResult(int Items, int Suppliers, int StaleCount, IReadOnlyList<AdminHomeLogic.StaleItem> Stale,
        IReadOnlyList<PriceVersionRow> Recent, int UploadsThisMonth, int ItemsThisMonth, int LookupsThisMonth, int LookupsPrevMonth);

    /// <summary>价格库概况：现行版本里的价格（旧版本只用来看历史，不算）、多久没更新、最近上传、助手这个月查了几次价格。</summary>
    public async Task<PriceResult> PricesAsync(CancellationToken ct)
    {
        var tables = await db.PriceTables.AsNoTracking().Where(t => t.CompanyId == me.CompanyId && t.CurrentVersionId != null)
            .Select(t => new { t.Id, t.Name, t.CurrentVersionId }).ToListAsync(ct);
        var current = tables.Select(t => t.CurrentVersionId!.Value).ToList();
        var versionDates = await db.PriceVersions.AsNoTracking().Where(v => current.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.VersionDate, ct);
        var items = await db.PriceItems.AsNoTracking().Where(i => current.Contains(i.VersionId))
            .Select(i => new { i.Name, i.Spec, i.Group, i.Supplier, i.PriceDate, i.VersionId }).ToListAsync(ct);
        var today = DateOnly.FromDateTime(HomeService.Now.DateTime);
        var (staleCount, stale) = AdminHomeLogic.Stale(
            items.Select(i => (i.Name, i.Spec, i.Group, i.Supplier, AdminHomeLogic.PriceDateOf(i.PriceDate, versionDates.GetValueOrDefault(i.VersionId, today)))), today, 8);

        var tableIds = tables.Select(t => t.Id).ToList();
        var tableNames = tables.ToDictionary(t => t.Id, t => t.Name);
        var recent = await db.PriceVersions.AsNoTracking().Where(v => tableIds.Contains(v.TableId))
            .OrderByDescending(v => v.UploadedAt).Take(5).ToListAsync(ct);
        var monthStart = new DateTimeOffset(today.Year, today.Month, 1, 0, 0, 0, HomeService.Now.Offset);
        var prevStart = monthStart.AddMonths(-1);
        var thisMonth = await db.PriceVersions.AsNoTracking().Where(v => tableIds.Contains(v.TableId) && v.UploadedAt >= monthStart)
            .Select(v => v.ItemCount).ToListAsync(ct);
        // 助手查价格：回答里的 search_prices 步骤（只读含这个词的回答，免得把整月的回答都解析一遍）
        var payloads = await db.QaMessages.AsNoTracking()
            .Where(m => m.At >= prevStart && m.Session!.CompanyId == me.CompanyId && m.Payload != null && m.Payload.Contains("search_prices"))
            .Select(m => new { m.At, m.Payload }).ToListAsync(ct);
        return new PriceResult(items.Count, items.Select(i => i.Supplier).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Count(),
            staleCount, stale,
            recent.Select(v => new PriceVersionRow(v.FileName, tableNames.GetValueOrDefault(v.TableId, ""), v.VersionDate, v.UploadedAt, v.ItemCount)).ToList(),
            thisMonth.Count, thisMonth.Sum(),
            payloads.Where(p => p.At >= monthStart).Sum(p => AdminHomeLogic.CountTool(p.Payload, "search_prices")),
            payloads.Where(p => p.At < monthStart).Sum(p => AdminHomeLogic.CountTool(p.Payload, "search_prices")));
    }

    public record QualityResult(int Days, int Answers, int Helpful, int NotHelpful, int PrevNotHelpful, int NoResult);

    /// <summary>回答质量概况（只读，给有「审计与统计」的人看）：回答几次、点了有帮助 / 没帮上、资料里找不到答案的问题。</summary>
    public async Task<QualityResult> QualityAsync(int days, CancellationToken ct)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-days); var prevFrom = from.AddDays(-days);
        var msgs = db.QaMessages.AsNoTracking().Where(m => m.Session!.CompanyId == me.CompanyId);
        var answers = await msgs.CountAsync(m => m.At >= from, ct);
        var helpful = await msgs.CountAsync(m => m.At >= from && m.Helpful == true, ct);
        var notHelpful = await msgs.CountAsync(m => m.At >= from && m.Helpful == false, ct);
        var prevNot = await msgs.CountAsync(m => m.At >= prevFrom && m.At < from && m.Helpful == false, ct);
        var noResult = await db.AnswerIssues.AsNoTracking()
            .CountAsync(i => i.CompanyId == me.CompanyId && i.CreatedAt >= from && i.Source == IssueSource.NoEvidence, ct);
        return new QualityResult(days, answers, helpful, notHelpful, prevNot, noResult);
    }
}
