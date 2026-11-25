using System.Text.Json;

namespace HT.Agent.Application.Logic;

/// <summary>首页周五的「这周我做成了」：只数系统里真记下的（问了几个问题、做好几份文件、查了几次资料、哪个对话来回最多），
/// 不估「省了多少小时」这种算不准的数。只给本人看，不排名。这里是纯逻辑：哪天出、一周从哪天算、回答里查了几次资料。</summary>
public static class WeekRecap
{
    /// <summary>这一周从周一 0 点算（北京时间）。</summary>
    public static DateTimeOffset WeekStart(DateTimeOffset now)
    {
        var back = ((int)now.DayOfWeek + 6) % 7;   // 周一 0、周日 6
        var d = now.Date.AddDays(-back);
        return new DateTimeOffset(d, now.Offset);
    }

    /// <summary>周五下午 2 点以后到周日都出；下周一自动没了。</summary>
    public static bool ShowOn(DateTimeOffset now)
        => now.DayOfWeek switch { DayOfWeek.Friday => now.Hour >= 14, DayOfWeek.Saturday or DayOfWeek.Sunday => true, _ => false };

    /// <summary>问题太少（不到 3 个）这周就不出回顾，免得一张卡上全是 0 和 1。</summary>
    public const int MinQuestions = 3;

    /// <summary>算「查资料」的工具：公司资料、标准、文献、价格、台账、联网搜索和读网页。</summary>
    public static bool IsLookup(string? tool)
        => tool is not null && (tool.StartsWith("search_", StringComparison.Ordinal) || tool is "fetch_url" or "query_projects" or "read_document");

    /// <summary>一条回答里查了几次资料（数 payload 里 steps 的工具名；坏的、没有的当 0）。</summary>
    public static int Lookups(string? payload)
    {
        if (string.IsNullOrEmpty(payload) || !payload.Contains("\"steps\"", StringComparison.Ordinal)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return 0;
            var n = 0;
            foreach (var s in steps.EnumerateArray())
                if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String && IsLookup(t.GetString())) n++;
            return n;
        }
        catch (JsonException) { return 0; }
    }

    /// <summary>这周来回最多的那个对话（至少来回 3 轮才算「费劲」）。</summary>
    public record Hardest(string Title, int Rounds, int Files);

    public static Hardest? HardestOf(IEnumerable<(string Title, int Rounds, int Files)> sessions)
        => sessions.Where(s => s.Rounds >= 3).OrderByDescending(s => s.Rounds).ThenByDescending(s => s.Files)
            .Select(s => new Hardest(s.Title, s.Rounds, s.Files)).FirstOrDefault();
}
