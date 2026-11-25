using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>OpenAI 兼容的工具调用（tools / tool_calls），流式。地址、模型名、密钥与普通对话共用一套运行时配置。
/// 本地 vLLM 要在启动参数里开 --enable-auto-tool-choice 和 --tool-call-parser，否则会回 400——
/// 这种情况抛 ToolCallingUnavailableException，调用方退回老的问答流程，不让用户看到报错。</summary>
public class OpenAiToolChatClient(IHttpClientFactory httpFactory, IRuntimeConfig config) : IToolChatClient, ITokenCounter
{
    /// <summary>多久一个字都没来算卡住（排队、长思考时字是一直在来的）。</summary>
    public static readonly TimeSpan Stall = TimeSpan.FromMinutes(4);

    /// <summary>哪些模型服务算不了 token（没有 /tokenize、或不认带工具清单）：记住，不再每轮去试。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NoTokenize = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NoToolsInTokenize = new();

    /// <summary>vLLM 的 /tokenize：和对话接口同样的消息、工具清单、模板参数，套上对话模板后算 token 数。只对本地 Qwen（vLLM）用。</summary>
    public async Task<TokenCount?> CountAsync(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, bool think, CancellationToken ct = default)
    {
        var baseUrl = (await config.GetStringAsync(ConfigKeys.ChatModelUrl, "http://127.0.0.1:8000/v1", ct)).TrimEnd('/');
        var model = await config.GetStringAsync(ConfigKeys.ChatModelName, "chat-default", ct);
        var provider = await config.GetStringAsync(ConfigKeys.ChatProvider, "", ct);
        if (!IsQwen(provider, model) || NoTokenize.ContainsKey(baseUrl)) return null;
        var root = System.Text.RegularExpressions.Regex.Replace(baseUrl, @"/v1$", "");
        var apiKey = (await config.GetStringAsync(ConfigKeys.ChatApiKey, "", ct)).Trim();
        var chat = BuildBody(model, messages, tools, new ToolRoundOptions(tools.Count > 0, think), qwen: true);
        var withTools = tools.Count > 0 && !NoToolsInTokenize.ContainsKey(baseUrl);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = chat["messages"]!.DeepClone(),
                ["add_generation_prompt"] = true,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = think },
            };
            if (withTools && chat["tools"] is { } t) body["tools"] = t.DeepClone();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{root}/tokenize")
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                };
                if (apiKey.Length > 0) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                using var resp = await httpFactory.CreateClient("assistant").SendAsync(req, cts.Token);
                if (resp.StatusCode == HttpStatusCode.NotFound) { NoTokenize[baseUrl] = true; return null; }
                if (!resp.IsSuccessStatusCode)
                {
                    // 老版本不认 tools 字段：不带工具清单再算一次（工具清单那部分由调用方粗估）
                    if (withTools) { NoToolsInTokenize[baseUrl] = true; withTools = false; continue; }
                    return null;
                }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                var r = doc.RootElement;
                if (!r.TryGetProperty("count", out var c) || !c.TryGetInt32(out var n) || n <= 0) return null;
                var max = r.TryGetProperty("max_model_len", out var m) && m.TryGetInt32(out var mm) ? mm : 0;
                return new TokenCount(n, withTools || tools.Count == 0, max);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }   // 算得太慢就不算了，按粗估
            catch (HttpRequestException) { return null; }
            catch (JsonException) { return null; }
        }
        return null;
    }

    public async Task<ToolRoundResult> RunRoundAsync(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools,
        ToolRoundOptions options, Func<string, Task>? onText, CancellationToken ct = default)
    {
        var baseUrl = (await config.GetStringAsync(ConfigKeys.ChatModelUrl, "http://127.0.0.1:8000/v1", ct)).TrimEnd('/');
        var model = await config.GetStringAsync(ConfigKeys.ChatModelName, "chat-default", ct);
        var apiKey = (await config.GetStringAsync(ConfigKeys.ChatApiKey, "", ct)).Trim();
        var provider = await config.GetStringAsync(ConfigKeys.ChatProvider, "", ct);

        var body = BuildBody(model, messages, tools, options, IsQwen(provider, model));
        var http = httpFactory.CreateClient("assistant");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (apiKey.Length > 0)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        // 不设总时限（长回答写十几分钟也正常），只看「卡住」：几分钟一个字都没来才算出故障，交给回合循环重试
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(Stall);
        HttpResponseMessage sent;
        try { sent = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ModelStalledException($"模型服务 {Stall.TotalMinutes:0} 分钟没有响应"); }
        using var resp = sent;
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == HttpStatusCode.BadRequest && tools.Count > 0 && LooksLikeToolsUnsupported(err))
                throw new ToolCallingUnavailableException($"模型端没有开启工具调用：{Trim(err, 300)}");
            if (resp.StatusCode == HttpStatusCode.BadRequest && ContextOverflowException.TryParse(err) is { } overflow) throw overflow;
            throw new HttpRequestException($"模型调用失败（{(int)resp.StatusCode}）：{Trim(err, 300)}", null, resp.StatusCode);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var acc = new StreamAccumulator();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var finished = false;
        for (;;)
        {
            ct.ThrowIfCancellationRequested();
            string? line;
            try { line = await reader.ReadLineAsync(idle.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new ModelStalledException($"模型服务 {Stall.TotalMinutes:0} 分钟没有吐出新内容"); }
            if (line is null) break;
            idle.CancelAfter(Stall);   // 来了内容就重新计时
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") { finished = true; break; }
            var text = acc.Feed(payload, clock.Elapsed.TotalSeconds);
            if (!string.IsNullOrEmpty(text) && onText is not null) await onText(text);
        }
        var result = acc.Result();
        // 流中途断了（模型服务重启、连接被掐）：没收到结束标记也没有结束原因，写出来的是半截，不能当成答完了
        if (!finished && result.FinishReason is null) throw new IOException("模型服务的输出中途断了");
        return result;
    }

    /// <summary>本地 Qwen：思考强度、top_k、presence_penalty 这些只有它（vLLM）认；在线服务不带，免得被拒。</summary>
    internal static bool IsQwen(string provider, string model)
        => provider.Contains("qwen", StringComparison.OrdinalIgnoreCase) || model.Contains("qwen", StringComparison.OrdinalIgnoreCase);

    internal static JsonObject BuildBody(string model, IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, ToolRoundOptions o, bool qwen = false)
    {
        var msgs = new JsonArray();
        foreach (var m in messages)
        {
            var node = new JsonObject { ["role"] = m.Role, ["content"] = m.Content ?? "" };
            if (m.ToolCalls is { Count: > 0 })
                node["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson }
                }).ToArray());
            if (m.ToolCallId is not null) node["tool_call_id"] = m.ToolCallId;
            msgs.Add(node);
        }
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = msgs,
            ["stream"] = true,
            ["temperature"] = o.Temperature,
            ["max_tokens"] = o.MaxTokens,
            // Qwen 系列：思考开关走对话模板参数（vLLM 认；别家兼容接口一般忽略未知字段）
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = o.Think }
        };
        if (qwen)
        {
            // 思考强度：Qwen3.8 的对话模板认 reasoning_effort（low / medium / xhigh），vLLM 官方说明是放在 chat_template_kwargs 里
            if (o.Think && !string.IsNullOrEmpty(o.Effort)) body["chat_template_kwargs"]!["reasoning_effort"] = o.Effort;
            if (o.TopP is { } p) body["top_p"] = p;
            if (o.TopK is { } k) body["top_k"] = k;
            if (o.PresencePenalty is { } pp) body["presence_penalty"] = pp;
        }
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonSerializer.SerializeToNode(t.Parameters)
                }
            }).ToArray());
            body["tool_choice"] = o.AllowTools ? "auto" : "none";
        }
        return body;
    }

    private static bool LooksLikeToolsUnsupported(string err)
        => err.Contains("tool", StringComparison.OrdinalIgnoreCase)
           && (err.Contains("auto", StringComparison.OrdinalIgnoreCase) || err.Contains("parser", StringComparison.OrdinalIgnoreCase)
               || err.Contains("not supported", StringComparison.OrdinalIgnoreCase));

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>把流式分片拼回完整的一轮：正文、工具调用（名字和参数是分片到的，按 index 归位）。</summary>
    internal sealed class StreamAccumulator
    {
        private readonly StringBuilder _content = new();
        private readonly SortedDictionary<int, (string? Id, StringBuilder Name, StringBuilder Args)> _calls = new();
        private string? _finish;
        private readonly StringBuilder _reasoning = new();
        private double _thinkEnd;

        /// <summary>吃一条 data: 负载，返回这一条里的正文增量（没有为 null）。at：从发出请求到现在的秒数，用来算想了多久。</summary>
        public string? Feed(string json, double at = 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    _finish = fr.GetString();
                if (!choice.TryGetProperty("delta", out var delta)) return null;
                // 推理解析器把思考过程单独放在 reasoning_content（新版 vLLM 叫 reasoning）
                foreach (var key in new[] { "reasoning_content", "reasoning" })
                    if (delta.TryGetProperty(key, out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } rs)
                    {
                        if (_reasoning.Length < 20000) _reasoning.Append(rs);
                        _thinkEnd = at;
                        break;
                    }
                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var index = tc.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var i) ? i : _calls.Count;
                        if (!_calls.TryGetValue(index, out var slot)) slot = (null, new StringBuilder(), new StringBuilder());
                        if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) slot.Id = id.GetString();
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) slot.Name.Append(n.GetString());
                            if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) slot.Args.Append(a.GetString());
                        }
                        _calls[index] = slot;
                    }
                }
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var t = c.GetString();
                    if (!string.IsNullOrEmpty(t)) { _content.Append(t); return t; }
                }
                return null;
            }
            catch (JsonException) { return null; }
        }

        public ToolRoundResult Result() => new(
            _content.ToString(),
            _calls.Values.Where(v => v.Name.Length > 0)
                .Select((v, i) => new ToolCall(v.Id ?? $"call_{i}", v.Name.ToString(), v.Args.Length == 0 ? "{}" : v.Args.ToString()))
                .ToList(),
            _finish, _reasoning.Length > 0 ? _reasoning.ToString() : null, _thinkEnd);
    }
}
