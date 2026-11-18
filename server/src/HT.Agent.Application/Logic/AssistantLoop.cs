using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HT.Agent.Application.Abstractions;

namespace HT.Agent.Application.Logic;

/// <summary>工具执行的结果。ForModel 交给模型；Compact 是上下文紧张时换上的短版；Label/Detail/Result 给界面上的「查了几步」。</summary>
/// <summary>Pause=true：这个工具要等用户回话（比如请用户确认），本轮执行完就停下，等用户的回答再接着跑。</summary>
public record ToolOutcome(string ForModel, string Compact, string Label, string Detail, string? Result, bool Pause = false);

public record StepRecord(string Id, string Tool, string Label, string Detail, string? Result, string Status, double Seconds);

public record LoopResult(string Answer, int Rounds, IReadOnlyList<StepRecord> Steps, bool HitRoundLimit, string? PausedCallId = null);

public record AssistantLoopOptions(int MaxRounds = 6, bool Think = true, int MaxTokens = 6000, double Temperature = 0.2, int ContextChars = 40000);

/// <summary>通用助手的回合循环：把问题和工具交给模型 → 模型要查什么就执行什么、把结果交回去 → 直到它直接回答。
/// 这里不判断问题类型、不规定先查什么后查什么——全由模型决定。系统只负责：执行工具、限制回合数、
/// 上下文快满时把早先的工具结果换成短版、把过程实时推给界面。</summary>
public sealed class AssistantLoop(IToolChatClient client, AssistantLoopOptions options)
{
    public async Task<LoopResult> RunAsync(
        List<AgentMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        Func<ToolCall, CancellationToken, Task<ToolOutcome>> execute,
        Func<QaEvent, Task> emit,
        CancellationToken ct)
    {
        var steps = new List<StepRecord>();
        var compacts = new Dictionary<int, string>(); // messages 下标 → 短版
        var stepNo = 0;
        for (var round = 1; ; round++)
        {
            var allowTools = round <= options.MaxRounds && tools.Count > 0;
            var streamed = false;
            var result = await client.RunRoundAsync(messages, tools,
                new ToolRoundOptions(allowTools, options.Think, options.MaxTokens, options.Temperature),
                async text => { streamed = true; await emit(new QaEvent("delta", new { text })); }, ct);

            var calls = result.ToolCalls;
            var content = result.Content;
            if (calls.Count == 0 && allowTools)
            {
                // 有的解析器在流式下把工具调用原样吐成文字：认出来照样执行
                var parsed = ToolCallTextParser.Parse(content);
                if (parsed.Calls.Count > 0) { calls = parsed.Calls; content = parsed.Rest; }
            }

            if (calls.Count == 0)
            {
                var answer = StripThinking(content);
                if (answer != content) await emit(new QaEvent("replace", new { text = answer }));
                return new LoopResult(answer.Trim(), round, steps, round > options.MaxRounds);
            }

            // 这一轮是「先说一句再去查」：说的话不算答案，界面上清掉
            if (streamed) await emit(new QaEvent("replace", new { text = "" }));
            messages.Add(new AgentMessage("assistant", StripThinking(content), calls));

            string? pausedOn = null;
            foreach (var call in calls)
            {
                var id = $"s{++stepNo}";
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
                var rec = new StepRecord(id, call.Name, outcome.Label, outcome.Detail, outcome.Result, status, Math.Round(sw.Elapsed.TotalSeconds, 1));
                steps.Add(rec);
                await emit(new QaEvent("step", new { id, tool = call.Name, label = rec.Label, detail = rec.Detail, result = rec.Result, status, seconds = rec.Seconds }));
                if (outcome.Pause) { pausedOn = call.Id; continue; } // 这个调用的结果要等用户来填，先不写回去
                messages.Add(new AgentMessage("tool", outcome.ForModel, ToolCallId: call.Id));
                compacts[messages.Count - 1] = outcome.Compact;
            }
            Shrink(messages, compacts, options.ContextChars);
            if (pausedOn is not null) return new LoopResult("", round, steps, false, pausedOn);
        }
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
        => Regex.Replace(s ?? "", @"<think>[\s\S]*?</think>", "").Replace("<think>", "").Replace("</think>", "").Trim();

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
        "web_search" => "联网搜索",
        "fetch_url" => "打开网页",
        "compare_documents" => "文件对比",
        "remember" => "记住",
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
        _ => name
    };

    /// <summary>界面上这一步的说明：把参数里最有信息量的几项拼出来。</summary>
    public static string Describe(ToolCall call)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson) as JsonObject;
            if (node is null) return "";
            var parts = node.Where(kv => kv.Value is not null && kv.Value.ToString().Length > 0)
                .Select(kv => kv.Key is "query" or "expression" or "keyword" ? kv.Value!.ToString() : $"{kv.Key}={kv.Value}");
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
