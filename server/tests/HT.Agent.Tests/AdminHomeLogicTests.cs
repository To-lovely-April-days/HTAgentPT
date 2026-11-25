using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class AdminHomeLogicTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset From = Now.AddDays(-30), PrevFrom = Now.AddDays(-60);
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), Gone = Guid.NewGuid();
    private static readonly Guid Sales = Guid.NewGuid(), Service = Guid.NewGuid();

    private static AdminHomeLogic.Call Call(Guid u, int daysAgo, string m = "检索问答") => new(u, Now.AddDays(-daysAgo), m);

    [Fact]
    public void 各部门_人数在用调用上期都分开算()
    {
        var users = new (Guid, Guid?)[] { (A, Sales), (B, Sales), (C, null) };
        var names = new Dictionary<Guid, string> { [Sales] = "售前部", [Service] = "售后部" };
        var calls = new[] { Call(A, 1), Call(A, 2), Call(B, 40), Call(C, 3), Call(Gone, 1) };
        var rows = AdminHomeLogic.Departments(users, names, calls, From, PrevFrom);

        var sales = rows.Single(r => r.Name == "售前部");
        Assert.Equal((2, 1, 2, 1), (sales.Members, sales.Active, sales.Calls, sales.PrevCalls));
        var none = rows.Single(r => r.Name == AdminHomeLogic.NoDept);
        Assert.Equal((1, 1, 1, 0), (none.Members, none.Active, none.Calls, none.PrevCalls));
        Assert.DoesNotContain(rows, r => r.Name == "售后部");   // 没人的部门不出现
        Assert.Equal("售前部", rows[0].Name);                  // 按本期调用排
    }

    [Fact]
    public void 本部门的人_用得多的在前_没用过的排最后()
    {
        var people = AdminHomeLogic.People([(A, "李工"), (B, "王工"), (C, "郑工")], [Call(A, 1), Call(B, 1), Call(B, 2), Call(A, 90)], From);
        Assert.Equal(["王工", "李工", "郑工"], people.Select(p => p.Name));
        Assert.Equal(1, people[1].Calls);                           // 90 天前那次不算本期
        Assert.Equal(Now.AddDays(-1), people[1].LastAt);
        Assert.Null(people[2].LastAt);
    }

    [Fact]
    public void 按功能只算本期()
    {
        var m = AdminHomeLogic.Modules([Call(A, 1, "出文档"), Call(A, 2, "出文档"), Call(B, 3), Call(B, 50, "翻译")], From);
        Assert.Equal([("出文档", 2), ("检索问答", 1)], m);
    }

    [Theory]
    [InlineData("2026-05-25", "2026-11-25", 6)]
    [InlineData("2026-05-26", "2026-11-25", 5)]
    [InlineData("2026-11-01", "2026-11-25", 0)]
    [InlineData("2025-12-31", "2026-11-25", 10)]
    public void 旧了几个月(string date, string today, int months)
        => Assert.Equal(months, AdminHomeLogic.MonthsOld(DateOnly.Parse(date), DateOnly.Parse(today)));

    [Fact]
    public void 该更新的价格_同一物料看最新的那个价()
    {
        var today = new DateOnly(2026, 11, 25);
        var items = new (string, string?, string?, string?, DateOnly)[]
        {
            ("316L 板", "3mm", "板材", "甲", new DateOnly(2026, 2, 1)),
            ("316L 板", "3mm", "板材", "乙", new DateOnly(2026, 10, 1)),   // 有新价，这个物料不算旧
            ("C276 管", null, "管材", "甲", new DateOnly(2026, 2, 1)),
            ("钛材 TA2", null, "板材", "丙", new DateOnly(2025, 12, 1)),
        };
        var (n, top) = AdminHomeLogic.Stale(items, today, 1);
        Assert.Equal(2, n);
        Assert.Equal("钛材 TA2", top.Single().Name);   // 最旧的在前，只取 1 条
        Assert.Equal(11, top[0].Months);
    }

    [Fact]
    public void 价格日期_行上有日期用行上的()
    {
        Assert.Equal(new DateOnly(2026, 3, 1), AdminHomeLogic.PriceDateOf(new DateOnly(2026, 3, 1), new DateOnly(2026, 11, 1)));
        Assert.Equal(new DateOnly(2026, 11, 1), AdminHomeLogic.PriceDateOf(null, new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void 数回答里查了几次价格()
    {
        Assert.Equal(2, AdminHomeLogic.CountTool("""{"steps":[{"tool":"search_prices"},{"tool":"search_docs"},{"tool":"search_prices"}]}""", "search_prices"));
        Assert.Equal(0, AdminHomeLogic.CountTool("""{"steps":[{"tool":"search_docs"}]}""", "search_prices"));
        Assert.Equal(0, AdminHomeLogic.CountTool("search_prices 坏的 json", "search_prices"));
        Assert.Equal(0, AdminHomeLogic.CountTool(null, "search_prices"));
    }
}
