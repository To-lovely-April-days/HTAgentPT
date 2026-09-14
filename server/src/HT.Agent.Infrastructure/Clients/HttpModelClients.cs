using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>对话模型（表 8-2：OpenAI 兼容，地址与模型名可配置，支持流式）。每次调用读运行时配置——改后即时生效。</summary>
public class OpenAiChatClient(IHttpClientFactory httpFactory, IRuntimeConfig config) : IChatModelClient
{
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (url, body) = await BuildAsync(messages, stream: true, ct);
        var http = httpFactory.CreateClient("model");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") yield break;
            var delta = ExtractDelta(payload);
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
    {
        var (url, body) = await BuildAsync(messages, stream: false, ct);
        var http = httpFactory.CreateClient("model");
        var resp = await http.PostAsJsonAsync(url, body, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private async Task<(string Url, object Body)> BuildAsync(IReadOnlyList<ChatTurn> messages, bool stream, CancellationToken ct)
    {
        var baseUrl = (await config.GetStringAsync(ConfigKeys.ChatModelUrl, "http://127.0.0.1:8000/v1", ct)).TrimEnd('/');
        var body = new
        {
            model = await config.GetStringAsync(ConfigKeys.ChatModelName, "chat-default", ct),
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            temperature = await config.GetDoubleAsync(ConfigKeys.ChatTemperature, 0.2, ct),
            max_tokens = await config.GetIntAsync(ConfigKeys.ChatMaxTokens, 2048, ct),
            stream
        };
        return ($"{baseUrl}/chat/completions", body);
    }

    private static string? ExtractDelta(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return null;
            return choices[0].TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var c)
                ? c.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>向量化（表 8-2：批量，批大小可配置）。OpenAI 兼容 /embeddings 形态。</summary>
public class HttpEmbeddingClient(IHttpClientFactory httpFactory, IRuntimeConfig config) : IEmbeddingClient
{
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var url = (await config.GetStringAsync(ConfigKeys.EmbeddingUrl, "http://127.0.0.1:8080/embed", ct)).TrimEnd('/');
        var model = await config.GetStringAsync(ConfigKeys.EmbeddingModelName, "embedding-default", ct);
        var batchSize = await config.GetIntAsync(ConfigKeys.EmbeddingBatchSize, 32, ct);
        var http = httpFactory.CreateClient("model");
        var all = new List<float[]>(texts.Count);
        for (var i = 0; i < texts.Count; i += batchSize)
        {
            var batch = texts.Skip(i).Take(batchSize).ToList();
            var resp = await http.PostAsJsonAsync(url, new { model, input = batch }, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
                all.Add(item.GetProperty("embedding").EnumerateArray().Select(e => e.GetSingle()).ToArray());
        }
        return all;
    }

    public async Task<string> CurrentModelTagAsync(CancellationToken ct = default)
    {
        var model = await config.GetStringAsync(ConfigKeys.EmbeddingModelName, "embedding-default", ct);
        var dim = await config.GetIntAsync(ConfigKeys.EmbeddingDimension, 1024, ct);
        return $"{model}@{dim}";
    }
}

/// <summary>重排序（表 8-2：输入查询与候选，返回分值）。</summary>
public class HttpRerankClient(IHttpClientFactory httpFactory, IRuntimeConfig config) : IRerankClient
{
    public async Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken ct = default)
    {
        var url = (await config.GetStringAsync(ConfigKeys.RerankUrl, "http://127.0.0.1:8081/rerank", ct)).TrimEnd('/');
        var model = await config.GetStringAsync(ConfigKeys.RerankModelName, "rerank-default", ct);
        var http = httpFactory.CreateClient("model");
        var resp = await http.PostAsJsonAsync(url, new { model, query, documents = passages }, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var scores = new double[passages.Count];
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
            scores[r.GetProperty("index").GetInt32()] = r.GetProperty("relevance_score").GetDouble();
        return scores;
    }
}

/// <summary>解析引擎（表 8-2）。连接失败/超时按不可用处理，任务置等待（10.3）；
/// 引擎返回的内容性错误按具体原因失败（FR-1.7）。</summary>
public class HttpParserClient(IHttpClientFactory httpFactory, IRuntimeConfig config) : IDocumentParserClient
{
    public async Task<ParsedDocument> ParseAsync(Stream file, string fileName, string contentType, CancellationToken ct = default)
    {
        var url = await config.GetStringAsync(ConfigKeys.ParserUrl, "http://127.0.0.1:8082/parse", ct);
        var timeout = await config.GetIntAsync(ConfigKeys.ParserTimeoutSeconds, 300, ct);
        var http = httpFactory.CreateClient("parser");
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync(url, form, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ParserUnavailableException($"解析引擎超时（{timeout} 秒未响应）");
        }
        catch (HttpRequestException ex)
        {
            throw new ParserUnavailableException($"解析引擎连接失败：{ex.Message}", ex);
        }
        if ((int)resp.StatusCode >= 500)
            throw new ParserUnavailableException($"解析引擎异常（HTTP {(int)resp.StatusCode}）");
        if (!resp.IsSuccessStatusCode)
        {
            var reason = await resp.Content.ReadAsStringAsync(ct);
            throw new ParseContentException($"引擎拒绝该文件（HTTP {(int)resp.StatusCode}）：{Truncate(reason)}");
        }
        var parsed = await resp.Content.ReadFromJsonAsync<ParserResponse>(cancellationToken: ct)
            ?? throw new ParseContentException("引擎返回空结果");
        var blocks = parsed.Blocks.Select(x => new ParsedBlock(
            x.Kind, x.Text, x.Level, x.PageNo, x.Bbox, x.TableHeader)).ToList();
        if (blocks.Count == 0) throw new ParseContentException("引擎未解析出任何内容块（文件可能损坏或为空）");
        return new ParsedDocument(blocks);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];

    private record ParserResponse([property: JsonPropertyName("blocks")] List<ParserBlock> Blocks);
    private record ParserBlock(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("level")] int? Level,
        [property: JsonPropertyName("page_no")] int? PageNo,
        [property: JsonPropertyName("bbox")] string? Bbox,
        [property: JsonPropertyName("table_header")] string? TableHeader);
}
