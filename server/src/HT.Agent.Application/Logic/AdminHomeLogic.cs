using System.Text.Json;

namespace HT.Agent.Application.Logic;

/// <summary>后台首页按角色拼块用到的纯逻辑：各部门用量、本部门每个人用了多少、价格多久没更新、助手查了几次价格。
/// 取数在 AdminHomeService；这里只算，方便测。</summary>
public static class AdminHomeLogic
{
    /// <summary>价格超过这么多天没更新，算「该更新」。</summary>
    public const int StaleDays = 180;

    /// <summary>没分部门的人归到这一组。</summary>
    public const string NoDept = "未分配部门";

    /// <summary>一次调用：谁、什么时候、算哪个功能（UsageDaily.ModuleOf 认出来的）。</summary>
    public record Call(Guid UserId, DateTimeOffset At, string Module);

    public record DeptUsage(string Name, int Members, int Active, int Calls, int PrevCalls);

    /// <summary>各部门：人数（在用账号）、本期用过的人、本期调用、上期调用。按本期调用从多到少。
    /// 本期 = [from, 现在)，上期 = [prevFrom, from)。</summary>
    public static IReadOnlyList<DeptUsage> Departments(IEnumerable<(Guid UserId, Guid? DeptId)> users, IReadOnlyDictionary<Guid, string> deptNames,
        IEnumerable<Call> calls, DateTimeOffset from, DateTimeOffset prevFrom)
    {
        var deptOf = users.ToDictionary(u => u.UserId, u => u.DeptId is { } d && deptNames.TryGetValue(d, out var n) ? n : NoDept);
        var cur = new Dictionary<string, int>(); var prev = new Dictionary<string, int>(); var active = new Dictionary<string, HashSet<Guid>>();
        foreach (var c in calls)
        {
            if (!deptOf.TryGetValue(c.UserId, out var dept)) continue;   // 已删除或不在名单里的账号不算
            if (c.At >= from)
            {
                cur[dept] = cur.GetValueOrDefault(dept) + 1;
                (active.TryGetValue(dept, out var s) ? s : active[dept] = []).Add(c.UserId);
            }
            else if (c.At >= prevFrom) prev[dept] = prev.GetValueOrDefault(dept) + 1;
        }
        return deptOf.GroupBy(kv => kv.Value)
            .Select(g => new DeptUsage(g.Key, g.Count(), active.TryGetValue(g.Key, out var s) ? s.Count : 0, cur.GetValueOrDefault(g.Key), prev.GetValueOrDefault(g.Key)))
            .OrderByDescending(r => r.Calls).ThenByDescending(r => r.Members).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    public record PersonUsage(Guid UserId, string Name, int Calls, DateTimeOffset? LastAt);

    /// <summary>本部门每个人：本期调用次数、最近一次用（所有时间里最近的一次）。用得多的在前，没用过的排最后。</summary>
    public static IReadOnlyList<PersonUsage> People(IEnumerable<(Guid UserId, string Name)> users, IEnumerable<Call> calls, DateTimeOffset from)
    {
        var list = calls.ToList();
        return users.Select(u =>
            {
                var mine = list.Where(c => c.UserId == u.UserId).ToList();
                return new PersonUsage(u.UserId, u.Name, mine.Count(c => c.At >= from), mine.Count == 0 ? null : mine.Max(c => c.At));
            })
            .OrderByDescending(p => p.Calls).ThenByDescending(p => p.LastAt).ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>本期按功能分：调用次数从多到少。</summary>
    public static IReadOnlyList<(string Module, int N)> Modules(IEnumerable<Call> calls, DateTimeOffset from)
        => calls.Where(c => c.At >= from).GroupBy(c => c.Module).Select(g => (g.Key, g.Count()))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();

    /// <summary>价格的日期：采购记录里每行自己的日期优先，没有就用这版价格表的日期。</summary>
    public static DateOnly PriceDateOf(DateOnly? itemDate, DateOnly versionDate) => itemDate ?? versionDate;

    public static bool IsStale(DateOnly date, DateOnly today) => today.DayNumber - date.DayNumber > StaleDays;

    /// <summary>旧了几个月（不满一个月算 0）。</summary>
    public static int MonthsOld(DateOnly date, DateOnly today)
    {
        var m = (today.Year - date.Year) * 12 + today.Month - date.Month;
        if (today.Day < date.Day) m--;
        return Math.Max(0, m);
    }

    /// <summary>一条回答里调了几次某个工具（数 payload 里 steps 的 tool；坏的、没有的当 0）。</summary>
    public static int CountTool(string? payload, string tool)
    {
        if (string.IsNullOrEmpty(payload) || !payload.Contains(tool, StringComparison.Ordinal)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return 0;
            var n = 0;
            foreach (var s in steps.EnumerateArray())
                if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == tool) n++;
            return n;
        }
        catch (JsonException) { return 0; }
    }

    public record StaleItem(string Name, string? Spec, string? Group, string? Supplier, DateOnly Date, int Months);

    /// <summary>该更新的价格：同一个物料（名称 + 规格）只看它最新的那个价；最新的也旧了才算。最旧的在前。</summary>
    public static (int Count, IReadOnlyList<StaleItem> Top) Stale(IEnumerable<(string Name, string? Spec, string? Group, string? Supplier, DateOnly Date)> items, DateOnly today, int take)
    {
        var latest = items.GroupBy(i => (i.Name.Trim(), (i.Spec ?? "").Trim()))
            .Select(g => g.OrderByDescending(i => i.Date).First())
            .Where(i => IsStale(i.Date, today))
            .OrderBy(i => i.Date).ThenBy(i => i.Name, StringComparer.Ordinal)
            .ToList();
        return (latest.Count, latest.Take(take).Select(i => new StaleItem(i.Name, i.Spec, i.Group, i.Supplier, i.Date, MonthsOld(i.Date, today))).ToList());
    }
}
