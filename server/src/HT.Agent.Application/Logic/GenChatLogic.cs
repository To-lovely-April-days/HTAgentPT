using System.Text.Json;
using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>对话式填槽的纯逻辑：指令识别、取值规整、模型抽取结果解析、模板推荐排序。
/// 与 IntentRouter 同一取舍——确定性、可测试；模型只负责「从自然语言里抽值」，
/// 抽出来的每个值都在这里过一遍校验，模型输出不直接落库。</summary>
public static class GenChatLogic
{
    public enum ChatCommand { None, Skip, Render, Summary }

    /// <summary>整句指令：跳过当前问题 / 要求生成 / 要求汇总。只认独立短句，
    /// 长句里出现这些词不算（「加热形式选电加热就能生成合格品」不是生成指令）。</summary>
    public static ChatCommand DetectCommand(string message)
    {
        var m = Regex.Replace(message.Trim(), @"[。！!，,~～\s]+$", "");
        if (m.Length is 0 or > 12) return ChatCommand.None;
        if (Regex.IsMatch(m, @"^(跳过|先跳过|跳过这[个一]?[项个]?|下一[个项]|留空|先不填|暂不填|不填这[个项])$"))
            return ChatCommand.Skip;
        if (Regex.IsMatch(m, @"^(生成|生成文档|生成word|开始生成|渲染|出文档|可以生成了?|生成吧)$", RegexOptions.IgnoreCase))
            return ChatCommand.Render;
        if (Regex.IsMatch(m, @"^(汇总|预览|看下进度|看看进度|进度|填了哪些)$"))
            return ChatCommand.Summary;
        return ChatCommand.None;
    }

    /// <summary>日期规整 → yyyy-MM-dd。认 2026-9-3 / 2026/9/3 / 2026.9.3 / 2026年9月3日 / 9月3日（就近取年）。</summary>
    public static string? NormalizeDate(string raw, int currentYear)
    {
        var s = raw.Trim();
        var m = Regex.Match(s, @"^(\d{4})\s*[-/.年]\s*(\d{1,2})\s*[-/.月]\s*(\d{1,2})\s*日?$");
        if (m.Success) return Compose(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        m = Regex.Match(s, @"^(\d{1,2})\s*月\s*(\d{1,2})\s*日?$");
        if (m.Success) return Compose(currentYear, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        return null;

        static string? Compose(int y, int mo, int d)
            => mo is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, mo) ? $"{y:D4}-{mo:D2}-{d:D2}" : null;
    }

    /// <summary>选择类槽位取值匹配：精确 → 去空白 → 包含（双向）→ 有/无同义。匹配不到返回 null（绝不硬塞）。</summary>
    public static string? MatchChoice(string raw, IReadOnlyList<string> choices)
    {
        var v = raw.Trim();
        if (v.Length == 0) return null;
        var exact = choices.FirstOrDefault(c => string.Equals(c, v, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        static string Squash(string s) => Regex.Replace(s, @"[\s（）()·、/－-]+", "");
        var sv = Squash(v);
        var squashed = choices.FirstOrDefault(c => string.Equals(Squash(c), sv, StringComparison.OrdinalIgnoreCase));
        if (squashed is not null) return squashed;
        // 包含匹配取最长命中，避免「油浴」同时命中两个油浴选项时取错
        var contains = choices.Where(c => sv.Contains(Squash(c), StringComparison.OrdinalIgnoreCase) ||
                                          Squash(c).Contains(sv, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Length).ToList();
        if (contains.Count == 1) return contains[0];
        if (Regex.IsMatch(v, @"^(无|没有|不要|不需要|不用)$") && choices.Contains("无")) return "无";
        if (Regex.IsMatch(v, @"^(有|要|需要)$") && choices.Contains("有")) return "有";
        return null;
    }

    /// <summary>解析模型的抽取回复：容忍代码围栏与前后废话，取第一段完整 JSON 对象里的 fills。
    /// 任何解析失败都返回空表——模型输出不可信时宁可少填。</summary>
    public static IReadOnlyList<(string Tag, string Value)> ParseModelFills(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return [];
        try
        {
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("fills", out var fills) || fills.ValueKind != JsonValueKind.Array)
                return [];
            var result = new List<(string, string)>();
            foreach (var f in fills.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("tag", out var tag)) continue;
                if (!f.TryGetProperty("value", out var value)) continue;
                var t = tag.GetString();
                var v = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    JsonValueKind.True => "是",
                    JsonValueKind.False => "否",
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(t) && !string.IsNullOrWhiteSpace(v))
                    result.Add((t!, v!.Trim()));
            }
            return result;
        }
        catch (JsonException) { return []; }
    }

    /// <summary>模板与提问的匹配打分（问答里推荐模板用）：模板名整段命中权重最高，
    /// 名称二字词滑窗命中累加，文档类别命中补一档。0 分即不相关。</summary>
    public static int TemplateScore(string question, string name, string docType)
    {
        var score = 0;
        var tokens = Regex.Split(name, @"[\s（）()【】\[\]·、,，_—-]+").Where(t => t.Length >= 2).ToList();
        foreach (var t in tokens)
        {
            if (question.Contains(t, StringComparison.OrdinalIgnoreCase)) { score += 4; continue; }
            var grams = new HashSet<string>();
            for (var i = 0; i + 2 <= t.Length; i++) grams.Add(t.Substring(i, 2));
            score += grams.Count(g => question.Contains(g, StringComparison.OrdinalIgnoreCase));
        }
        if (docType.Length >= 2 && question.Contains(docType, StringComparison.OrdinalIgnoreCase)) score += 3;
        return score;
    }
}
