using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.Extensions.Configuration;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>流程模拟服务（DWSIM 官方的无界面版 dwsim-mcp）的客户端：POST /mcp 发 JSON-RPC 的 tools/call，口令放 X-MCP-Token。
/// 口令从环境变量 DWSIM_TOKEN 读（docker compose 里给 dwsim 和后端同一个值）。</summary>
public class DwsimClient(IHttpClientFactory http, IRuntimeConfig config, IConfiguration appConfig)
{
    /// <summary>Unavailable：服务连不上（容器没起来、口令不对）；否则是这一步本身的错，原话可以交给模型去改。</summary>
    public class DwsimException(string message, bool unavailable = false) : Exception(message)
    {
        public bool Unavailable { get; } = unavailable;
    }

    private int _id;

    private async Task<string> BaseAsync(CancellationToken ct) => (await config.GetStringAsync(ConfigKeys.DwsimUrl, "http://dwsim:5901", ct)).TrimEnd('/');
    private string Token => appConfig["DWSIM_TOKEN"] ?? "";

    public async Task<JsonNode?> CallAsync(string tool, JsonObject args, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref _id), ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args.DeepClone() },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, await BaseAsync(ct) + "/mcp")
        {
            // 带 Content-Length 发（不用分块），对方是 HttpListener
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        if (Token.Length > 0) req.Headers.Add("X-MCP-Token", Token);
        HttpResponseMessage resp;
        try { resp = await http.CreateClient("dwsim").SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DwsimException("流程模拟服务连不上（dwsim 容器没起来？）", unavailable: true);
        }
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new DwsimException("流程模拟服务的口令不对（DWSIM_TOKEN 两边要一致）", unavailable: true);
            if (!resp.IsSuccessStatusCode) throw new DwsimException($"流程模拟服务出错（HTTP {(int)resp.StatusCode}）", unavailable: true);
            var text = await resp.Content.ReadAsStringAsync(ct);
            return Unwrap(text);
        }
    }

    /// <summary>JSON-RPC 回复 → 工具结果（content[0].text 里的 JSON）。工具报错、参数错都抛 DwsimException，带对方的原话。</summary>
    internal static JsonNode? Unwrap(string text)
    {
        JsonNode? r;
        try { r = JsonNode.Parse(text); }
        catch (JsonException) { throw new DwsimException("流程模拟服务回的不是 JSON", unavailable: true); }
        if (r?["error"] is JsonObject err) throw new DwsimException(err["message"]?.ToString() ?? "调用出错");
        var res = r?["result"];
        var inner = res?["content"]?[0]?["text"]?.ToString() ?? "{}";
        JsonNode? val;
        try { val = JsonNode.Parse(inner); } catch (JsonException) { val = JsonValue.Create(inner); }
        if (res?["isError"]?.GetValue<bool>() == true)
            throw new DwsimException(val?["error"]?.ToString() ?? inner);
        return val;
    }

    public async Task<bool> HealthyAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, await BaseAsync(ct) + "/health");
            if (Token.Length > 0) req.Headers.Add("X-MCP-Token", Token);
            using var resp = await http.CreateClient("tools").SendAsync(req, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }
}
