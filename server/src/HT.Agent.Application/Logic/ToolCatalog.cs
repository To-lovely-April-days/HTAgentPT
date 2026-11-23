using System.Text;
using System.Text.RegularExpressions;
using HT.Agent.Application.Abstractions;

namespace HT.Agent.Application.Logic;

/// <summary>专用工具的一组：后台显示的短名、一起加载的几个工具、目录里那一行用途、几个常见说法（问题里出现就提前加载）。</summary>
public record ToolGroup(string Code, string Label, string[] Tools, string Summary, string[] Words);

/// <summary>工具按需加载：查资料、查项目、计算这些几乎每问都用的每次都带完整说明；
/// 流程模拟、做 PPT、浏览器这类专用工具平时只在提示词里列一行目录，用得上才把完整说明交给模型。
/// 用得上的判断三路：系统按问题先挑（常见说法、和用途的相似度、这个对话之前用过的、用户在界面上选的），
/// 模型翻开的技能里提到的，模型自己用 find_tools 找。加载过的这一问里一直带着，下一问也接着带。</summary>
public static class ToolCatalog
{
    public const string FindTools = "find_tools";

    public static readonly IReadOnlyList<ToolGroup> Groups =
    [
        new("show_block", "回答组件", ["show_block"],
            "回答里放组件：关键数字、提示框、图表、步骤、时间线、检查清单、方案对比卡、公式、流程图、工艺流程图 PFD、管道仪表图 PID、分子结构、反应方程式、设备数据表、技术偏离表、故障树、风险矩阵、甘特图等",
            ["图表", "流程图", "PFD", "PID", "时间线", "甘特", "对比卡", "数据表", "偏离表", "故障树", "风险矩阵", "分子结构", "反应方程式", "画个", "画一个"]),
        new("make_ppt", "做 PPT", ["make_ppt"], "按模板库做 PPT 文件（方案汇报、产品介绍、项目总结）",
            ["PPT", "幻灯片", "演示文稿", "汇报材料", "换个风格"]),
        new("process_sim", "流程模拟", ["process_sim"], "流程模拟（DWSIM）：算物料和热量衡算，出流程图、物流表和 DWSIM 文件",
            ["流程模拟", "DWSIM", "物料衡算", "热量衡算", "物料平衡", "热平衡", "物流表"]),
        new("browser", "内置浏览器", ["browser_open", "browser_read", "browser_act", "browser_look"], "内置浏览器：打开要登录、要点按钮、要翻页或要填表的网页，在上面点、填、截图",
            ["浏览器", "登录", "点一下", "帮我点", "填表", "翻页", "这个网页", "这页"]),
        new("chemical_info", "化学品数据", ["chemical_info"], "查化学品的公开数据：CAS 号、分子式、沸点闪点等物性、危险性（PubChem）",
            ["CAS", "物性", "沸点", "闪点", "熔点", "分子量"]),
        new("hazard_lookup", "危化品和危险工艺", ["hazard_lookup"], "查危险化学品目录、重点监管的危险化工工艺、易制爆、易制毒，客户问釜要配哪些安全控制时用",
            ["危化", "危险化学品", "易制爆", "易制毒", "重点监管", "剧毒", "危险工艺"]),
        new("search_literature", "论文专利", ["search_literature"], "查论文、专利（开放全文的能接着打开读）",
            ["论文", "文献", "专利", "期刊", "综述"]),
        new("compare_documents", "两份文件逐条对照", ["compare_documents"], "逐条对照两份文件（客户需求书对我们的方案、协议对协议），列出满足、部分满足、没满足",
            ["对照", "逐条", "偏离", "符不符合", "满不满足"]),
        new("image", "看图片", ["view_image", "ocr_image"], "看对话附件里的图片、识别图片里的文字",
            ["图片", "照片", "截图", "看图", "图里", "铭牌"]),
        new("translate_file", "翻译文件", ["translate_file"], "把附件整份翻译成另一种语言，出译文文件",
            ["翻译", "译成", "英文版", "中文版", "英译中", "中译英"]),
        new("cost_estimate", "估材料成本", ["cost_estimate"], "估一台设备的材料成本（先用 search_prices 把各项价格查到）",
            ["成本", "材料费", "造价", "核算"]),
        new("inquiry_form", "询价缺项单", ["inquiry_form"], "生成询价缺项单：客户需求不全时，列出要回头找客户确认的项",
            ["缺项", "询价单", "需求不全", "要问客户"]),
    ];

    /// <summary>聊天「＋ → 插件」里给员工开关的：会替员工去外面办事的（另加联网搜索）。
    /// 查化学品、看图片、估成本这些是助手答题时自己用的本事，不摆出来。</summary>
    public static readonly string[] UserSwitches = ["browser"];

    private static readonly Dictionary<string, ToolGroup> ByTool =
        Groups.SelectMany(g => g.Tools.Select(t => (t, g))).ToDictionary(x => x.t, x => x.g);

    /// <summary>这个工具属于哪一组；null = 常用工具，每次都带。</summary>
    public static ToolGroup? GroupOf(string tool) => ByTool.GetValueOrDefault(tool);

    public static ToolGroup? Find(string code) => Groups.FirstOrDefault(g => g.Code == code);

    /// <summary>这次能加载的组：组里至少有一个工具这个人能用。</summary>
    public static List<ToolGroup> Available(IEnumerable<ToolSpec> all)
    {
        var names = all.Select(t => t.Name).ToHashSet();
        return Groups.Where(g => g.Tools.Any(names.Contains)).ToList();
    }

    /// <summary>交给模型的工具：常用的 + 加载了的组，顺序照原来的；有没加载的组就再带上 find_tools。</summary>
    public static List<ToolSpec> Live(IReadOnlyList<ToolSpec> all, IReadOnlySet<string> loaded)
    {
        var list = all.Where(t => GroupOf(t.Name) is not { } g || loaded.Contains(g.Code)).ToList();
        if (Available(all).Any(g => !loaded.Contains(g.Code))) list.Add(FindToolsSpec);
        return list;
    }

    public static readonly ToolSpec FindToolsSpec = new(FindTools,
        "加载专用工具：提示词里「专用工具」目录列的工具平时不带说明，要用先用这个加载，加载后下一步就能直接调用。"
        + "names 写目录里的工具名（可以几个），不知道叫什么就在 need 里写要做的事（如「算物料衡算」「做 PPT」「看图片」）。",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["names"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string" }, ["description"] = "目录里的工具名" },
                ["need"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "要做的事（不知道工具名时写）" },
            },
            ["required"] = Array.Empty<string>(),
        });

    /// <summary>提示词里的目录：没加载的组每组一行。</summary>
    public static string CatalogPrompt(IEnumerable<ToolGroup> notLoaded)
    {
        var list = notLoaded.ToList();
        if (list.Count == 0) return "";
        var sb = new StringBuilder("\n专用工具（平时不带说明；用得上时先用 find_tools 加载，再调用。工具名：用途）：\n");
        foreach (var g in list) sb.AppendLine($"- {string.Join(" / ", g.Tools)}：{g.Summary}");
        return sb.ToString();
    }

    /// <summary>问题里出现了哪些组的常见说法（大小写不分）。</summary>
    public static List<string> WordHits(string text, IEnumerable<ToolGroup> groups)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return groups.Where(g => g.Words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))).Select(g => g.Code).ToList();
    }

    /// <summary>一段文字里提到了哪些专用工具（技能正文里写了 process_sim，翻开技能就把它加载上）。</summary>
    public static List<string> Mentioned(string text, IEnumerable<ToolGroup> groups)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return groups.Where(g => g.Tools.Any(t => Regex.IsMatch(text, $@"(?<![A-Za-z0-9_]){Regex.Escape(t)}(?![A-Za-z0-9_])"))).Select(g => g.Code).ToList();
    }

    /// <summary>系统先挑：说法对上的、和用途最像的（分数过线，最多 maxBySimilarity 组）、之前用过的、界面上选的、后台设成常驻的。</summary>
    public static HashSet<string> Preload(IEnumerable<ToolGroup> available, string question,
        IReadOnlyDictionary<string, double>? similarity, double minSimilarity, int maxBySimilarity,
        IEnumerable<string> usedBefore, IEnumerable<string> forced, IEnumerable<string> pinned)
    {
        var avail = available.ToList();
        var codes = avail.Select(g => g.Code).ToHashSet();
        var set = new HashSet<string>(WordHits(question, avail));
        if (similarity is not null)
            foreach (var kv in similarity.Where(kv => codes.Contains(kv.Key) && kv.Value >= minSimilarity).OrderByDescending(kv => kv.Value).Take(maxBySimilarity))
                set.Add(kv.Key);
        foreach (var c in usedBefore.Concat(forced).Concat(pinned))
        {
            var code = codes.Contains(c) ? c : GroupOf(c)?.Code;   // 写工具名或组名都行
            if (code is not null && codes.Contains(code)) set.Add(code);
        }
        return set;
    }

    /// <summary>find_tools：先按名字（工具名、组名）认；认不出按要做的事匹配（常见说法、用途里的词，再加相似度），取最像的几组。</summary>
    public static List<string> Match(IEnumerable<ToolGroup> available, IReadOnlyList<string> names, string? need,
        IReadOnlyDictionary<string, double>? similarity = null, int max = 3)
    {
        var avail = available.ToList();
        var hit = new List<string>();
        foreach (var raw in names)
        {
            var n = raw.Trim();
            var g = avail.FirstOrDefault(x => x.Code.Equals(n, StringComparison.OrdinalIgnoreCase) || x.Tools.Any(t => t.Equals(n, StringComparison.OrdinalIgnoreCase)));
            if (g is not null && !hit.Contains(g.Code)) hit.Add(g.Code);
        }
        // names 里写的不是工具名（写成了要做的事）也当 need 用
        var text = string.Join(" ", new[] { need ?? "" }.Concat(names.Where(n => avail.All(g => g.Code != n.Trim() && !g.Tools.Contains(n.Trim())))));
        if (string.IsNullOrWhiteSpace(text)) return hit;
        var scored = avail.Where(g => !hit.Contains(g.Code)).Select(g => (g.Code, Score: TextScore(text, g) + (similarity?.GetValueOrDefault(g.Code) ?? 0)))
            .Where(x => x.Score > 0.3).OrderByDescending(x => x.Score).Take(Math.Max(0, max - hit.Count));
        hit.AddRange(scored.Select(x => x.Code));
        return hit;
    }

    /// <summary>文字匹配分：常见说法整词出现 +1；用途里的两字词重合按比例加（最多 +1）。</summary>
    public static double TextScore(string text, ToolGroup g)
    {
        var score = g.Words.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)) * 1.0;
        if (g.Tools.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))) score += 2;
        var q = Bigrams(text);
        if (q.Count > 0) score += Math.Min(1.0, q.Count(Bigrams(g.Summary).Contains) / (double)q.Count * 2);
        return score;
    }

    private static HashSet<string> Bigrams(string s)
    {
        var chars = s.Where(c => c >= 0x4E00 && c <= 0x9FFF).ToArray();   // 只看汉字：英文词靠常见说法认
        var set = new HashSet<string>();
        for (var i = 0; i + 1 < chars.Length; i++) set.Add(new string(chars, i, 2));
        return set;
    }

    /// <summary>用来和问题比相似度的文字：用途 + 常见说法。</summary>
    public static string MatchText(ToolGroup g) => g.Summary + "。" + string.Join("、", g.Words);

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>参数检查：JSON 坏了、必填的没填，说清楚哪里不对、应该怎么填，让模型改了重调。null = 没问题。</summary>
    public static string? CheckArgs(ToolSpec spec, string argumentsJson, out System.Text.Json.Nodes.JsonObject args)
    {
        args = new System.Text.Json.Nodes.JsonObject();
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(argumentsJson);
                // 有的模型把参数又包了一层字符串："{\"query\": …}"
                if (node is System.Text.Json.Nodes.JsonValue sv && sv.TryGetValue<string>(out var inner) && inner.TrimStart().StartsWith('{'))
                    node = System.Text.Json.Nodes.JsonNode.Parse(inner);
                if (node is System.Text.Json.Nodes.JsonObject o) args = o;
                else return $"{spec.Name} 的参数要写成 JSON 对象，如 {{\"参数名\": \"值\"}}。" + ParamHelp(spec);
            }
            catch (System.Text.Json.JsonException)
            {
                return $"{spec.Name} 的参数不是合法的 JSON（检查引号、逗号、括号）。" + ParamHelp(spec);
            }
        }
        if (spec.Parameters is not IDictionary<string, object> p || !p.TryGetValue("required", out var reqObj) || reqObj is not IEnumerable<string> required) return null;
        var a = args;
        var missing = required.Where(r => a[r] is null || a[r] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)).ToList();
        return missing.Count == 0 ? null : $"{spec.Name} 缺必填参数：{string.Join("、", missing)}。" + ParamHelp(spec);
    }

    /// <summary>参数说明：每个参数一行（名字、类型、是否必填、说明），截短。</summary>
    public static string ParamHelp(ToolSpec spec)
    {
        if (spec.Parameters is not IDictionary<string, object> p || !p.TryGetValue("properties", out var propsObj) || propsObj is not IDictionary<string, object> props) return "";
        var required = p.TryGetValue("required", out var r) && r is IEnumerable<string> rs ? rs.ToHashSet() : [];
        var sb = new StringBuilder("\n参数：");
        foreach (var (name, def) in props)
        {
            var d = def as IDictionary<string, object>;
            var type = d?.TryGetValue("type", out var t) == true ? t?.ToString() : "";
            var desc = d?.TryGetValue("description", out var ds) == true ? ds?.ToString() ?? "" : "";
            if (desc.Length > 60) desc = desc[..60] + "…";
            sb.Append($"\n- {name}（{type}{(required.Contains(name) ? "，必填" : "")}）{desc}");
        }
        sb.Append("\n改好参数再调一次。");
        return sb.ToString();
    }
}
