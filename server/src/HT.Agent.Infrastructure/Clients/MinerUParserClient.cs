using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>MinerU 解析服务客户端。支持 MinerU 4 的 V1 上传/任务 API，同时保留
/// MinerU 3.x 的同步 /file_parse 兼容路径。
/// 请求带 return_content_list=true，把 content_list（阅读序的类型化块）映射为统一解析块：
/// text 且 text_level≥1 → heading；table_body（HTML）逐行拆为 table_row 并携带表头行；
/// list 合并为一段；公式/代码保留文本；图片仅保留题注文字。page_idx 从 0 起，映射为 1 起页码。
/// 纯文本文件（.txt/.md）不需要版面分析引擎，直接走本地文本解析——也避免把它们递给
/// PDF 引擎换来一句格式错误。超时/重试语义与通用客户端一致（表 8-2），重试需重传文件。</summary>
public class MinerUParserClient(IHttpClientFactory httpFactory, IRuntimeConfig config) : IDocumentParserClient
{
    private static readonly StubParserClient TextFallback = new();

    public async Task<ParsedDocument> ParseAsync(Stream file, string fileName, string contentType, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".txt" or ".md")
            return await TextFallback.ParseAsync(file, fileName, contentType, ct);

        var url = await config.GetStringAsync(ConfigKeys.ParserUrl, "http://127.0.0.1:8000", ct);
        var backend = await config.GetStringAsync(ConfigKeys.ParserBackend, "pipeline", ct);
        var timeout = await config.GetIntAsync(ConfigKeys.ParserTimeoutSeconds, 300, ct);
        var maxRetries = Math.Max(0, await config.GetIntAsync(ConfigKeys.ParserMaxRetries, 3, ct));
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        // MinerU 4 的服务根地址是 http://host:8000（接口在 /v1/*）。已有数据库
        // 可能仍保存旧的 /file_parse 地址，因此先兼容旧服务；收到 404 时自动切换
        // 到同一主机的 V1 API，不要求管理员手动修改配置。
        if (IsLegacyUrl(url))
        {
            try
            {
                return await ParseLegacyAsync(bytes, fileName, contentType, url, backend, timeout, maxRetries, ct);
            }
            catch (LegacyEndpointNotFoundException)
            {
                url = NormalizeBaseUrl(url);
            }
        }

        return await ParseV1Async(bytes, fileName, contentType, NormalizeBaseUrl(url), backend, timeout, ct);
    }

    private async Task<ParsedDocument> ParseLegacyAsync(
        byte[] bytes, string fileName, string contentType, string url, string backend,
        int timeout, int maxRetries, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("parser");

        HttpResponseMessage resp = null!;
        for (var attempt = 0; ; attempt++)
        {
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            // 字段名显式带引号：.NET 默认发 name=files（不带引号），RFC 7578 要求 quoted-string，
            // 部分 Python 端 multipart 解析器只认带引号的写法。文件名压成 ASCII——
            // 引擎只拿它取扩展名和当结果键，中文名不值得赌各版本的 filename* 兼容性。
            var safeName = SafeAsciiFileName(fileName);
            form.Add(fileContent, "\"files\"", $"\"{safeName}\"");
            form.Add(new StringContent(backend), "\"backend\"");
            // OCR 语言：ch 覆盖中英混排；文字版文档不走 OCR，此参数只影响扫描件
            form.Add(new StringContent("ch"), "\"lang_list\"");
            form.Add(new StringContent("true"), "\"return_content_list\"");
            // 图片本体一并要回来（base64）：来源标注要能把图直接放出来（FR-4.9）
            form.Add(new StringContent("true"), "\"return_images\"");
            form.Add(new StringContent("false"), "\"return_md\"");
            form.Add(new StringContent("false"), "\"response_format_zip\"");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));
            try
            {
                resp = await http.PostAsync(url, form, cts.Token);
                if ((int)resp.StatusCode < 500) break;
                if (attempt >= maxRetries)
                    throw new ParserUnavailableException($"解析引擎异常（HTTP {(int)resp.StatusCode}，已重试 {attempt} 次）");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= maxRetries)
                    throw new ParserUnavailableException($"解析引擎超时（{timeout} 秒未响应，已重试 {attempt} 次）");
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= maxRetries)
                    throw new ParserUnavailableException($"解析引擎连接失败：{ex.Message}（已重试 {attempt} 次）", ex);
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5 * (attempt + 1), 30)), ct);
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            resp.Dispose();
            throw new LegacyEndpointNotFoundException();
        }
        if (!resp.IsSuccessStatusCode)
        {
            var reason = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            throw new ParseContentException($"引擎拒绝该文件（HTTP {(int)resp.StatusCode}）：{Truncate(reason)}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        resp.Dispose();
        var (blocks, images) = MapResponseFull(doc.RootElement);
        if (blocks.Count == 0)
            throw new ParseContentException("引擎未解析出任何内容块（文件可能损坏、为空或全为无法识别的图像）");
        return new ParsedDocument(blocks, images);
    }

    /// <summary>MinerU 4 V1：上传源文件，创建解析任务，轮询任务，再下载 zip 产物。</summary>
    private async Task<ParsedDocument> ParseV1Async(
        byte[] bytes, string fileName, string contentType, string baseUrl, string backend,
        int timeout, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("parser");
        var apiKey = (await config.GetStringAsync(ConfigKeys.ParserApiKey, "", ct)).Trim();
        var safeName = SafeAsciiFileName(fileName);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var uploadJson = await SendJsonAsync(http, HttpMethod.Post, $"{baseUrl}/v1/uploads", new
        {
            filename = safeName,
            bytes = bytes.LongLength,
            mime_type = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            purpose = "parse",
            sha256sum = sha256,
        }, apiKey, timeout, ct, "创建 MinerU 上传");

        using var upload = ParseJson(uploadJson, "MinerU 上传响应");
        var uploadRoot = upload.RootElement;
        var uploadStatus = GetString(uploadRoot, "status") ?? "";
        string fileId;
        if (uploadStatus.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            fileId = GetNestedString(uploadRoot, "file", "id")
                ?? throw new ParseContentException("MinerU 上传已完成，但响应缺少 file.id");
        }
        else
        {
            var uploadId = GetRequiredString(uploadRoot, "id", "MinerU 上传响应缺少 id");
            var uploadUrl = GetRequiredString(uploadRoot, "upload_url", "MinerU 上传响应缺少 upload_url");
            var method = GetString(uploadRoot, "upload_method") ?? "PUT";
            var headers = ReadHeaders(uploadRoot, "upload_headers");
            var resolvedUploadUrl = new Uri(new Uri(baseUrl + "/"), uploadUrl).ToString();
            if (SameOrigin(new Uri(baseUrl + "/"), new Uri(resolvedUploadUrl)))
                AddBearer(headers, apiKey);

            await SendBytesAsync(http, new HttpMethod(method), resolvedUploadUrl, bytes, contentType, headers,
                timeout, ct, "上传 MinerU 文件");
            var completeJson = await SendJsonAsync(http, HttpMethod.Post,
                $"{baseUrl}/v1/uploads/{Uri.EscapeDataString(uploadId)}/complete", null,
                apiKey, timeout, ct, "确认 MinerU 上传");
            using var complete = ParseJson(completeJson, "MinerU 上传确认响应");
            fileId = GetNestedString(complete.RootElement, "file", "id")
                ?? (GetString(complete.RootElement, "status")?.Equals("completed", StringComparison.OrdinalIgnoreCase) == true
                    ? GetString(complete.RootElement, "file_id") : null)
                ?? throw new ParseContentException("MinerU 上传确认未返回 file.id");
        }

        var tier = MapTier(backend);
        var jobJson = await SendJsonAsync(http, HttpMethod.Post, $"{baseUrl}/v1/parse/jobs", new
        {
            files = new[] { new { source = new { type = "file_id", file_id = fileId } } },
            tier,
            ocr_mode = "auto",
            output_formats = new[] { "zip" },
        }, apiKey, timeout, ct, "创建 MinerU 解析任务");
        using var job = ParseJson(jobJson, "MinerU 任务响应");
        var jobId = GetRequiredString(job.RootElement, "job_id", "MinerU 任务响应缺少 job_id");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, timeout));
        string? outputFileId = null;
        string? terminalError = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow > deadline)
                throw new ParserUnavailableException($"MinerU 解析超时（{timeout} 秒未完成，任务号 {jobId}）");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var stateJson = await SendJsonAsync(http, HttpMethod.Get,
                $"{baseUrl}/v1/parse/jobs/{Uri.EscapeDataString(jobId)}", null,
                apiKey, timeout, ct, "查询 MinerU 解析进度");
            using var state = ParseJson(stateJson, "MinerU 任务状态响应");
            var status = (GetString(state.RootElement, "status") ?? "").ToLowerInvariant();
            if (status is "queued" or "running" or "pending") continue;
            if (status is "failed" or "canceled")
            {
                terminalError = GetString(state.RootElement, "error")
                    ?? GetString(state.RootElement, "message")
                    ?? GetString(state.RootElement, "detail");
                throw new ParseContentException($"MinerU 解析{(status == "canceled" ? "已取消" : "失败")}：{Truncate(terminalError ?? "服务未给出原因")}");
            }
            if (status is "completed" or "partial")
            {
                outputFileId = FindOutputFileId(state.RootElement, "zip");
                if (string.IsNullOrWhiteSpace(outputFileId))
                    throw new ParseContentException($"MinerU 任务已{(status == "partial" ? "部分" : "完成")}，但没有返回 zip 产物");
                break;
            }
            throw new ParseContentException($"MinerU 返回未知任务状态：{status}");
        }

        var zipBytes = await SendBytesDownloadAsync(http, $"{baseUrl}/v1/files/{Uri.EscapeDataString(outputFileId!)}"
            + "/content", apiKey, timeout, ct, "下载 MinerU 解析结果");
        var (blocks, images) = ExtractV4Zip(zipBytes);
        if (blocks.Count == 0)
            throw new ParseContentException("MinerU 解析结果不含内容块（文件可能为空或全为无法识别的图像）");
        return new ParsedDocument(blocks, images);
    }

    private sealed class LegacyEndpointNotFoundException : Exception;

    private static bool IsLegacyUrl(string url)
        => new Uri(url, UriKind.Absolute).AbsolutePath.TrimEnd('/').EndsWith("/file_parse", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeBaseUrl(string url)
    {
        var uri = new Uri(url.TrimEnd('/'), UriKind.Absolute);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/file_parse", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/file_parse".Length];
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/v1".Length];
        return new UriBuilder(uri) { Path = path.TrimEnd('/') }.Uri.ToString().TrimEnd('/');
    }

    private static string MapTier(string backend)
        => backend.Trim().ToLowerInvariant() switch
        {
            "flash" or "basic" or "standard" or "advanced" => backend.Trim().ToLowerInvariant(),
            _ => "standard",
        };

    private static JsonDocument ParseJson(string json, string context)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ParseContentException($"{context}不是合法 JSON：{ex.Message}"); }
    }

    private static string GetRequiredString(JsonElement root, string name, string message)
        => GetString(root, name) is { Length: > 0 } value ? value : throw new ParseContentException(message);

    private static string? GetNestedString(JsonElement root, string parent, string child)
        => root.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object
            ? GetString(p, child) : null;

    private static Dictionary<string, string> ReadHeaders(JsonElement root, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty(name, out var headers) || headers.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in headers.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String) result[p.Name] = p.Value.GetString()!;
        return result;
    }

    private static void AddBearer(Dictionary<string, string> headers, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey) && !headers.ContainsKey("Authorization"))
            headers["Authorization"] = $"Bearer {apiKey}";
    }

    private static bool SameOrigin(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
           && left.Port == right.Port;

    private static string? FindOutputFileId(JsonElement root, string format)
    {
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;
        foreach (var file in files.EnumerateArray())
        {
            if (!file.TryGetProperty("output_files", out var outputs)) continue;
            if (outputs.ValueKind == JsonValueKind.Object && outputs.TryGetProperty(format, out var outputObject))
            {
                var id = GetString(outputObject, "file_id") ?? GetString(outputObject, "id");
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
            if (outputs.ValueKind == JsonValueKind.Array)
                foreach (var outputItem in outputs.EnumerateArray())
                    if (string.Equals(GetString(outputItem, "format"), format, StringComparison.OrdinalIgnoreCase))
                        return GetString(outputItem, "file_id") ?? GetString(outputItem, "id");
        }
        return null;
    }

    private async Task<string> SendJsonAsync(HttpClient http, HttpMethod method, string url, object? body,
        string apiKey, int timeout, CancellationToken ct, string step)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        AddAuthorization(req, apiKey);
        return await SendTextAsync(http, req, timeout, ct, step);
    }

    private async Task SendBytesAsync(HttpClient http, HttpMethod method, string url, byte[] bytes,
        string contentType, IReadOnlyDictionary<string, string> headers, int timeout, CancellationToken ct, string step)
    {
        using var req = new HttpRequestMessage(method, url) { Content = new ByteArrayContent(bytes) };
        if (MediaTypeHeaderValue.TryParse(contentType, out var media)) req.Content.Headers.ContentType = media;
        foreach (var (name, value) in headers)
        {
            if (!req.Headers.TryAddWithoutValidation(name, value))
                req.Content.Headers.TryAddWithoutValidation(name, value);
        }
        _ = await SendTextAsync(http, req, timeout, ct, step);
    }

    private async Task<byte[]> SendBytesDownloadAsync(HttpClient http, string url, string apiKey,
        int timeout, CancellationToken ct, string step)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorization(req, apiKey);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeout)));
        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var reason = await resp.Content.ReadAsStringAsync(ct);
                ThrowHttpFailure(resp.StatusCode, reason, step);
            }
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ParserUnavailableException($"解析引擎超时（{step}，{timeout} 秒未响应）");
        }
        catch (HttpRequestException ex)
        {
            throw new ParserUnavailableException($"解析引擎连接失败（{step}）：{ex.Message}", ex);
        }
    }

    private static async Task<string> SendTextAsync(HttpClient http, HttpRequestMessage req,
        int timeout, CancellationToken ct, string step)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeout)));
        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) ThrowHttpFailure(resp.StatusCode, body, step);
            return body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ParserUnavailableException($"解析引擎超时（{step}，{timeout} 秒未响应）");
        }
        catch (HttpRequestException ex)
        {
            throw new ParserUnavailableException($"解析引擎连接失败（{step}）：{ex.Message}", ex);
        }
    }

    private static void AddAuthorization(HttpRequestMessage req, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static void ThrowHttpFailure(System.Net.HttpStatusCode status, string body, string step)
    {
        if ((int)status >= 500)
            throw new ParserUnavailableException($"解析引擎异常（{step}，HTTP {(int)status}）：{Truncate(body)}");
        throw new ParseContentException($"解析引擎拒绝请求（{step}，HTTP {(int)status}）：{Truncate(body)}");
    }

    /// <summary>MinerU 4 structured_content/middle_json 的 pages[].blocks[] 映射。</summary>
    public static List<ParsedBlock> MapStructuredContent(JsonElement root)
    {
        var blocks = new List<ParsedBlock>();
        if (!root.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            return blocks;
        foreach (var pageElement in pages.EnumerateArray())
        {
            var page = PageNumber(pageElement);
            if (!pageElement.TryGetProperty("blocks", out var pageBlocks)
                && !pageElement.TryGetProperty("items", out pageBlocks))
                continue;
            if (pageBlocks.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var block in pageBlocks.EnumerateArray())
                MapStructuredBlock(block, page, blocks);
        }
        return blocks;
    }

    private static void MapStructuredBlock(JsonElement e, int? page, List<ParsedBlock> blocks)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        var type = (GetString(e, "type") ?? "").ToLowerInvariant();
        var bbox = Bbox(e);
        var content = StructuredText(e, "content") ?? GetString(e, "text") ?? "";
        var level = Number(e, "level") ?? Number(e, "text_level") ?? Number(e, "heading_level");
        if (type is "title" or "doc_title" or "heading" or "paragraph_title" or "section_title")
        {
            content = content.Trim();
            if (content.Length > 0) blocks.Add(new ParsedBlock("heading", content, level ?? 1, page, bbox, null));
            return;
        }
        if (type is "text" or "paragraph" or "page_header" or "page_footer" or "page_number" or "index")
        {
            content = content.Trim();
            if (content.Length > 0)
                blocks.Add(new ParsedBlock(level is > 0 ? "heading" : "paragraph", content, level is > 0 ? level : null, page, bbox, null));
            return;
        }
        if (type is "list" or "text_list" or "reference_list")
        {
            content = content.Trim();
            if (content.Length > 0) blocks.Add(new ParsedBlock("paragraph", content.Replace("\r\n", "\n"), null, page, bbox, null));
            return;
        }
        if (type is "table" or "simple_table" or "complex_table")
        {
            var caption = NestedText(e, "caption", "table_caption");
            if (!string.IsNullOrWhiteSpace(caption))
                blocks.Add(new ParsedBlock("paragraph", caption.Trim(), null, page, bbox, null));
            var html = NestedText(e, "html", "table_body") ??
                       (content.Contains("<tr", StringComparison.OrdinalIgnoreCase) ? content : null);
            if (!string.IsNullOrWhiteSpace(html))
            {
                var rows = HtmlTableRows(html);
                var header = rows.Count > 1 ? string.Join(" | ", rows[0]) : null;
                for (var i = rows.Count > 1 ? 1 : 0; i < rows.Count; i++)
                    blocks.Add(new ParsedBlock("table_row", string.Join(" | ", rows[i]), null, page, bbox, header));
            }
            else if (content.Trim().Length > 0)
                blocks.Add(new ParsedBlock("table_row", content.Trim(), null, page, bbox, null));
            return;
        }
        if (type is "image" or "chart")
        {
            var caption = NestedText(e, "caption", "image_caption", "chart_caption") ?? content;
            if (!string.IsNullOrWhiteSpace(caption))
                blocks.Add(new ParsedBlock("paragraph", caption.Trim(), null, page, bbox, null));
            return;
        }
        if (type is "equation_inline" or "equation_interline" or "equation" or "code" or "algorithm")
        {
            content = NestedText(e, "math_content", "code_body", "body") ?? content;
            if (content.Trim().Length > 0)
                blocks.Add(new ParsedBlock("paragraph", content.Trim(), null, page, bbox, null));
            return;
        }
        // 新版协议可能增加块类型；保留它的可读 content，避免升级后整篇文档变空。
        if (content.Trim().Length > 0)
            blocks.Add(new ParsedBlock("paragraph", content.Trim(), null, page, bbox, null));
    }

    private static int? PageNumber(JsonElement page)
    {
        var pageIdx = Number(page, "page_idx");
        if (pageIdx.HasValue) return pageIdx.Value + 1;
        return Number(page, "page_no") ?? Number(page, "page_number");
    }

    private static int? Number(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static string? Bbox(JsonElement e)
        => e.TryGetProperty("bbox", out var bb) && bb.ValueKind == JsonValueKind.Array
            ? string.Join(",", bb.EnumerateArray().Select(v => v.GetRawText())) : null;

    private static string? StructuredText(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var value)) return null;
        return FlattenText(value);
    }

    private static string? NestedText(JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (e.TryGetProperty(name, out var direct))
            {
                var text = FlattenText(direct);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            if (e.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty(name, out var nested))
            {
                var text = FlattenText(nested);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return null;
    }

    private static string? FlattenText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Array)
        {
            var parts = value.EnumerateArray().Select(FlattenText).Where(x => !string.IsNullOrWhiteSpace(x));
            return string.Join("", parts);
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("content", out var content)) return FlattenText(content);
            if (value.TryGetProperty("text", out var text)) return FlattenText(text);
            if (value.TryGetProperty("body", out var body)) return FlattenText(body);
            foreach (var key in new[] { "spans", "paragraph_content", "title_content", "description", "caption", "image_caption", "table_caption", "chart_caption", "footnote", "image_footnote", "table_footnote", "chart_footnote", "latex", "html", "table_body", "math_content", "code_body" })
                if (value.TryGetProperty(key, out var nested)) return FlattenText(nested);
        }
        return null;
    }

    private static string? NestedPath(JsonElement e)
    {
        if (!e.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object) return null;
        if (content.TryGetProperty("image_path", out var path) && path.ValueKind == JsonValueKind.String)
            return path.GetString();
        foreach (var sourceName in new[] { "source", "image_source" })
            if (content.TryGetProperty(sourceName, out var source) && source.ValueKind == JsonValueKind.Object
                && source.TryGetProperty("path", out var sourcePath) && sourcePath.ValueKind == JsonValueKind.String)
                return sourcePath.GetString();
        return null;
    }

    /// <summary>MinerU 4 ZIP：优先读取 structured_content.json，兼容 middle_json.json 及旧 content_list.json。</summary>
    public static (List<ParsedBlock> Blocks, List<ParsedImage> Images) ExtractV4Zip(byte[] zipBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var jsonEntry = archive.Entries.FirstOrDefault(e => e.Name.Equals("structured_content.json", StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.FirstOrDefault(e => e.Name.Equals("middle_json.json", StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.FirstOrDefault(e => e.Name.Equals("content_list.json", StringComparison.OrdinalIgnoreCase));
        if (jsonEntry is null)
            throw new ParseContentException("MinerU 解析结果包中没有 structured_content.json 或 middle_json.json");
        using var stream = jsonEntry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        using var doc = ParseJson(Encoding.UTF8.GetString(ms.ToArray()).TrimStart('\uFEFF'), "MinerU 解析结果");
        var root = doc.RootElement;
        List<ParsedBlock> blocks;
        List<ImageRef> refs;
        if (root.ValueKind == JsonValueKind.Array)
        {
            blocks = MapContentList(root);
            refs = ListImageRefs(root);
        }
        else
        {
            blocks = MapStructuredContent(root);
            refs = ListStructuredImageRefs(root);
        }
        var images = new List<ParsedImage>();
        foreach (var r in refs)
        {
            var img = archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith(r.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (img is null || img.Length == 0) continue;
            using var s = img.Open();
            using var buf = new MemoryStream();
            s.CopyTo(buf);
            images.Add(new ParsedImage(buf.ToArray(), Path.GetFileName(r.Path), ImageContentType(r.Path), r.Caption, r.PageNo, r.Bbox));
        }
        return (blocks, images);
    }

    private static List<ImageRef> ListStructuredImageRefs(JsonElement root)
    {
        var refs = new List<ImageRef>();
        if (!root.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array) return refs;
        foreach (var pageElement in pages.EnumerateArray())
        {
            var page = PageNumber(pageElement);
            if (!pageElement.TryGetProperty("blocks", out var blocks)
                && !pageElement.TryGetProperty("items", out blocks)) continue;
            if (blocks.ValueKind != JsonValueKind.Array) continue;
            foreach (var block in blocks.EnumerateArray())
            {
                var type = GetString(block, "type")?.ToLowerInvariant();
                if (type is not ("image" or "table" or "chart")) continue;
                var path = GetString(block, "image_path") ?? GetString(block, "img_path") ??
                           GetString(block, "path") ?? NestedPath(block);
                if (string.IsNullOrWhiteSpace(path)) continue;
                var caption = StructuredText(block, "caption") ?? StructuredText(block, "image_caption") ??
                              StructuredText(block, "table_caption");
                refs.Add(new ImageRef(path, caption, page, Bbox(block)));
            }
        }
        return refs;
    }

    /// <summary>响应形如 {"results": {"文件名": {"content_list": …}}}。content_list 随版本
    /// 可能是数组、也可能是 JSON 字符串，两种都接。每次只送一个文件，取 results 首个条目——
    /// 键名是引擎处理过的文件名，不做精确匹配。</summary>
    public static List<ParsedBlock> MapResponse(JsonElement root) => MapResponseFull(root).Blocks;

    public static (List<ParsedBlock> Blocks, List<ParsedImage> Images) MapResponseFull(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
            throw new ParseContentException("引擎响应缺少 results 字段（确认解析服务地址指向 mineru-api 的 /file_parse）");
        JsonElement entry = default;
        var found = false;
        foreach (var p in results.EnumerateObject()) { entry = p.Value; found = true; break; }
        if (!found)
            throw new ParseContentException("引擎响应 results 为空");
        if (!entry.TryGetProperty("content_list", out var cl) || cl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new ParseContentException("引擎响应缺少 content_list（请求已带 return_content_list=true，请检查服务版本）");

        JsonDocument? fromString = null;
        try
        {
            if (cl.ValueKind == JsonValueKind.String)
            {
                fromString = JsonDocument.Parse(cl.GetString() ?? "[]");
                cl = fromString.RootElement;
            }
            if (cl.ValueKind != JsonValueKind.Array)
                throw new ParseContentException("引擎 content_list 不是数组");
            return (MapContentList(cl), MapInlineImages(cl, entry));
        }
        catch (JsonException ex)
        {
            throw new ParseContentException($"引擎 content_list 不是合法 JSON：{ex.Message}");
        }
        finally { fromString?.Dispose(); }
    }

    /// <summary>本地引擎的图片走 images 字典（文件名 → data URI base64）。引用路径形如
    /// images/abc.jpg，字典键可能只有文件名——按尾段匹配。图片解不出来不挡正文入库。</summary>
    private static List<ParsedImage> MapInlineImages(JsonElement contentList, JsonElement entry)
    {
        var images = new List<ParsedImage>();
        if (!entry.TryGetProperty("images", out var dict) || dict.ValueKind != JsonValueKind.Object)
            return images;
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in dict.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String)
                byName[Path.GetFileName(p.Name)] = p.Value.GetString()!;
        foreach (var r in ListImageRefs(contentList))
        {
            if (!byName.TryGetValue(Path.GetFileName(r.Path), out var dataUri)) continue;
            var comma = dataUri.IndexOf(',');
            if (comma < 0) continue;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(dataUri[(comma + 1)..]); }
            catch (FormatException) { continue; }
            if (bytes.Length == 0) continue;
            images.Add(new ParsedImage(bytes, Path.GetFileName(r.Path), ImageContentType(r.Path), r.Caption, r.PageNo, r.Bbox));
        }
        return images;
    }

    /// <summary>content_list 数组 → 统一解析块。在线客户端（结果包里的同名文件）复用同一份映射。</summary>
    public static List<ParsedBlock> MapContentList(JsonElement arr)
    {
        var blocks = new List<ParsedBlock>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var type = GetString(e, "type") ?? "";
            int? page = e.TryGetProperty("page_idx", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() + 1 : null;
            string? bbox = e.TryGetProperty("bbox", out var bb) && bb.ValueKind == JsonValueKind.Array
                ? string.Join(",", bb.EnumerateArray().Select(v => v.GetRawText())) : null;
            switch (type)
            {
                case "text":
                {
                    var text = GetString(e, "text")?.Trim();
                    if (string.IsNullOrEmpty(text)) break;
                    var level = e.TryGetProperty("text_level", out var lv) && lv.ValueKind == JsonValueKind.Number
                        ? lv.GetInt32() : 0;
                    blocks.Add(level >= 1
                        ? new ParsedBlock("heading", text, level, page, bbox, null)
                        : new ParsedBlock("paragraph", text, null, page, bbox, null));
                    break;
                }
                case "list":
                {
                    // 列表整体一段：拆成孤行会让切分把步骤序列截断
                    var items = e.TryGetProperty("list_items", out var li) && li.ValueKind == JsonValueKind.Array
                        ? li.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString()!.Trim()).Where(s => s.Length > 0).ToList()
                        : [];
                    if (items.Count > 0)
                        blocks.Add(new ParsedBlock("paragraph", string.Join("\n", items), null, page, bbox, null));
                    break;
                }
                case "table":
                {
                    var caption = JoinStrings(e, "table_caption");
                    if (caption.Length > 0)
                        blocks.Add(new ParsedBlock("paragraph", caption, null, page, bbox, null));
                    var html = GetString(e, "table_body");
                    if (!string.IsNullOrWhiteSpace(html))
                    {
                        var rows = HtmlTableRows(html);
                        // 首行视作表头（引擎输出的表格首行即列名行），后续行携带表头上下文
                        var header = rows.Count > 1 ? string.Join(" | ", rows[0]) : null;
                        for (var i = rows.Count > 1 ? 1 : 0; i < rows.Count; i++)
                            blocks.Add(new ParsedBlock("table_row", string.Join(" | ", rows[i]), null, page, bbox, header));
                    }
                    var footnote = JoinStrings(e, "table_footnote");
                    if (footnote.Length > 0)
                        blocks.Add(new ParsedBlock("paragraph", footnote, null, page, bbox, null));
                    break;
                }
                case "equation":
                {
                    var text = GetString(e, "text")?.Trim();
                    if (!string.IsNullOrEmpty(text))
                        blocks.Add(new ParsedBlock("paragraph", text, null, page, bbox, null));
                    break;
                }
                case "image":
                {
                    // 图片本体不入文本库；题注是检索有价值的文字，保留
                    var caption = JoinStrings(e, "image_caption");
                    if (caption.Length > 0)
                        blocks.Add(new ParsedBlock("paragraph", caption, null, page, bbox, null));
                    break;
                }
                case "code":
                {
                    var caption = JoinStrings(e, "code_caption");
                    var body = GetString(e, "code_body")?.Trim();
                    var text = string.Join("\n", new[] { caption, body }.Where(s => !string.IsNullOrEmpty(s)));
                    if (text.Length > 0)
                        blocks.Add(new ParsedBlock("paragraph", text, null, page, bbox, null));
                    break;
                }
            }
        }
        return blocks;
    }

    /// <summary>机器生成的表格 HTML 拆行：tr → 行，td/th → 单元格，去标签、还原实体、压缩空白。
    /// 不处理任意手写 HTML——引擎输出结构规整，够用且可测。</summary>
    internal static List<string[]> HtmlTableRows(string html)
    {
        var rows = new List<string[]>();
        foreach (Match tr in Regex.Matches(html, "<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var cells = Regex.Matches(tr.Groups[1].Value, "<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                .Select(m => Regex.Replace(
                    System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ")),
                    @"\s+", " ").Trim())
                .ToArray();
            if (cells.Length > 0 && cells.Any(c => c.Length > 0)) rows.Add(cells);
        }
        return rows;
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>题注/脚注是字符串数组，合并成一行。</summary>
    private static string JoinStrings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return "";
        return string.Join("　", v.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!.Trim())
            .Where(s => s.Length > 0));
    }

    /// <summary>content_list 里的图片引用（image 与带截图的 table 条目）：路径、题注、页码、位置。
    /// 本地端按 images 字典取字节，在线端按结果包内路径取——引用提取共用这一份。</summary>
    public record ImageRef(string Path, string? Caption, int? PageNo, string? Bbox);

    public static List<ImageRef> ListImageRefs(JsonElement contentList)
    {
        var refs = new List<ImageRef>();
        foreach (var e in contentList.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var type = GetString(e, "type");
            if (type is not ("image" or "table")) continue;
            var path = GetString(e, "img_path");
            if (string.IsNullOrWhiteSpace(path)) continue;
            int? page = e.TryGetProperty("page_idx", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() + 1 : null;
            string? bbox = e.TryGetProperty("bbox", out var bb) && bb.ValueKind == JsonValueKind.Array
                ? string.Join(",", bb.EnumerateArray().Select(v => v.GetRawText())) : null;
            var caption = JoinStrings(e, type == "image" ? "image_caption" : "table_caption");
            refs.Add(new ImageRef(path, caption.Length > 0 ? caption : null, page, bbox));
        }
        return refs;
    }

    /// <summary>按扩展名给内容类型；未知按 jpeg。</summary>
    public static string ImageContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };

    /// <summary>非 ASCII 与引号替换为下划线；空结果回退 doc+原扩展名。</summary>
    internal static string SafeAsciiFileName(string fileName)
    {
        var cleaned = Regex.Replace(fileName, "[^\\x20-\\x7E]|[\"\\\\]", "_").Trim();
        if (cleaned.Trim('_', ' ', '.').Length == 0)
            cleaned = "doc" + Path.GetExtension(fileName).ToLowerInvariant();
        return cleaned;
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
