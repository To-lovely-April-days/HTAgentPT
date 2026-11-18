using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>Word 的自动编号（「3.2」「第一条」「一、」「①」、表格里的序号）不在正文文字里，
/// 是排版时按编号定义算出来的。这里按文档顺序把它算出来，补回到段落前面——
/// 否则条款号、序号全丢，「第 3.2 条怎么约定」就对不上。</summary>
public sealed class WordNumbering
{
    private sealed record Lvl(int Start, string Fmt, string Text, bool Legal);

    private readonly Dictionary<int, int> _numToAbs = new();
    private readonly Dictionary<int, Dictionary<int, Lvl>> _abs = new();
    private readonly Dictionary<int, Dictionary<int, int>> _startOverride = new();
    private readonly Dictionary<string, (int NumId, int Ilvl)> _styleNum = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int?[]> _counters = new();

    public WordNumbering(MainDocumentPart main)
    {
        var numbering = main.NumberingDefinitionsPart?.Numbering;
        if (numbering is not null)
        {
            foreach (var a in numbering.Elements<AbstractNum>())
            {
                if (a.AbstractNumberId?.Value is not { } aid) continue;
                var levels = new Dictionary<int, Lvl>();
                foreach (var l in a.Elements<Level>())
                {
                    if (l.LevelIndex?.Value is not { } li) continue;
                    levels[li] = new Lvl(l.StartNumberingValue?.Val?.Value ?? 1, l.NumberingFormat?.Val?.InnerText ?? "decimal", l.LevelText?.Val?.Value ?? "",
                        l.IsLegalNumberingStyle is { } lg && (lg.Val is null || lg.Val.Value));
                }
                _abs[aid] = levels;
            }
            foreach (var n in numbering.Elements<NumberingInstance>())
            {
                if (n.NumberID?.Value is not { } nid || n.AbstractNumId?.Val?.Value is not { } aid) continue;
                _numToAbs[nid] = aid;
                foreach (var o in n.Elements<LevelOverride>())
                    if (o.LevelIndex?.Value is { } li && o.StartOverrideNumberingValue?.Val?.Value is { } sv)
                        (_startOverride.TryGetValue(nid, out var d) ? d : _startOverride[nid] = new())[li] = sv;
            }
        }
        foreach (var s in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
        {
            var np = s.StyleParagraphProperties?.NumberingProperties;
            if (s.StyleId?.Value is { } id && np?.NumberingId?.Val?.Value is { } nid && nid != 0)
                _styleNum[id] = (nid, np.NumberingLevelReference?.Val?.Value ?? 0);
        }
    }

    /// <summary>这一段的编号文字（如「3.2」「一、」）；没有编号或是项目符号返回 null。每调用一次计数前进一次，必须按文档顺序调用。</summary>
    public string? Next(Paragraph p)
    {
        var np = p.ParagraphProperties?.NumberingProperties;
        int numId, ilvl;
        if (np?.NumberingId?.Val?.Value is { } nid)
        {
            numId = nid;
            ilvl = np.NumberingLevelReference?.Val?.Value ?? 0;
        }
        else if (p.ParagraphProperties?.ParagraphStyleId?.Val?.Value is { } sid && _styleNum.TryGetValue(sid, out var sn))
        {
            (numId, ilvl) = sn;
            if (np?.NumberingLevelReference?.Val?.Value is { } li) ilvl = li;
        }
        else return null;
        if (numId == 0 || !_numToAbs.TryGetValue(numId, out var aid) || !_abs.TryGetValue(aid, out var levels)) return null;
        if (!levels.TryGetValue(ilvl, out var lvl) || lvl.Fmt is "bullet" or "none") return null;

        var c = _counters.TryGetValue(numId, out var arr) ? arr : _counters[numId] = new int?[9];
        int StartOf(int i) => _startOverride.TryGetValue(numId, out var o) && o.TryGetValue(i, out var s) ? s : levels.TryGetValue(i, out var l) ? l.Start : 1;
        c[ilvl] = c[ilvl] is { } v ? v + 1 : StartOf(ilvl);
        for (var j = ilvl + 1; j < c.Length; j++) c[j] = null; // 上一级前进，下级重新数
        for (var j = 0; j < ilvl; j++) c[j] ??= StartOf(j);

        var text = new StringBuilder();
        var t = lvl.Text;
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] == '%' && i + 1 < t.Length && char.IsDigit(t[i + 1]))
            {
                var k = t[i + 1] - '1';
                if (k >= 0 && k < c.Length)
                    // 「法律式编号」（isLgl）：上级是「一、」，这一级也写成「1.1」
                    text.Append(Format(c[k] ?? StartOf(k), lvl.Legal ? "decimal" : levels.TryGetValue(k, out var lk) ? lk.Fmt : "decimal"));
                i++;
            }
            else text.Append(t[i]);
        }
        var label = text.ToString().Trim();
        return label.Length == 0 ? null : label;
    }

    private static readonly string[] Digits = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九"];
    private static readonly string[] Stems = ["甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸"];

    public static string Format(int n, string fmt) => fmt switch
    {
        "decimalZero" => n.ToString("00"),
        "upperLetter" => Letters(n, 'A'),
        "lowerLetter" => Letters(n, 'a'),
        "upperRoman" => Roman(n),
        "lowerRoman" => Roman(n).ToLowerInvariant(),
        "chineseCounting" or "chineseCountingThousand" or "japaneseCounting" or "ideographDigital" or "taiwaneseCounting" or "chineseLegalSimplified" => Chinese(n),
        "ideographTraditional" => n is >= 1 and <= 10 ? Stems[n - 1] : n.ToString(),
        "decimalEnclosedCircle" or "decimalEnclosedCircleChinese" => n is >= 1 and <= 20 ? ((char)('①' + n - 1)).ToString() : n.ToString(),
        "decimalFullWidth" => string.Concat(n.ToString().Select(ch => (char)(ch - '0' + '０'))),
        _ => n.ToString(),
    };

    private static string Letters(int n, char a)
    {
        if (n <= 0) return n.ToString();
        var s = "";
        while (n > 0) { n--; s = (char)(a + n % 26) + s; n /= 26; }
        return s;
    }

    private static string Roman(int n)
    {
        if (n is <= 0 or >= 4000) return n.ToString();
        var map = new (int, string)[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        var sb = new StringBuilder();
        foreach (var (v, r) in map) while (n >= v) { sb.Append(r); n -= v; }
        return sb.ToString();
    }

    /// <summary>1–999 的中文数字（一、十一、二十、一百零五）。</summary>
    public static string Chinese(int n)
    {
        if (n <= 0 || n >= 1000) return n.ToString();
        var h = n / 100; var t = n / 10 % 10; var o = n % 10;
        var sb = new StringBuilder();
        if (h > 0) { sb.Append(Digits[h]).Append('百'); if (t == 0 && o > 0) sb.Append('零'); }
        if (t > 0) { if (!(h == 0 && t == 1)) sb.Append(Digits[t]); sb.Append('十'); }
        if (o > 0) sb.Append(Digits[o]);
        return sb.ToString();
    }
}
