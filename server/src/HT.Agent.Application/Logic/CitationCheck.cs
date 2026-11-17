using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HT.Agent.Application.Abstractions;

namespace HT.Agent.Application.Logic;

/// <summary>回答出来以后的通用核对：引用编号有没有对应的依据；带单位的数字是不是真出现在它引用的原文里
/// （或者是计算工具算出来的）。不针对任何具体问题，只看「说的数字有没有出处」。
/// 另外给每段被引用的原文挑出和回答最相关的句子，右侧高亮用。</summary>
public static class CitationCheck
{
    private static readonly Regex CiteGroup = new(@"\[(\d{1,3}(?:\s*[,，、]\s*\d{1,3})*)\]", RegexOptions.Compiled);

    /// <summary>回答里引用了哪些编号（[1]、[1][2]、[1,2] 都认）。</summary>
    public static IReadOnlyList<int> Cited(string answer)
        => CiteGroup.Matches(answer)
            .SelectMany(m => Regex.Split(m.Groups[1].Value, @"[,，、\s]+").Where(x => x.Length > 0).Select(int.Parse))
            .Distinct().ToList();

    public record Issue(string Value, IReadOnlyList<int> Refs, string Sentence);
    public record Report(int Checked, int Verified, IReadOnlyList<Issue> Issues, IReadOnlyList<int> MissingRefs);

    /// <summary>单位：拉丁字母/符号单位，加上常见中文单位。只核对带单位的数（「2 个项目」这类计数不核）。</summary>
    private const string Unit = @"(?:万元|元|万|天|周|个月|小时|分钟|吨|升|毫米|摄氏度|年|[A-Za-zμ℃°%‰²³][A-Za-z0-9μ℃°%‰²³/·.]*)";
    private static readonly Regex Range = new(@"(?<![\d.])(\d+(?:\.\d+)?)\s*[-–—~～至到]\s*(\d+(?:\.\d+)?)\s*(" + Unit + ")", RegexOptions.Compiled);
    private static readonly Regex Single = new(@"(?<![\d.])(\d+(?:\.\d+)?)\s*(" + Unit + ")", RegexOptions.Compiled);
    private static readonly Regex AnyNumber = new(@"(?<![\d.])\d+(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>核对。computed：计算工具算出来的数（以及它用到的数），这些算作有出处。</summary>
    public static Report Verify(string answer, IReadOnlyDictionary<int, EvidenceItem> evidence, IReadOnlyCollection<double> computed)
    {
        var cited = Cited(answer);
        var missing = cited.Where(n => !evidence.ContainsKey(n)).ToList();
        var issues = new List<Issue>();
        int checkedN = 0, verified = 0;

        foreach (var sentence in Sentences(answer))
        {
            var refs = Cited(sentence).Where(evidence.ContainsKey).ToList();
            if (refs.Count == 0) continue; // 没引用的句子不核（通用知识、追问清单等）
            var body = CiteGroup.Replace(sentence, " ");
            body = Regex.Replace(body, @"^\s*(?:[-*+]|\d+[.、)])\s+", ""); // 列表序号不算数字
            var pool = new HashSet<double>(refs.SelectMany(n => Numbers(evidence[n].Text)));
            foreach (var c in computed) pool.Add(Math.Round(c, 6));

            var taken = new List<(int Start, int End)>();
            foreach (Match m in Range.Matches(body))
            {
                taken.Add((m.Index, m.Index + m.Length));
                var a = Parse(m.Groups[1].Value); var b = Parse(m.Groups[2].Value);
                checkedN++;
                if (pool.Contains(a) && pool.Contains(b)) verified++;
                else issues.Add(new Issue(m.Value.Trim(), refs, Clip(sentence)));
            }
            foreach (Match m in Single.Matches(body))
            {
                if (taken.Any(t => m.Index >= t.Start && m.Index < t.End)) continue;
                var unit = m.Groups[2].Value;
                var v = Parse(m.Groups[1].Value);
                if (unit == "年" && (v < 1990 || v > 2100)) continue; // 「3 年」这种期限照常核，「2026 年」也核；其余数字+年不当年份
                checkedN++;
                if (pool.Contains(v)) verified++;
                else issues.Add(new Issue(m.Value.Trim(), refs, Clip(sentence)));
            }
        }
        return new Report(checkedN, verified, issues, missing);
    }

    /// <summary>一段原文里所有数字（全角转半角、去千分位）。</summary>
    public static IEnumerable<double> Numbers(string text)
        => AnyNumber.Matches(NormalizeDigits(text)).Select(m => Parse(m.Value));

    private static double Parse(string s) => Math.Round(double.Parse(s, CultureInfo.InvariantCulture), 6);

    private static string NormalizeDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch is >= '０' and <= '９' ? (char)('0' + (ch - '０')) : ch == '．' ? '.' : ch);
        return Regex.Replace(sb.ToString(), @"(?<=\d),(?=\d{3}(?!\d))", "");
    }

    /// <summary>按句切：句号、问号、分号、换行；引用编号跟在句末标点后面时归前一句。</summary>
    public static IEnumerable<string> Sentences(string text)
    {
        var parts = Regex.Split(NormalizeDigits(text), @"(?<=[。！？；;!?\n](?:\s*\[[\d,，、\s]+\])*)(?!\s*\[)");
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0);
    }

    private static string Clip(string s) { s = s.Trim(); return s.Length <= 80 ? s : s[..80] + "…"; }

    // ── 原文高亮 ─────────────────────────────────────────
    public record Span(int Start, int Length);

    /// <summary>在一段被引用的原文里，挑出和引用它的回答句子最相关的 1–2 句（数字吻合优先，再看字面重合）。</summary>
    public static IReadOnlyList<Span> Highlights(string chunkText, IEnumerable<string> citingSentences, int max = 2)
    {
        var answerSide = string.Join("\n", citingSentences);
        if (answerSide.Length == 0 || chunkText.Length == 0) return [];
        var answerNums = new HashSet<double>(Numbers(CiteGroup.Replace(answerSide, " ")));
        var answerGrams = Bigrams(answerSide);

        var segs = new List<(int Start, int Len, double Score)>();
        foreach (Match m in Regex.Matches(chunkText, @"[^。！？；;\n|]+[。！？；;]?"))
        {
            var seg = m.Value;
            if (seg.Trim().Length < 4) continue;
            var nums = Numbers(seg).Count(answerNums.Contains);
            var grams = Bigrams(seg);
            var overlap = grams.Count == 0 ? 0 : grams.Count(answerGrams.Contains) / (double)grams.Count;
            var score = nums * 3 + overlap * 2;
            if (nums == 0 && overlap < 0.25) continue;
            var lead = seg.Length - seg.TrimStart().Length;
            segs.Add((m.Index + lead, seg.Trim().Length, score));
        }
        return segs.OrderByDescending(s => s.Score).Take(max).OrderBy(s => s.Start)
            .Select(s => new Span(s.Start, s.Len)).ToList();
    }

    private static HashSet<string> Bigrams(string s)
    {
        var t = Regex.Replace(s, @"\s+|\[\d+\]", "");
        var set = new HashSet<string>();
        for (var i = 0; i + 1 < t.Length; i++)
            if (!char.IsPunctuation(t[i]) && !char.IsPunctuation(t[i + 1])) set.Add(t.Substring(i, 2));
        return set;
    }

    /// <summary>回答里引用了 n 的句子。</summary>
    public static IReadOnlyList<string> SentencesCiting(string answer, int n)
        => Sentences(answer).Where(s => Cited(s).Contains(n)).ToList();
}
