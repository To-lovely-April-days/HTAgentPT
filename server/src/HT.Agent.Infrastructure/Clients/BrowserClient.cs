using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>内置浏览器容器（deploy/browser）的客户端。每个对话一个浏览器环境，用对话 id 当钥匙。</summary>
public class BrowserClient(IHttpClientFactory http, IRuntimeConfig config)
{
    /// <summary>浏览器那边回了错（网页打不开、找不到编号、超过上限……），Message 是给人看的一句话。</summary>
    public class BrowserException(string message, HttpStatusCode status) : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;
    }

    public record TabInfo(string Tab, string Url, string Title, int Rev);

    private async Task<string> BaseAsync(CancellationToken ct)
        => (await config.GetStringAsync(ConfigKeys.BrowserUrl, "http://browser:8100", ct)).TrimEnd('/');

    public async Task<IReadOnlyList<string>> AllowHostsAsync(CancellationToken ct)
        => (await config.GetStringAsync(ConfigKeys.BrowserAllowHosts, "", ct)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<IReadOnlyList<string>> BlockHostsAsync(CancellationToken ct)
    {
        var extra = (await config.GetStringAsync(ConfigKeys.BrowserBlockHosts, "", ct)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. BrowserRules.DefaultBlocked, .. extra];
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, await BaseAsync(ct) + path);
        if (body is not null) req.Content = JsonContent.Create(body);
        HttpResponseMessage resp;
        try { resp = await http.CreateClient("sandbox").SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new BrowserException("内置浏览器连不上（browser 容器没起来？）", HttpStatusCode.ServiceUnavailable);
        }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            if (!resp.IsSuccessStatusCode)
                throw new BrowserException(doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "浏览器出错了" : "浏览器出错了", resp.StatusCode);
            return doc.RootElement.Clone();
        }
    }

    private static TabInfo Info(JsonElement r) => new(
        r.GetProperty("tab").GetString() ?? "", r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
        r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "", r.TryGetProperty("rev", out var v) ? v.GetInt32() : 0);

    public async Task<TabInfo> OpenAsync(string sid, string url, string? tab, CancellationToken ct)
        => Info(await SendAsync(HttpMethod.Post, $"/s/{sid}/open", new { url, tab, allow = await AllowHostsAsync(ct) }, ct));

    public async Task<(bool Alive, IReadOnlyList<TabInfo> Tabs)> TabsAsync(string sid, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, $"/s/{sid}/tabs", null, ct);
        var tabs = r.TryGetProperty("tabs", out var ts) ? ts.EnumerateArray().Select(Info).ToList() : [];
        return (r.TryGetProperty("alive", out var a) && a.GetBoolean(), tabs);
    }

    public async Task<PageSnapshot> ReadAsync(string sid, string tab, CancellationToken ct)
        => BrowserRules.Parse(await SendAsync(HttpMethod.Post, $"/s/{sid}/t/{tab}/read", new { }, ct));

    public async Task<TabInfo> ActAsync(string sid, string tab, string action, int? n, string? text, string? value, bool submit, CancellationToken ct)
        => Info(await SendAsync(HttpMethod.Post, $"/s/{sid}/t/{tab}/act", new { action, n, text, value, submit }, ct));

    public async Task<TabInfo> InputAsync(string sid, string tab, object input, CancellationToken ct)
        => Info(await SendAsync(HttpMethod.Post, $"/s/{sid}/t/{tab}/input", input, ct));

    public async Task CloseTabAsync(string sid, string tab, CancellationToken ct)
        => await SendAsync(HttpMethod.Delete, $"/s/{sid}/t/{tab}", null, ct);

    public async Task<(byte[] Image, int Rev)?> ShotAsync(string sid, string tab, CancellationToken ct)
    {
        try
        {
            using var resp = await http.CreateClient("sandbox").GetAsync(await BaseAsync(ct) + $"/s/{sid}/t/{tab}/shot", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var rev = resp.Headers.TryGetValues("X-Rev", out var v) && int.TryParse(v.FirstOrDefault(), out var n) ? n : 0;
            return (await resp.Content.ReadAsByteArrayAsync(ct), rev);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    public async Task<bool> HealthyAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp = await http.CreateClient("tools").GetAsync(await BaseAsync(ct) + "/health", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }
}

/// <summary>谁在操作哪个网页：主助手（0）或第几个子代理，正在做什么。界面上标签的小人、蓝点、提示条都看这里。
/// 「停下」也记在这里：助手下一步操作网页前先看一眼。</summary>
public class BrowserActivity
{
    public record Mark(int Who, bool Live, bool Done, string Label, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Mark>> _tabs = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _stopped = new();

    public void Begin(string sid, string tab, int who, string label)
        => _tabs.GetOrAdd(sid, _ => new())[tab] = new Mark(who, true, false, label, DateTimeOffset.UtcNow);

    public void End(string sid, string tab, bool done = true)
    {
        if (_tabs.TryGetValue(sid, out var m) && m.TryGetValue(tab, out var cur)) m[tab] = cur with { Live = false, Done = done, At = DateTimeOffset.UtcNow };
    }

    /// <summary>这一轮回答结束：还亮着的都熄掉。</summary>
    public void EndAll(string sid)
    {
        if (!_tabs.TryGetValue(sid, out var m)) return;
        foreach (var kv in m) if (kv.Value.Live) m[kv.Key] = kv.Value with { Live = false, Done = true };
    }

    public IReadOnlyDictionary<string, Mark> Of(string sid)
        => _tabs.TryGetValue(sid, out var m) ? new Dictionary<string, Mark>(m) : new Dictionary<string, Mark>();

    public bool LiveOn(string sid, string tab) => _tabs.TryGetValue(sid, out var m) && m.TryGetValue(tab, out var x) && x.Live;

    public void Stop(string sid) => _stopped[sid] = DateTimeOffset.UtcNow;
    public void Resume(string sid) => _stopped.TryRemove(sid, out _);
    public bool Stopped(string sid) => _stopped.ContainsKey(sid);
}
