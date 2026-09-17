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

    // ── 总指挥派工单 ──────────────────────────────────────────

    /// <summary>一条派工：Type 为 fill / ledger / pick_base / suggest / adopt / ask / skip / summary / render。
    /// 总指挥（模型或规则）只产出派工单，具体活由服务端各专员按既有能力执行。</summary>
    public sealed record PlanAction(
        string Type,
        string? Tag = null, string? Value = null,
        string? Customer = null, string? Device = null, string? Keyword = null,
        IReadOnlyList<string>? Tags = null,
        string? Question = null,
        string? ProjectNo = null, int? Index = null,
        /// <summary>按显示名称指代的槽位（「采纳材质」），由服务端解析成 tag。</summary>
        string? Name = null);

    private static readonly HashSet<string> KnownActions =
        ["fill", "ledger", "pick_base", "suggest", "adopt", "ask", "advise", "skip", "summary", "render"];

    /// <summary>解析总指挥模型的派工单：{"actions":[...]}；兼容早期只有 {"fills":[...]} 的抽取格式。
    /// 未知动作类型丢弃，解析失败返回空表——由调用方退回规则规划。</summary>
    public static IReadOnlyList<PlanAction> ParsePlan(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return [];
        try
        {
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            var root = doc.RootElement;
            var result = new List<PlanAction>();
            if (root.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in actions.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var type = Str(a, "type")?.Trim().ToLowerInvariant();
                    if (type is null || !KnownActions.Contains(type)) continue;
                    var value = a.TryGetProperty("value", out var v) ? ScalarToString(v) : null;
                    if (type == "fill" && (string.IsNullOrWhiteSpace(Str(a, "tag")) || string.IsNullOrWhiteSpace(value))) continue;
                    result.Add(new PlanAction(type,
                        Tag: Blank(Str(a, "tag")), Value: value?.Trim(),
                        Customer: Blank(Str(a, "customer")), Device: Blank(Str(a, "device")), Keyword: Blank(Str(a, "keyword")),
                        Tags: a.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                            ? tags.EnumerateArray().Select(t => t.GetString()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList()
                            : null,
                        Question: Blank(Str(a, "question")),
                        Name: Blank(Str(a, "name")),
                        ProjectNo: Blank(Str(a, "projectNo")),
                        Index: a.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number && idx.TryGetInt32(out var i) ? i : null));
                }
            }
            foreach (var (tag, value) in ParseModelFills(reply[start..(end + 1)]))
                if (!result.Any(r => r.Type == "fill" && r.Tag == tag))
                    result.Add(new PlanAction("fill", Tag: tag, Value: value));
            return result;
        }
        catch (JsonException) { return []; }

        static string? Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        static string? ScalarToString(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            _ => null
        };
    }

    /// <summary>工况顾问的一条建议：给哪一项、建议什么值、为什么、有什么风险。
    /// 这是模型按工艺常识给的，不是文档依据——界面上必须与「有依据的建议」分开，人工确认才落表。</summary>
    /// <summary>工况顾问的一条建议。Level 是模型判定的要紧程度（high=安全相关/不改会出事），
    /// 界面据此排序与标色——工程师先看要命的那几条，不是从头读到尾。</summary>
    public sealed record EngineeringAdvice(string Tag, string Value, string? Reason, string? Risk, string Level = "normal");

    /// <summary>解析工况顾问的回复：{"notes":"…","advices":[{tag,value,reason,risk,level}]}。
    /// 缺 tag 或 value 的丢弃；level 只认 high/normal，别的一律按 normal；
    /// 解析失败返回空——宁可不给，也不给半截建议。</summary>
    public static (string? Notes, IReadOnlyList<EngineeringAdvice> Advices) ParseAdvice(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return (null, []);
        try
        {
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            var root = doc.RootElement;
            var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()?.Trim() : null;
            var list = new List<EngineeringAdvice>();
            if (root.TryGetProperty("advices", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var tag = Text(a, "tag");
                    var value = Text(a, "value");
                    if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(value)) continue;
                    var level = Text(a, "level")?.Trim();
                    list.Add(new EngineeringAdvice(tag!.Trim(), value!.Trim(), Text(a, "reason"), Text(a, "risk"),
                        string.Equals(level, "high", StringComparison.OrdinalIgnoreCase) ? "high" : "normal"));
                }
            }
            return (string.IsNullOrWhiteSpace(notes) ? null : notes, list);
        }
        catch (JsonException) { return (null, []); }

        static string? Text(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    private static readonly Regex AdoptAll = new(@"^(都采纳|全部采纳|采纳全部|采纳|就用建议的|按建议的?来|采纳建议|用建议的)$", RegexOptions.Compiled);
    private static readonly Regex AdoptOne = new(@"^采纳[:：]?(.{1,20})$", RegexOptions.Compiled);
    private static readonly Regex PickIndex = new(@"^(?:就?用|选|拿|要)?第([一二三四五12345])(?:个|条|项)?(?:做|作|当|为)?(?:基准)?(?:吧|啊)?$", RegexOptions.Compiled);
    private static readonly Regex ProjectNoRef = new(@"([A-Z]{1,3}-\d{4}-\d{3,5})", RegexOptions.Compiled);
    private static readonly Regex LedgerAsk = new(
        @"(历史|做过|台账|以前|之前|类似|相近|相似|老项目|参考项目).{0,12}(项目|做过|查|找|有没有|哪些|案例)|^查(一下|下|询)?.{0,10}(历史|台账|项目)|有没有.{0,8}(做过|历史|类似)",
        RegexOptions.Compiled);
    private static readonly Regex SuggestAsk = new(
        @"(推荐|建议|参考一下|参考.{0,6}(参数|值|数据)|一般(用|是|取|选)什么|通常|给个参考|帮我定|帮我选)", RegexOptions.Compiled);
    /// <summary>工况、用途、介质、硬性约束一类的说明：「我要做硝化反应」「介质有腐蚀性」
    /// 「要过夜连续运行」。这类话既不是取值也不是查历史，而是应当影响一批参数选型的前提。</summary>
    private static readonly Regex ConditionTell = new(
        @"(硝化|氢化|加氢|聚合|氧化|还原|酯化|磺化|重氮|光催化|电催化|超临界|发酵|结晶|萃取|蒸馏|煅烧)" +
        @"|(腐蚀|强酸|强碱|氯离子|卤素|易燃|易爆|有毒|剧毒|放热|热失控|高粘|结垢|析出|淤浆)" +
        @"|(我要做|要做|用来做|用于|拿来做|工况是|介质是|物料是|反应是|做的是|场景是)" +
        @"|(过夜|连续运行|无人值守|洁净|无菌|GMP|防爆要求|高真空)",
        RegexOptions.Compiled);

    /// <summary>对上一轮给过的建议不买账：「材质换其他的」「这个不合适」「再给一个」。
    /// 要点在于**后面不能跟具体取值**——「材质换成316L」是给值不是要新方案，
    /// 所以只认后面是「其他/别的/一个」这类含糊说法或干脆没有下文的。</summary>
    private static readonly Regex ReviseAsk = new(
        @"^(?<name>.{0,14}?)[的]?\s*(换|改|重选|重新选|重新推荐|再给|再来|再推荐)\s*" +
        @"(一?[个种款条])?\s*(其他|其它|别的|另外的?|新的|个别的)?\s*(的|吧|呢|看看)?$",
        RegexOptions.Compiled);

    private static readonly Regex RejectAsk = new(
        @"^(?<name>.{0,14}?)[的]?\s*(不合适|不满意|不行|不好|不太行|不想要|不要这个|太贵|不靠谱)\s*(吧|啊|呀|了)?$",
        RegexOptions.Compiled);

    /// <summary>「换一个」类追问 → (是不是, 说的哪一项)。项名为空表示没点名，按上一轮给过的建议整体重来。</summary>
    public static (bool Revise, string? Name) ParseRevise(string message)
    {
        var m = Regex.Replace(message.Trim(), @"[。！!，,~～\s]+$", "");
        foreach (var re in new[] { ReviseAsk, RejectAsk })
        {
            var hit = re.Match(m);
            if (!hit.Success) continue;
            var name = hit.Groups["name"].Value.Trim();
            // 「这个」「那个」不是项名，当作没点名
            if (Regex.IsMatch(name, @"^(这|那|它|他)[个条项]?$")) name = "";
            return (true, name.Length == 0 ? null : name);
        }
        return (false, null);
    }

    private static readonly Regex QuestionAsk = new(
        @"[?？]$|什么区别|怎么(选|定|算|填|理解)|为什么|要不要|需要吗|是什么意思|什么是|多少合适|合适吗|可以吗|行不行", RegexOptions.Compiled);

    /// <summary>规则规划（演示档、模型不可用时的兜底）：一句只派一个活，宁可保守。
    /// 没命中任何意图即视为在回答当前问题（fill，值为整句）。</summary>
    public static IReadOnlyList<PlanAction> PlanByRules(string message)
    {
        var m = Regex.Replace(message.Trim(), @"[。！!，,~～\s]+$", "");
        if (m.Length == 0) return [];
        switch (DetectCommand(m))
        {
            case ChatCommand.Skip: return [new PlanAction("skip")];
            case ChatCommand.Render: return [new PlanAction("render")];
            case ChatCommand.Summary: return [new PlanAction("summary")];
        }
        if (AdoptAll.IsMatch(m)) return [new PlanAction("adopt", Tags: [])];
        var one = AdoptOne.Match(m);
        if (one.Success) return [new PlanAction("adopt", Name: one.Groups[1].Value.Trim())];
        var pick = PickIndex.Match(m);
        if (pick.Success) return [new PlanAction("pick_base", Index: CnIndex(pick.Groups[1].Value))];
        var no = ProjectNoRef.Match(m);
        if (no.Success && (m.Length <= no.Length + 8 || Regex.IsMatch(m, "基准|用这个|就这个|选这个|参考")))
            return [new PlanAction("pick_base", ProjectNo: no.Groups[1].Value)];
        if (LedgerAsk.IsMatch(m)) return [new PlanAction("ledger")];
        // 对上一轮的建议不买账：接着给新方案，而不是回一句「没识别到可入表的信息」
        var rev = ParseRevise(m);
        if (rev.Revise) return [new PlanAction("advise", Question: message.Trim(), Name: rev.Name)];
        // 工况说明优先于泛泛的「推荐」：说了做什么反应，要的是按工况选型，不是照抄历史
        if (ConditionTell.IsMatch(m)) return [new PlanAction("advise", Question: message.Trim())];
        if (SuggestAsk.IsMatch(m)) return [new PlanAction("suggest", Tags: [])];
        if (QuestionAsk.IsMatch(m)) return [new PlanAction("ask", Question: message.Trim())];
        return [new PlanAction("fill", Value: message.Trim())];
    }

    private static int CnIndex(string s) => s switch
    {
        "一" or "1" => 1, "二" or "2" => 2, "三" or "3" => 3, "四" or "4" => 4, "五" or "5" => 5, _ => 0
    };

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
