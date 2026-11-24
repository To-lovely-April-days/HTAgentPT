using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HT.Agent.Application.Abstractions;

namespace HT.Agent.Application.Logic;

/// <summary>工具执行的结果。ForModel 交给模型；Compact 是上下文紧张时换上的短版；Label/Detail/Result 给界面上的「查了几步」。</summary>
/// <summary>Pause=true：这个工具要等用户回话（比如请用户确认），本轮执行完就停下，等用户的回答再接着跑。</summary>
/// <summary>Failed=true：这一步没办成（比如做的文件检查没过），界面上这一行打叉。</summary>
/// <summary>Empty：执行了但什么都没拿到（没搜到、打不开、0 条）。同一个工具连着几次 Empty/Failed，系统提醒模型别再换说法重试。</summary>
public record ToolOutcome(string ForModel, string Compact, string Label, string Detail, string? Result, bool Pause = false, bool Failed = false, bool Empty = false);

public record StepRecord(string Id, string Tool, string Label, string Detail, string? Result, string Status, double Seconds);

/// <summary>HitRoundLimit：能用工具的轮数用完了，最后一轮是被迫直接回答的。Truncated：最后一轮写到「回答最长」停了。Reasoning / ThinkSeconds：各轮思考过程拼起来、思考用时加起来。</summary>
public record LoopResult(string Answer, int Rounds, IReadOnlyList<StepRecord> Steps, bool HitRoundLimit, string? PausedCallId = null,
    bool Truncated = false, string Reasoning = "", double ThinkSeconds = 0);

/// <summary>ContextTokens：模型服务的上下文长度（0=不知道）。知道时每轮按已用的估一下，回答上限不超出剩下的，免得模型服务直接拒掉。</summary>
public record AssistantLoopOptions(int MaxRounds = 6, bool Think = true, int MaxTokens = 6000, double Temperature = 0.2, int ContextChars = 40000,
    string? Effort = null, double? TopP = null, int? TopK = null, double? PresencePenalty = null, int ContextTokens = 0,
    double CharsPerToken = 1.3);

/// <summary>通用助手的回合循环：把问题和工具交给模型 → 模型要查什么就执行什么、把结果交回去 → 直到它直接回答。
/// 这里不判断问题类型、不规定先查什么后查什么——全由模型决定。系统只负责：执行工具、限制回合数、
/// 上下文快满时把早先的工具结果换成短版、把过程实时推给界面。</summary>
public sealed class AssistantLoop(IToolChatClient client, AssistantLoopOptions options)
{
    /// <summary>模型服务能准确算 token 的（本地 vLLM）：快满时按准确数算，不靠粗估。</summary>
    private readonly ITokenCounter? counter = client as ITokenCounter;

    public async Task<LoopResult> RunAsync(
        List<AgentMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        Func<ToolCall, CancellationToken, Task<ToolOutcome>> execute,
        Func<QaEvent, Task> emit,
        CancellationToken ct,
        Func<string?>? continueHint = null,
        Func<int>? bonusRounds = null,
        Func<IReadOnlyList<ToolCall>, string?>? progressHint = null,
        Func<string?>? limitHint = null,
        string stepPrefix = "s",
        Func<Task>? afterTools = null)
    {
        // afterTools：每轮工具都执行完、结果写回对话后调一次（存进度：服务器中途断了，重启后能从这里接着做）
        // stepPrefix：步骤编号的前缀。同一问里再跑一遍（自查后补缺口）用别的前缀，免得和第一遍的步骤撞号。
        // progressHint：每轮工具执行完问一句（比如计划里这一步已经查了很多次），有话就附在这轮最后一个工具结果后面。
        // limitHint：轮数用完、下一轮只能直接回答时，交给模型的话（还有哪几步没做、回答里怎么交代）。
        // continueHint：模型不调工具、要收尾时问一句「还有没做完的吗」——有就把这句话交回去让它接着做（最多 MaxNudges 次）。
        // bonusRounds：额外放宽的回合数（列了 N 步计划的，多给 N 回合，免得计划做到一半回合数先用完）。
        var steps = new List<StepRecord>();
        var nudges = 0;
        var compacts = new Dictionary<int, string>(); // messages 下标 → 短版
        var stepNo = 0;
        var reasoning = new System.Text.StringBuilder();
        var thinkSeconds = 0.0;
        var widen = false;      // 上一轮工具参数写到长度上限被截断了：这一轮把长度放宽一倍重来（只放宽一次）
        var widened = false;
        var misses = new Dictionary<string, int>();   // 每个工具连着几次没结果
        var limitNoted = false;
        var written = new System.Text.StringBuilder();   // 回答写到长度上限、系统让它接着写时，前面已经写出来的
        var continues = 0;
        var noThink = false;       // 上一轮想完了一个字没写就到了长度上限：这一轮关掉思考，额度全给回答
        var overflows = 0;         // 模型服务说放不下（超出上下文）的次数：按它报的实际数字重估、压短再发，最多两次

        // 按模型服务的分词器准确算一次，定下「几个字一个 token」：只在粗估过了上下文四成时算（平时不多一次请求）
        async Task CalibrateAsync(int sentTools, IReadOnlyList<ToolSpec> liveTools, bool th)
        {
            if (counter is null) return;
            var chars = PromptChars(messages, sentTools);
            if (EstimateTokens(chars, options.CharsPerToken) < options.ContextTokens * 0.4) return;
            if (await counter.CountAsync(messages, liveTools, th, ct) is not { Tokens: > 0 } n) return;
            var measured = n.IncludesTools ? chars : chars - sentTools;
            options = options with
            {
                CharsPerToken = Math.Clamp(measured / (double)n.Tokens * 0.97, 0.5, 4),
                ContextTokens = n.ContextTokens > 0 ? n.ContextTokens : options.ContextTokens,
            };
        }

        // 换成短版还放不下：让模型把早先查到的、做过的整理成要点，替换原文（最近一轮的结果、系统提示词、最后一句用户的话不动）
        async Task<bool> SummarizeAsync(int budget)
        {
            var picked = Compactable(messages);
            var total = picked.Sum(i => messages[i].Content!.Length);
            if (picked.Count == 0 || total < 3000) return false;
            // 交给整理的那一段自己也得放得下：最多用上下文的六成
            var maxIn = (int)(options.ContextTokens * 0.6 * options.CharsPerToken);
            var input = new StringBuilder();
            var used = new List<int>();
            foreach (var i in picked)
            {
                var part = $"【{used.Count + 1}】{(messages[i].Role == "tool" ? "查到的" : messages[i].Role == "user" ? "用户说的" : "助手写的")}\n{Clip(messages[i].Content!, 8000)}\n\n";
                if (input.Length + part.Length > maxIn && used.Count > 0) break;
                input.Append(part);
                used.Add(i);
            }
            var usedChars = used.Sum(i => messages[i].Content!.Length);
            var target = Math.Clamp(usedChars / 4, 800, 4000);
            var id = $"{stepPrefix}{++stepNo}";
            await emit(new QaEvent("step", new { id, tool = "compact", label = "整理资料", detail = $"前面查到的有 {usedChars} 字，放不下了，整理成要点接着做", status = "running" }));
            var sw = Stopwatch.StartNew();
            string summary;
            try
            {
                var r = await client.RunRoundAsync(
                    [new AgentMessage("system", SummaryPrompt(target)), new AgentMessage("user", input.ToString())], [],
                    new ToolRoundOptions(false, false, (int)(target / options.CharsPerToken * 1.5) + 300, 0.2), null, ct);
                summary = StripThinking(r.Content);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { summary = ""; }
            var ok = summary.Length >= 100 && summary.Length < usedChars / 2;
            var rec = new StepRecord(id, "compact", "整理资料", $"前面查到的有 {usedChars} 字，放不下了，整理成要点接着做",
                ok ? $"{used.Count} 段整理成 {summary.Length} 字" : "没整理成，改为只留每段开头", ok ? "done" : "error", Math.Round(sw.Elapsed.TotalSeconds, 1));
            steps.Add(rec);
            await emit(new QaEvent("step", new { id, tool = rec.Tool, label = rec.Label, detail = rec.Detail, result = rec.Result, status = rec.Status, seconds = rec.Seconds }));
            if (!ok) return false;
            // 要点放进最早那段查到的资料里（工具调用和结果一一对应的关系不动），其余几段只留一句
            var host = used.FirstOrDefault(i => messages[i].Role == "tool", used[0]);
            foreach (var i in used)
            {
                messages[i] = messages[i] with { Content = i == host ? SummaryMark + summary : MergedMark };
                compacts.Remove(i);
            }
            return true;
        }
        var thinkDropped = false;  // 只关一次
        for (var round = 1; ; round++)
        {
            var toolChars = ToolChars(tools);   // 工具按需加载：中途加载了专用工具，下一轮的工具清单就变长了
            var allowTools = round <= options.MaxRounds + Math.Max(0, bonusRounds?.Invoke() ?? 0) && tools.Count > 0;
            var cut = !allowTools && tools.Count > 0;   // 轮数用完了
            if (cut && !limitNoted)
            {
                limitNoted = true;
                if (limitHint?.Invoke() is { Length: > 0 } lh) messages.Add(new AgentMessage("user", lh));
            }
            var think = options.Think && !noThink;
            noThink = false;
            // 查了好几次资料后上下文快满了：这一轮的回答上限会被压到很小，思考就能把它用光、正文一个字写不出来，甚至整个发不出去。
            // ⓪ 快满了（粗估过四成）：让模型服务按分词器准确算一次，按准确的比例估后面的；
            // ① 先把早先的工具结果换成短版，给回答留出余量（本档「回答最长」，最多上下文的四分之一）；
            // ② 还放不下（历史、前一遍的回答、没有短版的原文很长）：让模型把早先的整理成要点（数字、标准号、引用编号都留着）；
            // ③ 整理不了或整理完还放不下：早先的内容只留头尾，至少给回答留出上下文的八分之一
            if (options.ContextTokens > 0)
            {
                var sentTools = allowTools ? toolChars : 0;
                await CalibrateAsync(sentTools, allowTools ? tools : [], think);
                var room = AnswerRoomChars(messages, sentTools, options, options.ContextTokens / 4);
                Shrink(messages, compacts, room);
                if (MessageChars(messages) > room && await SummarizeAsync(room)) await CalibrateAsync(sentTools, allowTools ? tools : [], think);
                Squeeze(messages, AnswerRoomChars(messages, sentTools, options, options.ContextTokens / 8));
            }
            var streamed = false;
            var roundOptions = widen ? options with { MaxTokens = options.MaxTokens * 2 } : options;
            widen = false;
            ToolRoundResult result;
            try
            {
                result = await client.RunRoundAsync(messages, tools,
                    new ToolRoundOptions(allowTools, think, RoundMaxTokens(messages, allowTools ? toolChars : 0, roundOptions), options.Temperature,
                        options.Effort, options.TopP, options.TopK, options.PresencePenalty),
                    async text => { streamed = true; await emit(new QaEvent("delta", new { text })); }, ct);
            }
            catch (ContextOverflowException ox) when (overflows < 2 && !streamed)
            {
                // 放不下：粗估偏少了（表格、代码、英文的字和 token 比例跟中文不一样）。按模型服务报的实际输入长度重新定比例，
                // 上限也按它报的；下一轮开头照新的比例把早先的内容压短，再发一次（不算一轮）
                overflows++;
                var chars = PromptChars(messages, allowTools ? toolChars : 0);
                var cpt = ox.PromptTokens > 0 ? chars / (double)ox.PromptTokens : options.CharsPerToken * 0.8;
                options = options with
                {
                    CharsPerToken = Math.Max(0.5, Math.Min(options.CharsPerToken, cpt) * 0.9),
                    ContextTokens = ox.ContextTokens > 0 ? ox.ContextTokens : options.ContextTokens,
                };
                round--;
                continue;
            }
            thinkSeconds += result.ThinkSeconds;
            if (!string.IsNullOrWhiteSpace(result.Reasoning) && reasoning.Length < MaxReasoning)
            {
                if (reasoning.Length > 0) reasoning.Append("\n\n");
                reasoning.Append(result.Reasoning.Trim());
            }

            var calls = result.ToolCalls;
            var content = result.Content;
            if (calls.Count == 0 && allowTools)
            {
                // 有的解析器在流式下把工具调用原样吐成文字：认出来照样执行
                var parsed = ToolCallTextParser.Parse(content);
                if (parsed.Calls.Count > 0) { calls = parsed.Calls; content = parsed.Rest; }
            }

            if (calls.Count == 0 && result.FinishReason == "length" && StripThinking(content).Length == 0 && think && !thinkDropped)
            {
                // 思考把这一轮的额度用光了，正文一个字没写：不算一轮，关掉思考重来（想过的已经记在「看思路」里）
                thinkDropped = true;
                noThink = true;
                if (streamed) await emit(new QaEvent("replace", new { text = written.ToString() }));
                round--;
                continue;
            }

            if (calls.Count == 0 && allowTools && nudges < MaxNudges && continueHint?.Invoke() is { Length: > 0 } hint)
            {
                // 本地模型常见：说一句「接下来我将……」就停了，计划后面几步没做。把它说的留在上下文里，提醒它接着做
                nudges++;
                if (streamed) await emit(new QaEvent("replace", new { text = "" }));
                messages.Add(new AgentMessage("assistant", StripThinking(content)));
                messages.Add(new AgentMessage("user", hint));
                continue;
            }

            if (calls.Count == 0)
            {
                var part = StripThinking(content);
                // 回答写到「回答最长」停了：不交给用户去点，系统让它从断的地方接着写（最多接 MaxContinues 次），界面上接在后面
                if (result.FinishReason == "length" && continues < MaxContinues && part.Trim().Length > 0)
                {
                    continues++;
                    if (part != content) await emit(new QaEvent("replace", new { text = written + part }));
                    written.Append(part);
                    messages.Add(new AgentMessage("assistant", part));
                    messages.Add(new AgentMessage("user", "接着上面没写完的地方往下写：直接接上断的那句，不要重复已经写过的，也不要加开场白。"));
                    continue;
                }
                var answer = written + part;
                if (part != content) await emit(new QaEvent("replace", new { text = answer }));
                return new LoopResult(answer.Trim(), round, steps, cut,
                    Truncated: result.FinishReason == "length", Reasoning: Clip(reasoning.ToString(), MaxReasoning), ThinkSeconds: thinkSeconds);
            }

            // 这一轮是「先说一句再去查」：说的话不算答案，界面上清掉（接着写了一半又去调工具的，前面写的也不算）
            if (streamed || written.Length > 0) await emit(new QaEvent("replace", new { text = "" }));
            written.Clear();

            // 工具参数（比如一整页代码）写到「回答最长」被截断了：参数是半截的，执行了只会报「参数不对」。
            // 第一次：不执行，长度放宽一倍把这一轮重来；放宽了还截断：告诉模型拆短了再写
            if (result.FinishReason == "length")
            {
                if (!widened)
                {
                    widened = true;
                    widen = true;
                    round--;
                    continue;
                }
                messages.Add(new AgentMessage("assistant", StripThinking(content), calls));
                foreach (var call in calls)
                {
                    var id = $"{stepPrefix}{++stepNo}";
                    var argChars = call.ArgumentsJson.Length;
                    var rec = new StepRecord(id, call.Name, ToolLabel(call.Name), Describe(call), "写太长，被截断了", "error", 0);
                    steps.Add(rec);
                    await emit(new QaEvent("step", new { id, tool = call.Name, label = rec.Label, detail = rec.Detail, result = rec.Result, status = "error", seconds = 0 }));
                    messages.Add(new AgentMessage("tool",
                        $"没有执行：这次调用的参数写了 {argChars} 字，到了单次回答的长度上限被截断，内容是半截的。"
                        + "把它拆短再做：代码分几次写（第一次 open(路径, 'w') 写前一部分，后面每次 open(路径, 'a') 接着写，最后一次再运行或检查），"
                        + "或者先做一个精简版，回答里说明哪些部分可以接着补。",
                        ToolCallId: call.Id));
                }
                continue;
            }
            messages.Add(new AgentMessage("assistant", StripThinking(content), calls));

            string? pausedOn = null;
            foreach (var call in calls)
            {
                var id = $"{stepPrefix}{++stepNo}";
                await emit(new QaEvent("step", new { id, tool = call.Name, label = ToolLabel(call.Name), detail = Describe(call), status = "running" }));
                var sw = Stopwatch.StartNew();
                ToolOutcome outcome;
                string status = "done";
                try { outcome = await execute(call, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    status = "error";
                    outcome = new ToolOutcome($"工具出错：{ex.Message}。可以换个参数再试，或直接根据已有信息回答。",
                        "工具出错", ToolLabel(call.Name), Describe(call), "出错了");
                }
                if (outcome.Failed) status = "error";
                var rec = new StepRecord(id, call.Name, outcome.Label, outcome.Detail, outcome.Result, status, Math.Round(sw.Elapsed.TotalSeconds, 1));
                steps.Add(rec);
                await emit(new QaEvent("step", new { id, tool = call.Name, label = rec.Label, detail = rec.Detail, result = rec.Result, status, seconds = rec.Seconds }));
                if (outcome.Pause) { pausedOn = call.Id; continue; } // 这个调用的结果要等用户来填，先不写回去
                var forModel = outcome.ForModel;
                // 同一个工具连着 3 次没结果：别再换说法重试了（搜索被拦、资料里本来就没有时，模型会一直换词搜，把轮数耗光）
                var miss = outcome.Empty || outcome.Failed ? misses.GetValueOrDefault(call.Name) + 1 : 0;
                misses[call.Name] = miss;
                if (miss > 0 && miss % 3 == 0)
                    forModel += $"\n（「{outcome.Label}」已经连着 {miss} 次没有结果。不要再换说法重试：用已经查到的往下做；实在缺的，在回答里写明没查到。）";
                messages.Add(new AgentMessage("tool", forModel, ToolCallId: call.Id));
                compacts[messages.Count - 1] = outcome.Compact;
            }
            if (pausedOn is null && progressHint?.Invoke(calls) is { Length: > 0 } ph && messages[^1].Role == "tool")
                messages[^1] = messages[^1] with { Content = messages[^1].Content + "\n（" + ph + "）" };
            Shrink(messages, compacts, options.ContextChars);
            if (pausedOn is null && afterTools is not null) await afterTools();
            if (pausedOn is not null)
                return new LoopResult("", round, steps, false, pausedOn, Reasoning: Clip(reasoning.ToString(), MaxReasoning), ThinkSeconds: thinkSeconds);
        }
    }

    /// <summary>模型想收尾、但还有没做完的事时，最多提醒几次。</summary>
    public const int MaxNudges = 2;

    /// <summary>回答写到长度上限时，系统让它接着写最多几次。</summary>
    public const int MaxContinues = 2;

    /// <summary>界面上「看思路」最多留多少字。</summary>
    public const int MaxReasoning = 6000;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static int ToolChars(IReadOnlyList<ToolSpec> tools)
        => tools.Sum(t => t.Name.Length + t.Description.Length + JsonSerializer.Serialize(t.Parameters).Length);

    /// <summary>粗估 token：中文约 1.3 字一个，英文和 JSON 更省——按 1.3 估偏多，宁可回答上限给小一点也别超。
    /// 模型服务报过实际输入长度的，按实际的比例估（cpt）。</summary>
    public static int EstimateTokens(int chars, double cpt = 1.3) => (int)Math.Ceiling(chars / cpt) + 64;

    /// <summary>这一轮发过去的大致字数：工具清单 + 每条消息（含工具调用的参数）。</summary>
    public static int PromptChars(IReadOnlyList<AgentMessage> messages, int toolChars)
        => toolChars + messages.Sum(m => (m.Content?.Length ?? 0) + 8 + (m.ToolCalls?.Sum(c => c.Name.Length + c.ArgumentsJson.Length) ?? 0));

    /// <summary>这一轮的回答上限：不超过本档的「回答最长」，也不超过上下文里剩下的。</summary>
    public static int RoundMaxTokens(IReadOnlyList<AgentMessage> messages, int toolChars, AssistantLoopOptions o)
    {
        if (o.ContextTokens <= 0) return o.MaxTokens;
        var left = o.ContextTokens - EstimateTokens(PromptChars(messages, toolChars), o.CharsPerToken);
        return Math.Clamp(left, Math.Min(256, o.MaxTokens), o.MaxTokens);
    }

    /// <summary>给这一轮的回答留出余量（本档回答最长，不超过 maxReserve）时，消息正文最多能有多少字。</summary>
    public static int AnswerRoomChars(IReadOnlyList<AgentMessage> messages, int toolChars, AssistantLoopOptions o, int maxReserve)
    {
        var reserve = Math.Min(o.MaxTokens, maxReserve);
        var overhead = PromptChars(messages, toolChars) - messages.Sum(m => m.Content?.Length ?? 0);
        return Math.Max(0, (int)((o.ContextTokens - reserve - 64) * o.CharsPerToken) - overhead);
    }

    private static int MessageChars(IReadOnlyList<AgentMessage> messages) => messages.Sum(m => m.Content?.Length ?? 0);

    internal const string SummaryMark = "（前面几步查到的要点，原文太长已整理压缩；引用编号照原文）\n";
    internal const string MergedMark = "（这段已并入前面整理的要点）";

    /// <summary>能整理成要点的：早先的工具结果和对话（够长、还没整理过的）。系统提示词、最后一句用户的话、最近一轮的工具调用和结果不动。</summary>
    public static List<int> Compactable(IReadOnlyList<AgentMessage> messages)
    {
        var lastUser = -1;
        for (var i = messages.Count - 1; i >= 0; i--) if (messages[i].Role == "user") { lastUser = i; break; }
        var lastCalls = messages.Count;
        for (var i = messages.Count - 1; i >= 0; i--) if (messages[i].Role == "assistant" && messages[i].ToolCalls is { Count: > 0 }) { lastCalls = i; break; }
        var list = new List<int>();
        for (var i = 1; i < messages.Count; i++)
        {
            if (i == lastUser || i >= lastCalls) continue;
            if (messages[i].Role is not ("tool" or "user" or "assistant")) continue;
            if (messages[i].Content is not { Length: > 300 } c || c.StartsWith(SummaryMark) || c == MergedMark) continue;
            list.Add(i);
        }
        return list;
    }

    private static string SummaryPrompt(int target) => $"""
        你在帮助手整理它前面查到的资料和做过的事，好腾出上下文空间接着做。把下面几段压成要点：
        - 保留所有数字和单位、参数、标准号和条款号、文件名、型号、算出来的结果、结论、还没解决的问题；
        - 资料里的引用编号（[1]、[2]……）原样留在对应要点后面，回答还要按编号引用；
        - 用户提过的要求和定下的做法要留着；
        - 去掉重复的、和问题无关的、客套话；
        - 只输出要点本身，不加开场白，不超过 {target} 字。
        """;

    private const string Omitted = "\n…（这段太长，为了放得下已省略；前面引用过的编号照样有效）";

    /// <summary>换成短版还放不下时（历史、前一遍的回答、没有短版的工具结果很长）：从最早的开始只留头尾。
    /// 先剪工具结果，再剪对话；系统提示词和最后一条用户的话不动。</summary>
    public static void Squeeze(List<AgentMessage> messages, int budget)
    {
        var total = messages.Sum(m => m.Content?.Length ?? 0);
        if (total <= budget) return;
        var lastUser = messages.FindLastIndex(m => m.Role == "user");
        void Cut(int i, string content)
        {
            total += content.Length - (messages[i].Content?.Length ?? 0);
            messages[i] = messages[i] with { Content = content };
        }
        for (var i = 1; i < messages.Count && total > budget; i++)
            if (messages[i].Role == "tool" && messages[i].Content is { Length: > 800 } c && !c.StartsWith(SummaryMark))
                Cut(i, c[..500] + Omitted);
        for (var i = 1; i < messages.Count && total > budget; i++)
            if (i != lastUser && messages[i].Role is "user" or "assistant" && messages[i].Content is { Length: > 1600 } c)
                Cut(i, c[..800] + "\n…（中间省略）…\n" + c[^500..]);
    }

    /// <summary>工具结果总量超过预算时，从最早的开始换成短版（短版里保留编号和出处，模型仍能引用）。</summary>
    public static void Shrink(List<AgentMessage> messages, Dictionary<int, string> compacts, int budget)
    {
        int Total() => messages.Sum(m => m.Content?.Length ?? 0);
        foreach (var i in compacts.Keys.OrderBy(k => k).ToList())
        {
            if (Total() <= budget) return;
            messages[i] = messages[i] with { Content = compacts[i] };
            compacts.Remove(i);
        }
    }

    private static string StripThinking(string s)
    {
        s = Regex.Replace(s ?? "", @"<think>[\s\S]*?</think>", "");
        // 想到一半就到了长度上限：<think> 没有收尾，后面全是思考，不是回答
        var open = s.IndexOf("<think>", StringComparison.Ordinal);
        if (open >= 0) s = s[..open];
        return s.Replace("</think>", "").Trim();
    }

    public static string ToolLabel(string name) => name switch
    {
        "search_documents" => "搜索资料",
        "read_document" => "打开原文",
        "query_projects" => "查项目",
        "read_attachments" => "看附件",
        "calculate" => "计算",
        "convert_unit" => "单位换算",
        "ask_user" => "请你确认",
        "run_python" => "运行计算",
        "chemical_info" => "化学品数据",
        "hazard_lookup" => "查危险工艺和危化品",
        "process_sim" => "流程模拟",
        "make_ppt" => "做 PPT",
        "find_tools" => "加载工具",
        "plan" => "列计划",
        "web_search" => "联网搜索",
        "fetch_url" => "打开网页",
        "compare_documents" => "文件对比",
        "remember" => "记住",
        "use_skill" => "翻看技能",
        "forget" => "删记忆",
        "inquiry_form" => "询价缺项单",
        "search_standards" => "查标准规范",
        "search_literature" => "文献检索",
        "view_image" => "看图",
        "ocr_image" => "识别文字",
        "translate_file" => "整份翻译",
        "search_prices" => "查价格",
        "cost_estimate" => "材料成本估算",
        "show_table" => "生成表格",
        "show_block" => "显示组件",
        "note_correction" => "记下纠正",
        "delegate" => "分头查",
        "browser_open" => "打开网页",
        "browser_read" => "读页面",
        "browser_act" => "操作网页",
        "browser_look" => "看网页画面",
        _ => name
    };

    /// <summary>界面上这一步的说明：把参数里最有信息量的几项拼出来。</summary>
    public static string Describe(ToolCall call)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson) as JsonObject;
            if (node is null) return "";
            if (call.Name == "find_tools")
                return string.Join("、", new[] { node["names"] is JsonArray ns ? string.Join("、", ns.Select(x => x?.ToString())) : node["names"]?.ToString(), node["need"]?.ToString() }.Where(x => !string.IsNullOrEmpty(x)));
            if (call.Name == "delegate") return node["tasks"] is JsonArray ts ? $"{ts.Count} 个子任务，同时进行" : "分头查";
            if (call.Name == "make_ppt")
                return string.Join(" · ", new[] { node["file_name"]?.ToString(), node["pages"] is JsonArray ps ? $"{ps.Count} 页" : node["redo"] is not null ? $"重做《{node["redo"]}》" : null,
                    node["template"] is { } tp ? $"模板「{tp}」" : null }.Where(x => !string.IsNullOrEmpty(x)));
            var parts = node.Where(kv => kv.Value is not null && kv.Value.ToString().Length > 0)
                .Select(kv => kv.Key is "query" or "expression" or "keyword" || call.Name == "use_skill" ? kv.Value!.ToString() : $"{kv.Key}={kv.Value}");
            var s = string.Join("，", parts);
            return s.Length <= 120 ? s : s[..120] + "…";
        }
        catch (JsonException) { return call.ArgumentsJson.Length <= 120 ? call.ArgumentsJson : call.ArgumentsJson[..120]; }
    }
}

/// <summary>认出写成文字的工具调用（Hermes 风格 &lt;tool_call&gt;{json}&lt;/tool_call&gt;，或 Qwen XML 风格
/// &lt;function=名&gt;&lt;parameter=键&gt;值&lt;/parameter&gt;）。认不出就原样返回。</summary>
public static class ToolCallTextParser
{
    private static readonly Regex Hermes = new(@"<tool_call>\s*(\{[\s\S]*?\})\s*</tool_call>", RegexOptions.Compiled);
    private static readonly Regex Xml = new(@"(?:<tool_call>\s*)?<function=([\w.\-]+)>([\s\S]*?)</function>(?:\s*</tool_call>)?", RegexOptions.Compiled);
    private static readonly Regex Param = new(@"<parameter=([\w.\-]+)>\s*([\s\S]*?)\s*</parameter>", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public record Parsed(IReadOnlyList<ToolCall> Calls, string Rest);

    public static Parsed Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || (!text.Contains("<tool_call>") && !text.Contains("<function="))) return new([], text ?? "");
        var calls = new List<ToolCall>();
        var rest = Hermes.Replace(text, m =>
        {
            try
            {
                var o = JsonNode.Parse(m.Groups[1].Value) as JsonObject;
                var name = o?["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) return m.Value;
                var args = o!["arguments"];
                calls.Add(new ToolCall($"call_t{calls.Count}", name, args is JsonValue v && v.TryGetValue<string>(out var str) ? str : args?.ToJsonString(Plain) ?? "{}"));
                return "";
            }
            catch (JsonException) { return m.Value; }
        });
        rest = Xml.Replace(rest, m =>
        {
            var obj = new JsonObject();
            foreach (Match p in Param.Matches(m.Groups[2].Value)) obj[p.Groups[1].Value] = Value(p.Groups[2].Value);
            calls.Add(new ToolCall($"call_t{calls.Count}", m.Groups[1].Value, obj.ToJsonString(Plain)));
            return "";
        });
        return new(calls, rest.Trim());
    }

    private static JsonNode? Value(string raw)
    {
        var s = raw.Trim();
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        if (s is "true" or "false") return JsonValue.Create(s == "true");
        if (s.StartsWith('{') || s.StartsWith('['))
            try { return JsonNode.Parse(s); } catch (JsonException) { }
        return JsonValue.Create(s);
    }
}
