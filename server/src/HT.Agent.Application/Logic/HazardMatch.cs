using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>危险工艺、危险化学品的查找规则（纯函数，方便测）。
/// 工艺：用户说的工艺名、反应类型、典型工艺 → 对上重点监管工艺目录里的哪几类；
/// 化学品：只认 CAS 号、品名、别名、英文名的精确对应（去掉[含量]这类限定后），不做模糊猜测——对不上就说对不上。</summary>
public static partial class HazardMatch
{
    public record ProcessRow(Guid Id, int No, string Name, string Typical, string Intro);
    public record ChemRow(Guid Id, int Seq, bool Sub, string Name, string Aliases, string? English, string? Cas);
    public record ControlRow(string List, string No, string Name, string? Cas, string? Category);

    [GeneratedRegex(@"^\d{2,7}-\d{2}-\d$")]
    private static partial Regex CasRx();
    [GeneratedRegex(@"[\[［(（].*?[\]］)）]")]
    private static partial Regex QualRx();
    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRx();

    public static bool IsCas(string s) => CasRx().IsMatch(s.Trim());

    /// <summary>去掉空白、全角半角统一、去掉方括号/括号里的限定（如 [无水]、[含量＞20%]）。</summary>
    public static string Norm(string s) => QualRx().Replace(SpaceRx().Replace(s.Normalize(System.Text.NormalizationForm.FormKC), ""), "").ToLowerInvariant();

    /// <summary>工艺名里能直接拿来对照的关键词：「裂解（裂化）工艺」→ 裂解、裂化；「光气及光气化工艺」→ 光气。另加几个常见说法。</summary>
    public static IReadOnlyList<string> Keywords(string name)
    {
        var n = name.Replace("工艺", "");
        var list = new List<string>();
        var m = Regex.Match(n, @"^(.*?)（(.*?)）$");
        if (m.Success) { list.Add(m.Groups[1].Value); list.Add(m.Groups[2].Value); }
        else list.Add(n);
        if (n.StartsWith("光气")) list.Add("光气");
        if (n.Contains("电石")) list.Add("电石");
        if (n.Contains("煤化工")) { list.Add("煤化工"); list.Add("煤制"); }
        if (n == "加氢") { list.Add("氢化"); }
        if (n == "胺基化") { list.Add("氨基化"); list.Add("胺化"); list.Add("氨化"); }
        if (n == "合成氨") list.Add("合成氨");
        return list.Where(x => x.Length >= 2).Distinct().ToList();
    }

    static HashSet<string> Bigrams(string s)
    {
        var t = Norm(s);
        var set = new HashSet<string>();
        for (var i = 0; i + 1 < t.Length; i++) set.Add(t.Substring(i, 2));
        return set;
    }

    /// <summary>对上重点监管工艺目录里的哪几类：工艺名关键词出现在问题里的优先（关键词越长越靠前）；
    /// 都没有就看问题和「典型工艺」的字面重合，够多才算。</summary>
    public static List<ProcessRow> MatchProcesses(string query, IEnumerable<ProcessRow> rows, int take = 2)
    {
        var q = Norm(query);
        if (q.Length == 0) return [];
        var all = rows.ToList();
        var byName = all.Select(r => (r, k: Keywords(r.Name).Where(k => q.Contains(Norm(k))).Select(k => k.Length).DefaultIfEmpty(0).Max()))
            .Where(x => x.k > 0).OrderByDescending(x => x.k).ThenBy(x => x.r.No).Select(x => x.r).ToList();
        if (byName.Count > 0) return byName.Take(take).ToList();
        var qb = Bigrams(query);
        if (qb.Count < 2) return [];
        return all.Select(r => (r, s: Bigrams(r.Typical + r.Intro).Count(qb.Contains) / (double)qb.Count))
            .Where(x => x.s >= 0.5).OrderByDescending(x => x.s).Select(x => x.r).Take(take).ToList();
    }

    /// <summary>名称（或 CAS）对上目录里的哪几条。CAS 一样就是同一种（实施指南第二条）；名称要和品名或别名完全一样（去掉限定后）。
    /// 「某某气」对不上时再试去掉「气」（氧气 → 氧[压缩的或液化的]）。</summary>
    public static List<ChemRow> FindChemical(string query, IReadOnlyList<ChemRow> rows)
    {
        var q = query.Trim();
        if (q.Length == 0) return [];
        if (IsCas(q)) return rows.Where(r => r.Cas == q).ToList();
        foreach (var cand in q.EndsWith('气') && q.Length > 1 ? new[] { q, q[..^1] } : [q])
        {
            var n = Norm(cand);
            if (n.Length == 0) continue;
            var hit = rows.Where(r => Norm(r.Name) == n || r.Aliases.Split('；', StringSplitOptions.RemoveEmptyEntries).Any(a => Norm(a) == n)).ToList();
            if (hit.Count == 0)
                hit = rows.Where(r => r.English is not null && r.English.Split(';').Any(e => Norm(e) == n)).ToList();
            if (hit.Count > 0)
            {
                // 对上主条目：带上同序号的细分条目（不同浓度、溶液），太多（类属条目）就不带；
                // 对上细分条目：带上它的主条目
                var mains = hit.Where(h => !h.Sub).Select(h => h.Seq).ToHashSet();
                var subsOf = hit.Where(h => h.Sub).Select(h => h.Seq).ToHashSet();
                var subCount = rows.Where(r => r.Sub && mains.Contains(r.Seq)).GroupBy(r => r.Seq).ToDictionary(g => g.Key, g => g.Count());
                return rows.Where(r => hit.Contains(r)
                        || (r.Sub && mains.Contains(r.Seq) && subCount.GetValueOrDefault(r.Seq) <= 6)
                        || (!r.Sub && subsOf.Contains(r.Seq)))
                    .OrderBy(r => r.Seq).ThenBy(r => r.Sub).ToList();
            }
        }
        return [];
    }

    /// <summary>对不上时给几个名字相近的品名，让人（或模型）换个说法再查；不当成结论。</summary>
    public static List<ChemRow> Suggest(string query, IReadOnlyList<ChemRow> rows, int take = 5)
    {
        var n = Norm(query);
        if (n.Length < 2) return [];
        return rows.Where(r => !r.Sub && (Norm(r.Name).Contains(n) || r.Aliases.Split('；').Any(a => Norm(a).Contains(n))))
            .OrderBy(r => r.Name.Length).Take(take).ToList();
    }

    public static List<ControlRow> FindControlled(string? cas, IEnumerable<string> names, IEnumerable<ControlRow> rows)
    {
        var ns = names.Select(Norm).Where(x => x.Length > 0).ToHashSet();
        return rows.Where(r => (cas is not null && r.Cas == cas) || ns.Contains(Norm(r.Name))).ToList();
    }
}
