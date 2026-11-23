using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace HT.Agent.Infrastructure.Services;

/// <summary>对话模型服务的上下文长度（vLLM 在 /models 里报 max_model_len）。
/// 后台没填（0 = 自动）时就用它——免得模型开了 32K、系统还按出厂的 8K 算，每轮只给几百 token，
/// 工具参数写一半就断、回答写到一半就停。查到的缓存 5 分钟，查不到的缓存 1 分钟。</summary>
public class ModelContextProbe(IHttpClientFactory http, IRuntimeConfig config, IMemoryCache cache)
{
    public async Task<int?> DetectAsync(CancellationToken ct)
    {
        if ((await config.GetStringAsync(ConfigKeys.ChatProvider, "openai", ct)).Trim().Equals("stub", StringComparison.OrdinalIgnoreCase)) return null;
        var url = (await config.GetStringAsync(ConfigKeys.ChatModelUrl, "http://127.0.0.1:8000/v1", ct)).TrimEnd('/');
        var key = $"model-ctx:{url}";
        if (cache.TryGetValue(key, out int? hit)) return hit;
        int? found = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/models");
            var apiKey = (await config.GetStringAsync(ConfigKeys.ChatApiKey, "", ct)).Trim();
            if (apiKey.Length > 0) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await http.CreateClient("model").SendAsync(req, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
                found = MaxModelLen(doc.RootElement);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException) { }
        if (ct.IsCancellationRequested) return found;
        cache.Set(key, found, found is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5));
        return found;
    }

    /// <summary>/models 返回里第一个带 max_model_len 的模型。</summary>
    public static int? MaxModelLen(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var m in data.EnumerateArray())
            if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("max_model_len", out var len) && len.TryGetInt32(out var n) && n > 0) return n;
        return null;
    }
}
