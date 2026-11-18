using System.Text;
using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>公司词表里的一个说法：标准说法 + 别名（客户简称、内部叫法、手误、英文…）。</summary>
public record TermEntry(string Kind, string Value, IReadOnlyList<string> Aliases);

/// <summary>帮助模型听懂问题的提示：问题里出现的别名、客户简称、口语单位，对到公司的标准说法。
/// 只给「可能是什么」的提示，最后怎么理解由模型结合上下文判断；拿不准的由它去问。
/// 全部来自词表数据，不针对具体业务写规则。</summary>
public static class QuestionHints
{
    public static IReadOnlyList<string> SplitAliases(string? aliases)
        => (aliases ?? "").Split(['、', ',', '，', ';', '；', '/', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => a.Length > 0).Distinct().ToList();

    private static string N(string s) => (s ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    private static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;

    /// <summary>别名命中：问题里写的是别名、没写标准说法。</summary>
    public static List<(string Said, TermEntry Term)> AliasHits(string question, IEnumerable<TermEntry> terms)
    {
        var q = N(question);
        var hits = new List<(string, TermEntry)>();
        foreach (var t in terms)
        {
            if (q.Contains(N(t.Value))) continue;
            foreach (var a in t.Aliases)
            {
                if (a.Length < 2) continue;
                if (!q.Contains(N(a))) continue;
                // 英文、型号类别名要整词出现，免得 "PP" 命中 "PPL"
                if (a.Any(char.IsAsciiLetter) && !Regex.IsMatch(q, $@"(?<![a-z0-9]){Regex.Escape(N(a))}(?![a-z0-9])")) continue;
                hits.Add((a, t));
                break;
            }
        }
        // 同一个说法对到多个标准说法，都保留，让模型判断
        return hits;
    }

    /// <summary>客户名称的两字简称（「华东理工大学」→「华理」「华工」…）：首字 + 后面任意一字，且不是原名里连着的两个字。
    /// 只在问题里没写全称、也没写已登记别名时才提示，并且可能对到多家时全列出来。</summary>
    public static List<(string Said, List<string> Names)> Abbreviations(string question, IEnumerable<TermEntry> customers)
    {
        var q = N(question);
        var map = new Dictionary<string, List<string>>();
        foreach (var c in customers)
        {
            var name = c.Value.Trim();
            if (name.Length < 4 || !IsCjk(name[0]) || q.Contains(N(name)) || c.Aliases.Any(a => q.Contains(N(a)))) continue;
            for (var j = 2; j < name.Length; j++)
            {
                if (!IsCjk(name[j])) continue;
                var ab = $"{name[0]}{name[j]}";
                if (name.Contains(ab) || !q.Contains(ab)) continue;
                if (!map.TryGetValue(ab, out var list)) map[ab] = list = [];
                if (!list.Contains(name)) list.Add(name);
            }
        }
        return map.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static readonly Regex KgfRe = new(@"(\d+(?:\.\d+)?)\s*(?:个)?\s*公斤(?!重)", RegexOptions.Compiled);

    /// <summary>口语里的压力单位：「16 公斤」在说压力时一般指 16 kgf/cm²。</summary>
    public static List<string> UnitHints(string question)
    {
        var list = new List<string>();
        foreach (Match m in KgfRe.Matches(question ?? ""))
        {
            if (!double.TryParse(m.Groups[1].Value, out var v)) continue;
            var mpa = v * 0.0980665;
            list.Add($"「{m.Value.Trim()}」如果说的是压力，一般指 {v:0.##} kgf/cm² ≈ {mpa:0.##} MPa（表压）；如果说的是重量，就是 {v:0.##} kg");
        }
        return list;
    }

    /// <summary>拼成给模型看的几行提示；没有就返回空。</summary>
    public static string Build(string question, IReadOnlyList<TermEntry> terms)
    {
        var lines = new List<string>();
        foreach (var (said, t) in AliasHits(question, terms).Take(12))
            lines.Add($"「{said}」= {t.Value}（{t.Kind}）");
        foreach (var (said, names) in Abbreviations(question, terms.Where(t => t.Kind == "客户")).Take(6))
            lines.Add(names.Count == 1
                ? $"「{said}」可能是客户「{names[0]}」的简称"
                : $"「{said}」可能是这几家客户之一：{string.Join("、", names.Take(5))}（从上下文判断不了就问用户）");
        lines.AddRange(UnitHints(question).Take(3));
        return lines.Count == 0 ? "" : string.Join("\n", lines.Select(l => "- " + l));
    }

    /// <summary>检索时把别名对到的标准说法并进搜索词，资料里写的是标准说法也能查到。</summary>
    public static string Expand(string query, IReadOnlyList<TermEntry> terms)
    {
        var add = AliasHits(query, terms).Select(h => h.Term.Value)
            .Concat(Abbreviations(query, terms.Where(t => t.Kind == "客户")).Where(a => a.Names.Count == 1).Select(a => a.Names[0]))
            .Distinct().Take(6).ToList();
        return add.Count == 0 ? query : query + " " + string.Join(' ', add);
    }
}
