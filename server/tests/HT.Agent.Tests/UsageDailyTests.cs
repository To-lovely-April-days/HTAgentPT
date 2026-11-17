using System.Text.Json;
using HT.Agent.Application.Logic;
using static HT.Agent.Application.Logic.UsageDaily;

namespace HT.Agent.Tests;

public class UsageDailyTests
{
    private static readonly TimeSpan Cn = TimeSpan.FromHours(8);
    private static readonly DateOnly Today = new(2026, 11, 17);
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    private static Event E(string day, int hourCn, string action, Guid? user, object? detail = null) =>
        new(new DateTimeOffset(DateOnly.Parse(day).ToDateTime(new TimeOnly(hourCn, 0)), Cn).ToUniversalTime(),
            action, user, detail is null ? null : JsonSerializer.Serialize(detail));

    private static Result Run(IReadOnlyList<Event> xs, int days = 7, bool year = false) =>
        Build(xs, Today, days, Cn, new Dictionary<Guid, Person> { [A] = new(A, "张工", "工程师") }, 5, year ? xs : null);

    [Fact]
    public void Counts_questions_by_start_and_hit_rate_by_knowledge_complete()
    {
        var r = Run([
            E("2026-11-17", 9, "qa.multi_agent.start", A, new { intent = "knowledge" }),
            E("2026-11-17", 9, "qa.multi_agent.complete", A, new { intent = "knowledge", answered = true }),
            E("2026-11-17", 10, "qa.multi_agent.start", A, new { intent = "knowledge" }),
            E("2026-11-17", 10, "qa.multi_agent.complete", A, new { intent = "knowledge", answered = false }),
            E("2026-11-16", 10, "qa.multi_agent.start", B, new { intent = "ledger" }),
            E("2026-11-16", 10, "qa.multi_agent.complete", B, new { intent = "ledger", answered = true }),
            E("2026-11-16", 11, "auth.login", B),
        ]);
        Assert.Equal(3, r.Totals.Calls);
        Assert.Equal(50.0, r.Totals.HitRate);
        Assert.Equal(1, r.Totals.NoResult);
        Assert.Equal(2, r.Totals.ActiveUsers);
        Assert.Equal(2, r.ModuleRows.Single(m => m.Name == "检索问答").N);
        Assert.Equal(1, r.ModuleRows.Single(m => m.Name == "项目查询").N);
        Assert.Equal("已删除账号", r.People.Single(p => p.UserId == B).Name);
    }

    [Fact]
    public void Splits_days_in_caller_timezone_and_compares_previous_period()
    {
        // 北京时间 0 点在 UTC 还是前一天——要按北京时间算到 11-17
        var r = Run([
            E("2026-11-17", 0, "translate.text", A),
            E("2026-11-10", 23, "translate.text", A), // 本期第一天（11-11）之前 → 上期
        ]);
        Assert.Equal(7, r.Days.Count);
        Assert.Equal("2026-11-11", r.From);
        Assert.Equal(1, r.Days.Last().Total);
        Assert.Equal(1, r.Totals.Calls);
        Assert.Equal(1, r.Totals.PrevCalls);
    }

    [Fact]
    public void Template_names_survive_escaped_json_and_year_has_streak()
    {
        var r = Run([
            E("2026-11-15", 9, "gen.render", A, new { Name = "技术协议" }),
            E("2026-11-16", 9, "gen.render", A, new { Name = "技术协议" }),
            E("2026-11-17", 9, "gen.chat", A),
        ], year: true);
        Assert.Equal(2, r.Totals.Docs);
        Assert.Equal(2, r.Totals.DocsByTemplate["技术协议"]);
        Assert.Equal(365, r.Year!.Days.Count);
        Assert.Equal(3, r.Year.LongestStreak);
        Assert.Equal(3, r.Year.ActiveDays);
    }
}
