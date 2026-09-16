using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>MinerU 官方在线解析（mineru.net API v4）。流程是任务制而非同步应答：
/// ① POST /api/v4/file-urls/batch 申请预签名上传地址（同时带解析选项，上传完成即自动开始解析）
/// ② PUT 文件到预签名地址 ③ 轮询 /api/v4/extract-results/batch/{id} 直到 done/failed
/// ④ 下载结果 zip，取其中的 content_list.json，映射复用本地客户端的同一份逻辑。
/// 网络类失败按引擎不可用抛出（任务置等待，10.3）；令牌无效、服务拒绝、解析失败按内容性
/// 错误抛出并带原因（FR-1.7）——这类错误重试也不会好，静默重试只会耗额度。
/// 纯文本（.txt/.md）不上传外部服务，本地直接解析。</summary>
public class MinerUOnlineParserClient(IHttpClientFactory httpFactory, IRuntimeConfig config) : IDocumentParserClient
{
    private static readonly StubParserClient TextFallback = new();

    public async Task<ParsedDocument> ParseAsync(Stream file, string fileName, string contentType, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".txt" or ".md")
            return await TextFallback.ParseAsync(file, fileName, contentType, ct);

        var token = (await config.GetStringAsync(ConfigKeys.ParserApiKey, "", ct)).Trim();
        if (token.Length == 0)
            throw new ParseContentException("未配置在线解析令牌：在「系统设置 → 文档解析」选择在线服务并填入 mineru.net 的令牌");
        var baseUrl = (await config.GetStringAsync(ConfigKeys.ParserOnlineBase, "https://mineru.net", ct)).TrimEnd('/');
        var backend = await config.GetStringAsync(ConfigKeys.ParserBackend, "pipeline", ct);
        var timeout = await config.GetIntAsync(ConfigKeys.ParserTimeoutSeconds, 300, ct);
        var http = httpFactory.CreateClient("parser");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var safeName = MinerUParserClient.SafeAsciiFileName(fileName);

        // ① 申请上传地址。请求体按官方现行示例保持最小面：files[{name,data_id}] + model_version——
        //    is_ocr / enable_formula / enable_table / language 属随版本变动的可选项，带上曾被线上
        //    校验以 -10002 拒绝；不发即用服务端默认值。model_version 取解析后端配置（pipeline/vlm/
        //    MinerU-HTML…），在「系统设置 → 切分与解析 → 解析后端」可改，不用动代码。
        var dataId = Guid.NewGuid().ToString("N");
        var createBody = new
        {
            files = new[] { new { name = safeName, data_id = dataId } },
            model_version = backend.StartsWith("vlm", StringComparison.OrdinalIgnoreCase) ? "vlm" : backend.Trim()
        };
        var createJson = JsonSerializer.Serialize(createBody);
        string batchId, uploadUrl;
        using (var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v4/file-urls/batch")
               { Content = new StringContent(createJson, System.Text.Encoding.UTF8, "application/json") })
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await SendAsync(http, req, timeout, ct, "申请上传地址");
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new ParseContentException($"在线解析服务拒绝（HTTP {(int)resp.StatusCode}）：{Truncate(body)}（已发送：{createJson}）");
            try
            {
                (batchId, uploadUrl) = ParseBatchCreate(body);
            }
            catch (ParseContentException ex)
            {
                // 把我们实际发送的请求体带上——服务端字段校验类错误全靠这个一眼定位
                throw new ParseContentException($"{ex.Reason}（已发送：{createJson}）");
            }
        }

        // ② 上传文件（预签名地址，无需鉴权头；上传完成后服务端自动入队解析）
        buffer.Position = 0;
        using (var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new StreamContent(buffer) })
        {
            var resp = await SendAsync(http, put, timeout, ct, "上传文件");
            if (!resp.IsSuccessStatusCode)
                throw new ParserUnavailableException($"上传到在线解析服务失败（HTTP {(int)resp.StatusCode}）");
        }

        // ③ 轮询直到 done / failed；总时长受解析超时配置约束
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, timeout));
        string zipUrl;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow > deadline)
                throw new ParserUnavailableException($"在线解析超时（{timeout} 秒未完成，任务号 {batchId}）");
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
            using var poll = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v4/extract-results/batch/{batchId}");
            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await SendAsync(http, poll, timeout, ct, "查询解析进度");
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new ParserUnavailableException($"查询在线解析进度失败（HTTP {(int)resp.StatusCode}）：{Truncate(body)}");
            var (state, url, err) = ParseBatchState(body, safeName, dataId);
            if (state == "failed")
                throw new ParseContentException($"在线解析失败：{(string.IsNullOrWhiteSpace(err) ? "服务未给出原因" : err)}");
            if (state == "done" && !string.IsNullOrEmpty(url)) { zipUrl = url!; break; }
        }

        // ④ 下载结果包并抽取 content_list
        byte[] zipBytes;
        using (var dl = new HttpRequestMessage(HttpMethod.Get, zipUrl))
        {
            var resp = await SendAsync(http, dl, timeout, ct, "下载解析结果");
            if (!resp.IsSuccessStatusCode)
                throw new ParserUnavailableException($"下载解析结果失败（HTTP {(int)resp.StatusCode}）");
            zipBytes = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        var blocks = ExtractBlocksFromZip(zipBytes);
        if (blocks.Count == 0)
            throw new ParseContentException("在线解析结果不含内容块（文件可能为空或全为无法识别的图像）");
        return new ParsedDocument(blocks);
    }

    /// <summary>网络层异常统一按引擎不可用抛出（任务置等待稍后重试），带上是哪一步。</summary>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpRequestMessage req, int timeoutSeconds, CancellationToken ct, string step)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ParserUnavailableException($"在线解析服务超时（{step}，{timeoutSeconds} 秒未响应）");
        }
        catch (HttpRequestException ex)
        {
            throw new ParserUnavailableException($"在线解析服务连接失败（{step}）：{ex.Message}", ex);
        }
    }

    /// <summary>批次创建响应：{"code":0,"data":{"batch_id":…,"file_urls":[…]}}；code 非 0 带 msg 报出。</summary>
    public static (string BatchId, string UploadUrl) ParseBatchCreate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
        if (code != 0)
            throw new ParseContentException($"在线解析服务拒绝请求（code {code}）：{GetMsg(root)}");
        var data = root.GetProperty("data");
        var batchId = data.GetProperty("batch_id").GetString();
        var urls = data.GetProperty("file_urls");
        if (string.IsNullOrEmpty(batchId) || urls.ValueKind != JsonValueKind.Array || urls.GetArrayLength() == 0)
            throw new ParseContentException("在线解析服务未返回上传地址（确认令牌有效、额度未耗尽）");
        return (batchId!, urls[0].GetString()!);
    }

    /// <summary>批次状态响应：优先按 data_id 匹配 extract_result 条目（服务端可能改写文件名），
    /// 其次按文件名，都找不到取第一条——我们每次只送一个文件。</summary>
    public static (string State, string? ZipUrl, string? Error) ParseBatchState(string json, string fileName, string? dataId = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("extract_result", out var results)
            || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            return ("pending", null, null);
        JsonElement entry = results[0];
        var found = false;
        if (!string.IsNullOrEmpty(dataId))
            foreach (var r in results.EnumerateArray())
                if (r.TryGetProperty("data_id", out var di) && di.ValueKind == JsonValueKind.String && di.GetString() == dataId)
                { entry = r; found = true; break; }
        if (!found)
            foreach (var r in results.EnumerateArray())
                if (r.TryGetProperty("file_name", out var fn) && fn.ValueKind == JsonValueKind.String && fn.GetString() == fileName)
                { entry = r; break; }
        var state = entry.TryGetProperty("state", out var st) ? st.GetString() ?? "pending" : "pending";
        var zip = entry.TryGetProperty("full_zip_url", out var z) && z.ValueKind == JsonValueKind.String ? z.GetString() : null;
        var err = entry.TryGetProperty("err_msg", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        return (state, zip, err);
    }

    /// <summary>结果 zip 里找 content_list.json（文件名可能带前缀），解析并映射为统一块。</summary>
    public static List<ParsedBlock> ExtractBlocksFromZip(byte[] zipBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith("content_list.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new ParseContentException("在线解析结果包中没有 content_list.json（服务版本可能变更）");
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        using var doc = JsonDocument.Parse(ms.ToArray());
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ParseContentException("在线解析结果的 content_list 不是数组");
        return MinerUParserClient.MapContentList(doc.RootElement);
    }

    private static string GetMsg(JsonElement root)
        => root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "无说明";

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
