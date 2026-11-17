using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>使用统计按天汇总（统计页的数据源）：每天各功能的调用、本期与上期的汇总、按人员、一年活跃热力图。
/// 数据源是审计日志——每次调用都已经记了，这里只换个切面读。
///
/// 口径：
/// - 一次提问算一次（多 Agent 问答记 qa.multi_agent.start；旧链路记 qa.ask），台账类提问归「项目查询」；
/// - 命中率只看知识问答：回答了 / (回答了 + 如实说没找到)；
/// - 「没答上来」= 知识问答里如实说没找到的次数——这是资料缺口，不是故障；
/// - 日期按调用方时区切（中国是 +8），不然晚上八点后的调用会算到第二天。</summary>
public static class UsageDaily
{
    public record Event(DateTimeOffset At, string Action, Guid? UserId, string? Detail);

    public record Person(Guid UserId, string Name, string Role);

    /// <summary>动作 → 功能（界面文案不出现技术名称）。顺序有意义：先匹配的先算。</summary>
    private static readonly (string Prefix, string Module)[] Map =
    [
        ("qa.ask", "检索问答"), ("retrieve.query", "检索问答"),
        ("project.search", "项目查询"), ("project.get", "项目查询"),
        ("gen.chat", "出文档"), ("gen.render", "出文档"), ("clause.assemble", "出文档"),
        ("translate.", "翻译"), ("case.", "故障案例"), ("ticket.", "报修工单"),
    ];

    public static readonly string[] Modules = ["检索问答", "项目查询", "出文档", "故障案例", "翻译", "报修工单"];

    private static readonly Regex Answered = new(@"""answered""\s*:\s*(true|false)", RegexOptions.Compiled);
    private static readonly Regex IntentKnowledge = new(@"""intent""\s*:\s*""knowledge""", RegexOptions.Compiled);
    private static readonly Regex IntentLedger = new(@"""intent""\s*:\s*""ledger""", RegexOptions.Compiled);

    /// <summary>这条审计算哪个功能；不算调用的（登录、管理操作、问答收尾记录等）返回 null。</summary>
    public static string? ModuleOf(Event e)
    {
        if (e.Action == "qa.multi_agent.start")
            return e.Detail is not null && IntentLedger.IsMatch(e.Detail) ? "项目查询" : "检索问答";
        foreach (var (prefix, module) in Map)
            if (e.Action.StartsWith(prefix, StringComparison.Ordinal)) return module;
        return null;
    }

    /// <summary>知识问答的结果：true=答上了，false=如实说没找到，null=不是知识问答的结果记录。</summary>
    public static bool? AnsweredOf(Event e)
    {
        if (e.Detail is null) return null;
        var isKnowledgeResult = e.Action == "qa.ask"
            || (e.Action == "qa.multi_agent.complete" && IntentKnowledge.IsMatch(e.Detail));
        if (!isKnowledgeResult) return null;
        var m = Answered.Match(e.Detail);
        return m.Success ? m.Groups[1].Value == "true" : null;
    }

    /// <summary>出文档记录里的模板名（审计明细是默认序列化：中文被转义成 \uXXXX，所以要按 JSON 解析，不能正则抠）。</summary>
    public static string? TemplateOf(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(detail);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("Name", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return p.Value.GetString();
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    public record DayRow(string Date, int Total, Dictionary<string, int> Modules);
    public record ModuleRow(string Name, int N, int Prev);
    public record PersonRow(Guid UserId, string Name, string Role, int N, Dictionary<string, int> Modules, DateTimeOffset LastAt);
    public record Totals(int Calls, int PrevCalls, int ActiveUsers, int PrevActiveUsers, int TotalUsers,
        double? HitRate, double? PrevHitRate, int NoResult, int PrevNoResult, int Docs, int PrevDocs,
        Dictionary<string, int> DocsByTemplate);
    public record YearDay(string Date, int N);
    public record Year(IReadOnlyList<YearDay> Days, int Total, int ActiveDays, int LongestStreak, int? BusiestWeekday);
    public record Result(string From, string To, IReadOnlyList<DayRow> Days, Totals Totals,
        IReadOnlyList<ModuleRow> ModuleRows, IReadOnlyList<PersonRow> People, Year? Year);

    /// <summary>汇总。today 为调用方时区的今天；本期 = 含今天在内的最近 days 天，上期 = 再往前同样天数。
    /// yearEvents 为空就不算热力图（只在需要时多读一年的日志）。</summary>
    public static Result Build(IReadOnlyList<Event> events, DateOnly today, int days, TimeSpan offset,
        IReadOnlyDictionary<Guid, Person> people, int totalUsers, IReadOnlyList<Event>? yearEvents = null)
    {
        var from = today.AddDays(-(days - 1));
        var prevFrom = from.AddDays(-days);
        DateOnly Local(DateTimeOffset t) => DateOnly.FromDateTime(t.ToOffset(offset).DateTime);

        var cur = events.Where(e => Local(e.At) >= from && Local(e.At) <= today).ToList();
        var prev = events.Where(e => Local(e.At) >= prevFrom && Local(e.At) < from).ToList();

        var dayRows = new List<DayRow>();
        var byDay = cur.Select(e => (Day: Local(e.At), Module: ModuleOf(e))).Where(x => x.Module is not null)
            .GroupBy(x => x.Day).ToDictionary(g => g.Key, g => g.GroupBy(x => x.Module!).ToDictionary(m => m.Key, m => m.Count()));
        for (var d = from; d <= today; d = d.AddDays(1))
        {
            var mods = byDay.GetValueOrDefault(d) ?? [];
            dayRows.Add(new DayRow(d.ToString("yyyy-MM-dd"), mods.Values.Sum(), mods));
        }

        static int Calls(IEnumerable<Event> xs) => xs.Count(e => ModuleOf(e) is not null);
        static int Users(IEnumerable<Event> xs) => xs.Where(e => ModuleOf(e) is not null && e.UserId is not null).Select(e => e.UserId).Distinct().Count();
        static (double? Rate, int No) Hit(IEnumerable<Event> xs)
        {
            var results = xs.Select(AnsweredOf).Where(a => a is not null).ToList();
            var yes = results.Count(a => a == true);
            var no = results.Count - yes;
            return (results.Count == 0 ? null : Math.Round(100.0 * yes / results.Count, 1), no);
        }
        static List<Event> Renders(IEnumerable<Event> xs) => xs.Where(e => e.Action == "gen.render").ToList();

        var (hit, noRes) = Hit(cur);
        var (prevHit, prevNoRes) = Hit(prev);
        var renders = Renders(cur);
        var byTemplate = renders.Select(e => TemplateOf(e.Detail) ?? "其他")
            .GroupBy(x => x).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count());

        var totals = new Totals(Calls(cur), Calls(prev), Users(cur), Users(prev), totalUsers,
            hit, prevHit, noRes, prevNoRes, renders.Count, Renders(prev).Count, byTemplate);

        var curMods = cur.Select(ModuleOf).Where(m => m is not null).GroupBy(m => m!).ToDictionary(g => g.Key, g => g.Count());
        var prevMods = prev.Select(ModuleOf).Where(m => m is not null).GroupBy(m => m!).ToDictionary(g => g.Key, g => g.Count());
        var moduleRows = Modules.Select(m => new ModuleRow(m, curMods.GetValueOrDefault(m), prevMods.GetValueOrDefault(m)))
            .Where(r => r.N > 0 || r.Prev > 0).OrderByDescending(r => r.N).ToList();

        var personRows = cur.Where(e => e.UserId is not null && ModuleOf(e) is not null)
            .GroupBy(e => e.UserId!.Value)
            .Select(g =>
            {
                var p = people.GetValueOrDefault(g.Key);
                return new PersonRow(g.Key, p?.Name ?? "已删除账号", p?.Role ?? "", g.Count(),
                    g.GroupBy(e => ModuleOf(e)!).ToDictionary(m => m.Key, m => m.Count()), g.Max(e => e.At));
            })
            .OrderByDescending(r => r.N).Take(20).ToList();

        Year? year = null;
        if (yearEvents is not null)
        {
            var yFrom = today.AddDays(-364);
            var counts = yearEvents.Where(e => ModuleOf(e) is not null).Select(e => Local(e.At))
                .Where(d => d >= yFrom && d <= today).GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
            var list = new List<YearDay>();
            int streak = 0, longest = 0;
            for (var d = yFrom; d <= today; d = d.AddDays(1))
            {
                var n = counts.GetValueOrDefault(d);
                list.Add(new YearDay(d.ToString("yyyy-MM-dd"), n));
                streak = n > 0 ? streak + 1 : 0;
                longest = Math.Max(longest, streak);
            }
            int? busiest = counts.Count == 0 ? null
                : counts.GroupBy(kv => (int)kv.Key.DayOfWeek).OrderByDescending(g => g.Sum(kv => kv.Value)).First().Key;
            year = new Year(list, counts.Values.Sum(), counts.Count, longest, busiest);
        }

        return new Result(from.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"), dayRows, totals, moduleRows, personRows, year);
    }
}
