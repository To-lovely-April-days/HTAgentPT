using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>测试题打分：和 deploy/eval/run_eval.py 同一套算法，搬进后台。
/// 查得准 = 题目要求查到的资料/项目都出现在这次的依据里；答得全 = 每条要点（| 隔开的几种说法任一）都在回答里，且该问的问了。</summary>
public static class EvalScoring
{
    public record Card(string? Title, string? FileName, string? ProjectNo, bool Used);

    public record Score(bool? RetrievalOk, bool? AnswerOk, bool AskOk, List<string> MissingPoints, List<string> FoundDocs);

    private static readonly Regex Punct = new(@"[\s，,。．.、；;：:（）()\[\]「」“”""'·\-—_/]+", RegexOptions.Compiled);

    public static string Norm(string? s) => Punct.Replace((s ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant(), "");

    public static Score Grade(IReadOnlyList<string> expectDocs, IReadOnlyList<string> expectProjects, IReadOnlyList<string> expectPoints,
        bool expectAsk, string answer, IReadOnlyList<Card> cards, bool asked)
    {
        bool Found(string needle)
        {
            var n = Norm(needle);
            return n.Length > 0 && cards.Any(c => Norm(c.Title).Contains(n) || Norm(c.FileName).Contains(n) || Norm(c.ProjectNo) == n);
        }
        var targets = expectDocs.Concat(expectProjects).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        bool? retrieval = targets.Count == 0 ? null : targets.All(Found);

        var a = Norm(answer);
        var missing = expectPoints.Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(p => !p.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).Any(x => a.Contains(Norm(x))))
            .ToList();
        var askOk = asked == expectAsk;
        var hasPoints = expectPoints.Any(p => !string.IsNullOrWhiteSpace(p));
        bool? answerOk = hasPoints ? missing.Count == 0 && askOk : expectAsk ? askOk : null;

        var found = cards.Select(c => c.Title ?? c.FileName ?? c.ProjectNo).Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!).Distinct().Take(12).ToList();
        return new Score(retrieval, answerOk, askOk, missing, found);
    }
}

/// <summary>「系统看出来的」：按一条回答存下的记录核对出来的事实，不让模型猜原因。</summary>
public static class IssueFacts
{
    // 「0 个项目」「没有相关规范」「没读到内容」「代号不对」这类步骤结果都算没查到（和前端小人冒汗的判断一致）
    private static readonly Regex Empty = new(@"^(0\s|没|无|空)|不对", RegexOptions.Compiled);

    public static List<string> From(string? payloadJson, double threshold)
    {
        var facts = new List<string>();
        if (string.IsNullOrWhiteSpace(payloadJson)) return facts;
        JsonElement root;
        try { root = JsonDocument.Parse(payloadJson).RootElement; }
        catch (JsonException) { return facts; }
        if (root.ValueKind != JsonValueKind.Object) return facts;

        if (root.TryGetProperty("interrupted", out var intr) && intr.ValueKind == JsonValueKind.True)
            facts.Add("这条回答中途断了，没有答完。");

        if (root.TryGetProperty("searches", out var searches) && searches.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in searches.EnumerateArray())
            {
                var q = Str(s, "query");
                var found = Int(s, "found") ?? 0;
                var top = Dbl(s, "top");
                if (found == 0 && top is > 0)
                {
                    var gap = threshold - top.Value;
                    facts.Add(gap <= 0.1
                        ? $"搜「{q}」时最相关的一段 {F(top.Value)}，比「没找到」的门槛 {F(threshold)} 只低一点，被挡在外面了。"
                        : $"搜「{q}」时最相关的一段只有 {F(top.Value)}，门槛是 {F(threshold)}，资料里可能确实没有，或者叫法不一样。");
                }
                else if (found == 0)
                    facts.Add($"搜「{q}」一段都没搜到。");
            }
        }

        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var st in steps.EnumerateArray())
            {
                var label = Str(st, "label") ?? Str(st, "tool") ?? "一步";
                var result = Str(st, "result") ?? "";
                var detail = Str(st, "detail");
                if (Str(st, "status") == "error")
                    facts.Add($"「{label}」这一步出错了{(result.Length > 0 ? $"：{result}" : "")}。");
                else if (Empty.IsMatch(result) && !(Str(st, "tool") is "search_documents" or "search_standards"))
                    facts.Add($"「{label}」{(string.IsNullOrWhiteSpace(detail) ? "" : $"（{detail}）")}返回「{result}」。");
            }
        }

        if (root.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.Object)
        {
            var used = Count(ev, "used");
            var other = Count(ev, "other");
            if (used == 0 && other > 0) facts.Add($"搜到了 {other} 段资料，但回答一段都没引用。");
        }

        if (root.TryGetProperty("verify", out var vf) && vf.ValueKind == JsonValueKind.Object
            && vf.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in issues.EnumerateArray().Take(3))
                facts.Add($"回答里的「{Str(i, "value")}」在引用的原文里核对不上。");
        }
        return facts;
    }

    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static double? Dbl(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static int Count(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
}

/// <summary>两次跑分之间改了什么：比两份配置快照，写成人话，一行一项。</summary>
public static class ConfigDiff
{
    /// <summary>快照里的键 → 界面上的叫法。数字类的写增减，其余写「旧 → 新」。</summary>
    public static readonly (string Key, string Label, bool Count)[] Items =
    [
        ("retrieval.score_threshold", "「没找到」门槛", false),
        ("retrieval.recall_top_k", "召回条数", false),
        ("retrieval.rerank_top_n", "重排保留", false),
        ("assistant.search_top_k", "每次给模型几段", false),
        ("assistant.context_chars", "上下文字数", false),
        ("assistant.thinking", "先思考再查", false),
        ("assistant.max_rounds", "最多查几轮", false),
        ("model.chat.name", "对话模型", false),
        ("model.embedding.name", "向量模型", false),
        ("model.rerank.name", "重排模型", false),
        ("parser.backend", "解析方式", false),
        ("count.docs", "资料", true),
        ("count.aliases", "别名", true),
        ("count.terms", "术语", true),
        ("count.questions", "测试题", true),
    ];

    public static List<string> Describe(IReadOnlyDictionary<string, string>? prev, IReadOnlyDictionary<string, string> cur)
    {
        var lines = new List<string>();
        if (prev is null) return lines;
        foreach (var (key, label, count) in Items)
        {
            prev.TryGetValue(key, out var a);
            cur.TryGetValue(key, out var b);
            if ((a ?? "") == (b ?? "")) continue;
            if (count && int.TryParse(a, out var x) && int.TryParse(b, out var y))
                lines.Add($"{label} {(y >= x ? "+" : "")}{y - x}");
            else
                lines.Add($"{label} {(string.IsNullOrEmpty(a) ? "（默认）" : a)} → {(string.IsNullOrEmpty(b) ? "（默认）" : b)}");
        }
        return lines;
    }
}
