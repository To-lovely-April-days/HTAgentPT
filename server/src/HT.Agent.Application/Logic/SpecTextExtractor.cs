using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>从技术协议/技术方案的参数正文里按设备取值。历史协议的参数不是表格，而是一段段
/// 「标签：值」，按设备分组写：
///   （1）R-3101 预聚釜
///   接液材质：316L，夹套材质：316L；
///   工作温度：10℃，工作压力：3.5MPaG；
/// 或者在部件清单的规格参数格里：「| R-101 | 反应釜 | 容积：29L；材质：316L；工作压力：-0.1~3MPaG；… |」。
///
/// 槽位名是按模板展开出来的「分组·标签（单位）」（内筒·工作压力（MPa）、夹套/盘管·材质），
/// 所在章节带着设备（「表3-1 带搅拌反应釜 · R-101 聚合反应釜」）。取值分三步：
/// ① 把正文切成一台台设备（设备标题行、清单行）；② 每台里拆出「标签：值」；
/// ③ 标签按同名/同义/包含打分，分组不对的不要（问内筒的不取「夹套材质」），
///    位号、名称对得上的那台设备加分——同一份协议里往往有好几台釜。</summary>
public static class SpecTextExtractor
{
    /// <summary>要取的一项：标签、分组（内筒/夹套/管程/壳程…，主体为 null）、单位、设备线索。</summary>
    public record Target(string Label, string? Group, string? Unit, string? TagNo, IReadOnlyList<string> EquipmentWords,
        string? AltLabel = null);

    public record Hit(string Value, int Score, string Equipment, string Label);

    /// <summary>主体分组：问它的时候不要带「夹套/壳程」的标签。</summary>
    private static readonly string[] MainGroups = ["内筒", "釜体", "罐体"];
    private static readonly string[] SideWords = ["夹套", "盘管", "壳程"];
    /// <summary>「X·Y」里 X 是设备部位时才算分组；不是部位（「内筒全容积·容积及尺寸」「下放料方式·内径」）
    /// 那 X 本身也是标签，两个都试。</summary>
    private static readonly string[] PartWords = ["内筒", "釜体", "罐体", "夹套", "盘管", "壳程", "管程"];

    /// <summary>同义标签：模板用词 → 历史协议里常见的写法。</summary>
    private static readonly Dictionary<string, string[]> Synonyms = new()
    {
        ["材质"] = ["接液材质", "釜体材质", "本体材质", "主体材质", "过流材质"],
        ["内筒全容积"] = ["容积", "全容积"],
        ["容积及尺寸"] = ["容积"],
        ["电机功率"] = ["功率", "搅拌功率"],
        ["搅拌转速"] = ["调速范围", "转速", "电机搅拌转速"],
        ["搅拌扭矩"] = ["扭矩", "扭矩范围"],
        ["搅拌密封"] = ["密封", "搅拌"],
        ["管程换热面积"] = ["换热面积"],
        ["安全阀整定压力"] = ["整定压力", "泄压压力"],
        ["爆破片爆破压力"] = ["爆破压力", "起跳压力"],
        ["测量范围"] = ["压力", "量程"],
        ["封头形式"] = ["结构"],
    };

    private static readonly Regex Pair = new(@"^\s*([^：:]{1,18}?)\s*[：:]\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex TagLike = new(@"\b[A-Z]{1,4}-?\d{2,5}[A-Z]?\b", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^[（(]\d+[）)]", RegexOptions.Compiled);
    private static readonly char[] Breaks = ['；', ';', '，', ',', '。', '\n'];

    /// <summary>从展开后的槽位名与章节造 Target。章节「表3-1 带搅拌反应釜 · R-101 聚合反应釜」
    /// 或「主要部件清单 · PG-101 压力表」里取位号、名称与类别做设备线索。</summary>
    public static Target? FromSlot(string name, string? unit, string? section)
    {
        var unitInName = Regex.Match(name, @"[（(]([^（）()]+)[）)]\s*$");
        var bare = Regex.Replace(name, @"[（(][^（）()]*[）)]", "").Trim();
        var parts = bare.Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        var label = parts[^1];
        var group = parts.Length > 1 ? parts[0] : null;
        string? alt = null;
        if (group is not null && !group.Split('/', '、').Any(g => PartWords.Contains(g)))
        {
            alt = group;
            group = null;
        }
        if (group is not null && MainGroups.Contains(group)) group = null;

        string? tagNo = null;
        var words = new List<string>();
        if (!string.IsNullOrWhiteSpace(section))
        {
            var dot = section.IndexOf('·');
            var who = dot >= 0 ? section[(dot + 1)..].Trim() : "";
            var kind = Regex.Replace(dot >= 0 ? section[..dot] : section, @"^(表\s*\d+-\d+|主要部件清单)\s*", "").Trim();
            var tm = TagLike.Match(who);
            if (tm.Success) { tagNo = tm.Value; who = who.Replace(tm.Value, "").Trim(); }
            if (who.Length > 0) words.Add(who);
            words.AddRange(kind.Split(['、', '，', ' '], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 1));
        }
        return new Target(label, group, unit ?? (unitInName.Success ? unitInName.Groups[1].Value : null), tagNo, words, alt);
    }

    /// <summary>整批资料里最贴的一处（跨块比分）。</summary>
    public static Hit? ExtractBest(IEnumerable<string> texts, Target target)
        => texts.Select(t => Extract(t, target)).Where(h => h is not null)
            .OrderByDescending(h => h!.Score).FirstOrDefault();

    public static Hit? Extract(string text, Target target)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Hit? best = null;
        foreach (var (equipment, body) in Sections(text))
        {
            var bonus = EquipmentBonus(equipment, target);
            foreach (var (label, value) in Pairs(body))
            {
                var score = Math.Max(LabelScore(label, target, target.Label),
                    target.AltLabel is null ? 0 : LabelScore(label, target, target.AltLabel));
                if (score == 0) continue;
                score += bonus;
                if (best is not null && score <= best.Score) continue;
                var clean = Clean(value, target.Unit);
                if (clean is null) continue;
                best = new Hit(clean, score, equipment, label);
            }
        }
        // 标签只沾边、设备也对不上的，不如不给
        return best is { Score: >= 3 } ? best : null;
    }

    /// <summary>正文切成一台台设备：标题行（带位号、或「（1）」开头、且不是「标签：值」）开始一台；
    /// 表格行里规格参数那一格自成一台，位号+名称做它的标题。标题前的内容归到空标题。</summary>
    private static IEnumerable<(string Equipment, string Body)> Sections(string text)
    {
        var current = "";
        var body = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('|'))
            {
                var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToList();
                var spec = cells.Select((c, i) => (c, i)).OrderByDescending(x => x.c.Count(ch => ch is '：' or ':')).First();
                if (spec.c.Count(ch => ch is '：' or ':') == 0) continue;
                var head = string.Join(" ", cells.Take(spec.i).Where(c => c.Length > 0 && c.Length <= 20));
                yield return (head, spec.c);
                continue;
            }
            var isHeader = !line.Contains('：') && !line.Contains(':') && line.Length <= 30
                           && (TagLike.IsMatch(line) || Numbered.IsMatch(line));
            if (isHeader)
            {
                if (body.Count > 0) yield return (current, string.Join("\n", body));
                current = line;
                body.Clear();
                continue;
            }
            body.Add(line);
        }
        if (body.Count > 0) yield return (current, string.Join("\n", body));
    }

    /// <summary>拆「标签：值」。「管程材质/壳程材质：316L/304」「工作压力 管程/壳程：-0.1~3/0.3MPaG」
    /// 这种成对写法拆成两条，值里的单位补到前一条上。</summary>
    private static IEnumerable<(string Label, string Value)> Pairs(string body)
    {
        foreach (var seg in body.Split(Breaks, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Pair.Match(seg);
            if (!m.Success) continue;
            var label = m.Groups[1].Value.Trim();
            var value = m.Groups[2].Value.Trim();
            var lp = label.Split('/');
            var vp = value.Split('/');
            if (lp.Length == 2 && vp.Length == 2)
            {
                // 「工作压力 管程/壳程」：公共部分在斜杠外面
                var pre = Regex.Match(lp[0], @"^(.*?)(管程|壳程|内筒|夹套)$");
                var common = pre.Success ? pre.Groups[1].Value.Trim() : "";
                var right = Regex.Match(lp[1], @"^(管程|壳程|内筒|夹套)(.*)$");
                var tail = right.Success ? right.Groups[2].Value.Trim() : "";
                var a = pre.Success ? pre.Groups[2].Value + common + tail : lp[0] + tail;
                var b = (right.Success ? right.Groups[1].Value + common + tail : lp[1]);
                var unit = Regex.Match(vp[1], @"[^\d.\-~～\s]+$").Value;
                var v0 = vp[0].Trim();
                if (unit.Length > 0 && Regex.IsMatch(v0, @"[\d.]$")) v0 += unit;
                yield return (a.Trim(), v0);
                yield return (b.Trim(), vp[1].Trim());
                continue;
            }
            yield return (label, value);
        }
    }

    /// <summary>4=同名；3=同义；2=互相包含且长度接近。分组：问主体的不收夹套/壳程标签，
    /// 问夹套/壳程的只收带这个字的标签（「夹套材质」→ 去掉分组字再比「材质」）。</summary>
    private static int LabelScore(string label, Target t, string wanted)
    {
        var l = Norm(label);
        if (l.Length == 0) return 0;
        if (t.Group is null)
        {
            if (SideWords.Any(w => l.Contains(w))) return 0;
            l = l.Replace("管程", "");
        }
        else
        {
            var options = t.Group.Split('/', '、').Select(Norm).Where(o => o.Length > 0).ToList();
            var hit = options.FirstOrDefault(o => l.Contains(o));
            if (hit is null) return 0;
            l = l.Replace(hit, "");
            if (l.Length == 0) return 0;
        }
        var want = Norm(wanted);
        if (l == want) return 4;
        if (Synonyms.TryGetValue(want, out var syn) && syn.Select(Norm).Contains(l)) return 3;
        if (l.Length > want.Length * 2 || want.Length > l.Length * 2) return 0;
        return l.Contains(want) || want.Contains(l) ? 2 : 0;
    }

    /// <summary>设备对得上加分：位号相同 +4，名称/类别有两字以上的共同部分 +2（「聚合反应釜」对「反应釜」）。
    /// 位号字母前缀不同（问 R- 釜却是 E- 换热器、问 PG 压力表却是 PT 变送器）减 3——那是另一类设备，
    /// 参数名相同也不能拿来用。同类别不同台的不减分，历史上同类设备的参数正是参考。</summary>
    private static int EquipmentBonus(string equipment, Target t)
    {
        if (equipment.Length == 0) return 0;
        var bonus = 0;
        var want = Prefix(t.TagNo);
        var have = Prefix(TagLike.Match(equipment) is { Success: true } m ? m.Value
            : Regex.Match(equipment, @"^\s*(?:[（(]\d+[）)])?\s*([A-Z]{1,4})(?![A-Za-z])").Groups[1].Value);
        if (want is not null && have is not null && want != have) return -3;
        if (t.TagNo is not null && Regex.Replace(equipment, @"\s", "").Contains(t.TagNo.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            bonus += 4;
        if (t.EquipmentWords.Any(w => CommonLength(equipment, w) >= 2)) bonus += 2;
        return bonus;
    }

    private static string? Prefix(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = Regex.Match(tag.Trim(), @"^([A-Z]{1,4})");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int CommonLength(string a, string b)
    {
        var best = 0;
        for (var i = 0; i < a.Length; i++)
            for (var j = 0; j < b.Length; j++)
            {
                var k = 0;
                while (i + k < a.Length && j + k < b.Length && a[i + k] == b[j + k] && !char.IsAsciiLetterOrDigit(a[i + k])) k++;
                if (k > best) best = k;
            }
        return best;
    }

    private static string? Clean(string raw, string? unit)
    {
        // 空格后面又起了一个「标签：」（「316L 使用温度：180℃」）：值到空格为止
        var v = Regex.Replace(raw.Trim(), @"\s+[^\s：:]{1,10}[：:].*$", "").TrimEnd('；', ';', '，', ',', '。');
        if (v.Length == 0 || v.Length > 80 || !v.Any(char.IsLetterOrDigit)) return null;
        if (v.StartsWith("--")) return null;   // 「电机功率：--KW」是没定的占位
        if (!string.IsNullOrWhiteSpace(unit) && v.Length > unit.Length
            && v.EndsWith(unit, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(v[..^unit.Length], @"[\d.]\s*$"))
            v = v[..^unit.Length].TrimEnd();
        return v;
    }

    private static string Norm(string s) => Regex.Replace(s, @"[\s　（）()【】\[\]、]+", "");
}
