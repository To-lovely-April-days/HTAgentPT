using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HT.Agent.Application.Logic;

/// <summary>助手做事的固定流程里，系统强制做的两段（中间的执行交给回合循环）：
/// ① 开工前判断：这一问是直接答，还是要几步的任务；要几步就列计划，用户没说清的按常见做法定下假设，
///    只有缺了就没法开始的才开工前一次问完；
/// ④⑤ 交付前自查 + 收尾：对照要求和计划看有没有硬缺口（有就让助手补一次），再整理出「按什么假设做的、还有什么没做」
///    和最后问用户的一句话加两三个按钮。
/// 这里只拼给模型的话、解析模型交回的 JSON，不调模型——调用在 AssistantService 里，方便单独测。</summary>
public static class TaskFlow
{
    public sealed record Option(string Label, string? Detail);
    public sealed record Question(string Text, List<Option> Options, bool Multiple);

    /// <summary>判断结果。Task=要几步的任务（Steps 至少 2 步）；Blocking=开工前非问不可的问题（一般为空）。</summary>
    public sealed record Triage(bool Task, List<string> Steps, List<string> Assumptions, List<Question> Blocking)
    {
        public static readonly Triage Direct = new(false, [], [], []);
    }

    /// <summary>自查 + 收尾。Problems=交出去前必须补的硬缺口；其余是回答下面那张收尾卡的内容。</summary>
    public sealed record Review(List<string> Problems, List<string> Assumptions, List<string> Left, string? Ask, List<string> Options)
    {
        public bool HasCard => Assumptions.Count > 0 || Left.Count > 0 || !string.IsNullOrEmpty(Ask);
    }

    public const int MaxSteps = TaskPlan.MaxItems, MaxAssumptions = 5, MaxBlocking = 4;

    // ── ① 开工前判断 ─────────────────────────────────────

    public static string TriagePrompt(string capabilities) => $$"""
        你是任务分诊员：用户的问题交给助手之前，先判断怎么做。只输出一个 JSON 对象，不要别的话。

        - 一两步就能答的（问一个事实或参数、解释概念、查一条记录、改一句话、闲聊）："mode":"direct"，其余字段留空。
        - 要好几步才能做完的（先查再算、查几处资料汇总对比、做文件或 PPT、写程序或界面、调研、对照协议……）："mode":"task"，
          steps 列 2–{{MaxSteps}} 步，每步一句话、动词开头、具体到这件事（如「查我司 50L 加氢釜的设计参数」），
          不要写「理解需求」「整理回答」「确认需求」这类空步骤，最后一步是交付的东西（写回答、做出文件）。
        - assumptions：用户没说清、但能按常见做法定下来的（规模、风格、型号、对比哪几家、用在什么设备上……），
          每条写成「按……」的一句话，最多 {{MaxAssumptions}} 条。能假设的都假设，不要问用户。
        - blocking：只有缺了就没法开始、或者猜错要整个返工的（不知道指的是哪个项目、哪份文件、哪个客户）才放这里，
          最多 {{MaxBlocking}} 个问题，每个 2–6 个选项（label 是选项，detail 一句区别）。绝大多数情况为空。
        - 用户这句是在回答上一轮的追问、或者说「接着做」：按上下文里接着要做的事来判断。

        助手能做的：{{capabilities}}

        只输出：{"mode":"direct 或 task","steps":["…"],"assumptions":["按…"],"blocking":[{"question":"…","options":[{"label":"…","detail":"…"}],"multiple":false}]}
        """;

    /// <summary>交给判断的内容：最近几轮对话（理解「接着做」「就按第二个」这类话）、附件名、这一问。</summary>
    public static string TriageInput(string question, IEnumerable<(string Role, string Text)> history, IEnumerable<string> attachments)
    {
        var sb = new StringBuilder();
        var h = history.Where(x => !string.IsNullOrWhiteSpace(x.Text)).TakeLast(4).ToList();
        if (h.Count > 0)
        {
            sb.AppendLine("最近的对话：");
            foreach (var (role, text) in h) sb.AppendLine($"{(role == "user" ? "用户" : "助手")}：{Clip(text.Trim(), 300)}");
            sb.AppendLine();
        }
        var files = attachments.ToList();
        if (files.Count > 0) sb.AppendLine("用户传的附件：" + string.Join("、", files.Take(8))).AppendLine();
        sb.Append("这一问：").Append(Clip(question.Trim(), 1500));
        return sb.ToString();
    }

    /// <summary>解析判断结果；看不懂就当直接答（照原来的方式交给助手自己判断，不耽误回答）。</summary>
    public static Triage ParseTriage(string text)
    {
        if (ObjectIn(text) is not { } o) return Triage.Direct;
        var steps = Strings(o["steps"], 60, MaxSteps);
        var assumptions = Strings(o["assumptions"], 80, MaxAssumptions);
        var blocking = (o["blocking"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(q => new Question(Clip(q["question"]?.ToString()?.Trim() ?? "", 200), Options(q["options"]), q["multiple"] is JsonValue mv && mv.TryGetValue<bool>(out var m) && m))
            .Where(q => q.Text.Length > 0 && q.Options.Count >= 2).Take(MaxBlocking).ToList();
        var isTask = string.Equals(o["mode"]?.ToString()?.Trim(), "task", StringComparison.OrdinalIgnoreCase) && steps.Count >= 2;
        return new Triage(isTask, isTask ? steps : [], isTask ? assumptions : [], blocking);
    }

    /// <summary>列好计划后附在这一问后面交给助手的话。</summary>
    public static string PlanNote(TaskPlan plan)
    {
        var sb = new StringBuilder("\n\n（系统先看了这个问题，已经列好计划、界面上显示着：\n");
        for (var i = 0; i < plan.Items.Count; i++) sb.AppendLine($"{i + 1}. {plan.Items[i].Title}{(plan.Items[i].Status == "now" ? "（现在做这步）" : "")}");
        if (plan.Assumptions.Count > 0) sb.AppendLine("用户没说清的地方按这些假设做：" + string.Join("；", plan.Assumptions));
        sb.Append("照计划一步步做：每开始一步用 plan 把它标 now、做完标 done（plan 每次给整张清单，标题照抄）；做着发现计划不对，用 plan 重列并写 reason。"
            + "不要停下来问用户，假设不合适的在回答里说一句。要交付文件的（PPT、Excel、Word……），用工具把文件做出来，不要把内容写在回答里代替文件。"
            + "所有步骤都做完再写最终回答。最终回答写交付的内容本身；按什么假设做的、还有什么没做，系统会在回答下面统一列出，回答末尾不用再列。）");
        return sb.ToString();
    }

    // ── ④⑤ 自查 + 收尾 ──────────────────────────────────

    public const string ReviewPrompt = """
        你是交付前的检查员。下面是用户的要求、助手的计划（每步的状态）、助手按的假设、写好的回答、做出的文件，以及系统查出来的问题。只输出一个 JSON 对象，不要别的话。

        - problems：交出去之前必须补上的硬缺口，最多 3 条：要求里明确要的东西没做（比如要 PPT 却没有做出文件）、计划里的步骤没做却说成做了、回答前后矛盾、回答明显没写完。
          措辞、格式、可以更详细这类小事不算。没有就空数组。
        - assumptions：这次按什么假设做的（用户没说清、助手自己定的；开工前定下的和做的过程中定的都算，按最后实际用的写），一句一条，最多 5 条；没有就空。
        - left：还没做的、没能核实的，一句一条，最多 4 条；没有就空。
        - question：最后问用户的一句话，帮用户决定下一步（比如「要按客户的实际工况再调整吗？」）；没必要问就空字符串。
        - options：给这个问题的 2–3 个回答按钮，每个不超过 12 个字，是用户点了就直接发给助手的话（比如「换成深色风格」「再加一页成本对比」「可以了」）。

        只输出：{"problems":[],"assumptions":[],"left":[],"question":"","options":[]}
        """;

    public static string ReviewInput(string question, TaskPlan plan, string answer, IEnumerable<string> files, IEnumerable<string> systemFound)
    {
        var sb = new StringBuilder();
        sb.AppendLine("用户的要求：" + Clip(question.Trim(), 1500)).AppendLine();
        if (plan.Items.Count > 0)
        {
            sb.AppendLine("计划：");
            foreach (var it in plan.Items)
                sb.AppendLine($"- {it.Title}：{it.Status switch { "done" => "做完", "now" => "正在做", "skip" => "没做", _ => "没开始" }}{(string.IsNullOrEmpty(it.Note) ? "" : "（" + it.Note + "）")}");
            sb.AppendLine();
        }
        if (plan.Assumptions.Count > 0) sb.AppendLine("开工前定下的假设：" + string.Join("；", plan.Assumptions)).AppendLine();
        var made = files.ToList();
        sb.AppendLine("做出的文件：" + (made.Count > 0 ? string.Join("、", made) : "没有")).AppendLine();
        var found = systemFound.ToList();
        if (found.Count > 0) sb.AppendLine("系统查出来的问题：" + string.Join("；", found)).AppendLine();
        sb.AppendLine("助手写好的回答：");
        // 回答很长时留头尾：缺口多在开头没提、结尾没写完
        sb.Append(answer.Length <= 6000 ? answer : answer[..4000] + "\n……（中间略）……\n" + answer[^2000..]);
        return sb.ToString();
    }

    public static Review ParseReview(string text)
    {
        if (ObjectIn(text) is not { } o) return new Review([], [], [], null, []);
        var ask = Clip(o["question"]?.ToString()?.Trim() ?? "", 120);
        var options = Strings(o["options"], 16, 3);
        return new Review(Strings(o["problems"], 160, 3), Strings(o["assumptions"], 80, MaxAssumptions), Strings(o["left"], 80, 4),
            ask.Length > 0 ? ask : null, ask.Length > 0 && options.Count >= 2 ? options : []);
    }

    /// <summary>自查发现缺口时交给助手的话：补上，再把完整回答重写一遍。</summary>
    public static string FixNote(IEnumerable<string> problems)
        => "交付前自查发现这些问题：\n" + string.Join("\n", problems.Select((p, i) => $"{i + 1}. {p}"))
           + "\n现在就补，不要问用户：能补的用工具补上（要交付文件的就用工具把文件做出来，不要把内容写在回答里代替；计划里对应的步骤用 plan 标 now、做完标 done），"
           + "然后把完整的最终回答重新写一遍（不要只写补了什么）；实在补不了的在回答里如实写明。";

    // ── 中断后接着做 ─────────────────────────────────────

    /// <summary>用户这句是不是要接着上次没做完的（「接着做」「继续」……）。问别的就不续，免得把旧现场套到新问题上。</summary>
    public static bool WantsResume(string question)
    {
        var q = question.Trim().TrimStart('（', '(', '【', '[').ToLowerInvariant();
        return q.StartsWith("接着") || q.StartsWith("继续") || q.StartsWith("continue");
    }

    /// <summary>从存下的进度续上时，附在对话最后交给助手的话。</summary>
    public static string ResumeNote(string question)
        => "（上一轮做到一半中断了：服务器重启、出错或用户点了停。上面是中断前已经做完的步骤和查到的结果，直接用，不要重新查；"
           + "计划里没做完的接着做（用 plan 标 now / done），做完写完整的最终回答。）\n用户说：" + Clip(question.Trim(), 1500);

    // ── 工具 ─────────────────────────────────────────────

    /// <summary>模型交回的文字里找出 JSON 对象（可能包在 ```json 里、前后带几句话）。</summary>
    public static JsonObject? ObjectIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // 思考过程（<think>…</think>）里可能也有花括号，先去掉
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<think>[\s\S]*?</think>", "");
        var a = text.IndexOf('{');
        var b = text.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try { return JsonNode.Parse(text[a..(b + 1)]) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static List<string> Strings(JsonNode? node, int clip, int max)
        => (node as JsonArray ?? []).Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "")
            .Where(s => s.Length > 0).Select(s => Clip(s, clip)).Distinct().Take(max).ToList();

    private static List<Option> Options(JsonNode? node)
    {
        var list = new List<Option>();
        foreach (var o in node as JsonArray ?? [])
        {
            if (o is JsonObject ob && ob["label"]?.ToString()?.Trim() is { Length: > 0 } l)
                list.Add(new Option(Clip(l, 80), ob["detail"]?.ToString()?.Trim() is { Length: > 0 } d ? Clip(d, 160) : null));
            else if (o is JsonValue v && v.TryGetValue<string>(out var l2) && l2.Trim().Length > 0)
                list.Add(new Option(Clip(l2.Trim(), 80), null));
        }
        return list.Take(6).ToList();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
