using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>按字段名从结构化文本里取值。任务单、参数表这类资料解析后有两种形态，都要认：
/// 表单行「| 合同编号 | HT-2023-C091 | 下单日期 | 2023-04-11 |」——取标签右邻那格；
/// 表头+数据行「| 序号 | 名称 | 数量 | 型号 |」+「| 1 | 反应釜 | 1 | CJF-5L |」——按列对齐取。
/// 整块当值用没有意义，取错更糟：继承预填与参数建议都靠这个把历史资料真正用起来。
/// 匹配保守——精确同名优先，章节相符加分（「配件名称」不该取到设备表那一列），取不到返回 null。</summary>
public static class FormFieldExtractor
{
    /// <summary>行内标签与取值的冒号式写法（「合同编号：HT-2023-C091」）。</summary>
    private static readonly Regex ColonPair = new(@"^\s*([^:：|]{2,20})\s*[:：]\s*(.+)$", RegexOptions.Compiled);

    /// <param name="knownLabels">同一模板里其他字段名。右邻那格如果本身是个字段名，
    /// 说明这行是表头而不是「标签|值」，不能把它当取值（「数量」的右邻「单价（元）」就是这种）。</param>
    /// <param name="sectionHint">字段所属章节。同名列出现在多张表里时（设备表与配件表都有「名称」），
    /// 靠它认出该取哪一张。</param>
    public static string? Extract(string text, string fieldName, string? unit = null,
        IReadOnlyCollection<string>? knownLabels = null, string? sectionHint = null)
        => ExtractScored(text, fieldName, unit, knownLabels, sectionHint)?.Value;

    /// <summary>连同匹配分一起返回：同一份资料被切成多块时，靠分数选最贴的那一处
    /// （「配件名称」在设备表与配件表都能沾边，章节相符的那张表分更高）。</summary>
    public static (string Value, int Score)? ExtractScored(string text, string fieldName, string? unit = null,
        IReadOnlyCollection<string>? knownLabels = null, string? sectionHint = null)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(fieldName)) return null;
        var want = Norm(fieldName);
        if (want.Length == 0) return null;
        var labels = (knownLabels ?? []).Select(Norm).Where(l => l.Length > 0 && l != want).ToHashSet();
        var lines = text.Split('\n');

        string? best = null;
        var bestScore = 0;
        for (var li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            if (line.Contains('|'))
            {
                var cells = line.Split('|').Select(c => c.Trim()).ToList();
                var data = li + 1 < lines.Length && lines[li + 1].Contains('|')
                    ? lines[li + 1].Split('|').Select(c => c.Trim()).ToList()
                    : null;
                if (data is not null && IsHeaderRow(cells, data))
                {
                    // 表头行：取值在下一行的同一列
                    {
                        var bonus = SectionBonus(lines, li, sectionHint);
                        for (var i = 0; i < cells.Count; i++)
                        {
                            var score = MatchScore(Norm(cells[i]), want);
                            if (score == 0) continue;
                            score += bonus;
                            if (score <= bestScore) continue;
                            var value = Clean(data[i], fieldName, unit);
                            if (value is null || LooksLikeLabel(value, labels)) continue;
                            best = value;
                            bestScore = score;
                        }
                    }
                    continue;                       // 表头行不再按「标签|值」解释
                }
                // 格子里自带「标签：值」的写法（「防爆等级要求：ExdⅡCT4」占一整格）
                foreach (var cell in cells)
                {
                    var cm = ColonPair.Match(cell);
                    if (!cm.Success) continue;
                    var cellScore = MatchScore(Norm(cm.Groups[1].Value), want);
                    if (cellScore <= bestScore) continue;
                    var cellValue = Clean(cm.Groups[2].Value, fieldName, unit);
                    if (cellValue is null) continue;
                    best = cellValue;
                    bestScore = cellScore;
                }
                for (var i = 0; i + 1 < cells.Count; i++)
                {
                    var score = MatchScore(Norm(cells[i]), want);
                    if (score <= bestScore) continue;
                    var next = cells[i + 1];
                    if (LooksLikeLabel(next, labels)) continue;  // 右邻也是字段名 → 这是表头，不是取值
                    var value = Clean(next, fieldName, unit);
                    if (value is null) continue;
                    best = value;
                    bestScore = score;
                }
            }
            else
            {
                var m = ColonPair.Match(line);
                if (!m.Success) continue;
                var score = MatchScore(Norm(m.Groups[1].Value), want);
                if (score <= bestScore) continue;
                var value = Clean(m.Groups[2].Value, fieldName, unit);
                if (value is null) continue;
                best = value;
                bestScore = score;
            }
        }
        return best is null ? null : (best, bestScore);
    }

    /// <summary>字段名在整批资料里最贴的一处取值（跨块比分，不是碰到的第一处）。</summary>
    public static string? ExtractFirst(IEnumerable<string> texts, string fieldName, string? unit = null,
        IReadOnlyCollection<string>? knownLabels = null, string? sectionHint = null)
        => texts.Select(t => ExtractScored(t, fieldName, unit, knownLabels, sectionHint))
            .Where(h => h is not null)
            .OrderByDescending(h => h!.Value.Score)
            .Select(h => h!.Value.Value)
            .FirstOrDefault();

    /// <summary>表头行：多列、都是不含数字的短列名，且下一行是同列数的数据行。
    /// 只看本行不够——「| 结构形式 | 法兰 | 升 降 | 无 |」也是短词无数字，那是表单行不是表头；
    /// 真表头的下一行会有取值的样子（带数字，或明显比列名长）。</summary>
    private static bool IsHeaderRow(List<string> cells, List<string> next)
    {
        var named = cells.Where(c => c.Length > 0).ToList();
        if (named.Count < 3 || !named.All(c => c.Length <= 8 && !c.Any(char.IsDigit))) return false;
        if (next.Count != cells.Count) return false;
        return next.Any(c => c.Length > 8 || c.Any(char.IsDigit));
    }

    /// <summary>表头前两行里出现了章节名的关键字就加分——同名列出现在多张表里时靠它定位
    /// （「配件及耗材清单」上方的表才是「配件信息」那几项该取的）。</summary>
    private static int SectionBonus(string[] lines, int headerIndex, string? sectionHint)
    {
        if (string.IsNullOrWhiteSpace(sectionHint)) return 0;
        var key = Norm(sectionHint);
        if (key.Length < 2) return 0;
        key = key[..2];                              // 「设备信息」→「设备」，「配件信息」→「配件」
        for (var i = Math.Max(0, headerIndex - 2); i <= headerIndex; i++)
            if (Norm(lines[i]).Contains(key, StringComparison.Ordinal)) return 2;
        return 0;
    }

    /// <summary>这一格本身是不是个列名（按同一套匹配规则，不能只比全等：
    /// 「单价（元）」对字段「单价」就是同一列）。</summary>
    private static bool LooksLikeLabel(string cell, HashSet<string> labels)
    {
        var n = Norm(cell);
        return n.Length > 0 && labels.Any(l => MatchScore(n, l) > 0);
    }

    /// <summary>2=去空白后同名；1=互相包含且长度接近（「材质」对「主体材质」）；0=不算命中。</summary>
    private static int MatchScore(string label, string want)
    {
        if (label.Length == 0) return 0;
        if (label == want) return 2;
        if (label.Length > want.Length * 2 || want.Length > label.Length * 2) return 0;
        return label.Contains(want, StringComparison.Ordinal) || want.Contains(label, StringComparison.Ordinal) ? 1 : 0;
    }

    /// <summary>取值清洗：空、纯符号、与字段名同文的都不算值；带模板自带单位的把单位去掉
    /// （模板里「_____ml」的单位是版面固定文字，回填只填数值，否则会出现「20000ml ml」）。</summary>
    private static string? Clean(string raw, string fieldName, string? unit)
    {
        var v = raw.Trim().Trim('|').Trim();
        if (v.Length == 0 || v.Length > 300) return null;
        if (Norm(v) == Norm(fieldName)) return null;
        if (!v.Any(char.IsLetterOrDigit)) return null;
        if (!string.IsNullOrWhiteSpace(unit))
        {
            var u = unit.Trim();
            if (v.Length > u.Length && v.EndsWith(u, StringComparison.OrdinalIgnoreCase))
                v = v[..^u.Length].TrimEnd();
        }
        return v.Length == 0 ? null : v;
    }

    /// <summary>比对用的归一化：空白、冒号、括号、顿号一类都去掉——「单价（元）」与字段「单价」是同一列。</summary>
    private static string Norm(string s) => Regex.Replace(s, @"[\s\u3000:：*（）()【】\[\]、,，。.]+", "");
}
