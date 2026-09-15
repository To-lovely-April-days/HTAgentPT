using System.Text.Json;
using System.Text.RegularExpressions;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>MinerU 解析服务客户端（POST {parser.url}，即 mineru-api 的 /file_parse）。
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

        var url = await config.GetStringAsync(ConfigKeys.ParserUrl, "http://127.0.0.1:8000/file_parse", ct);
        var backend = await config.GetStringAsync(ConfigKeys.ParserBackend, "pipeline", ct);
        var timeout = await config.GetIntAsync(ConfigKeys.ParserTimeoutSeconds, 300, ct);
        var maxRetries = Math.Max(0, await config.GetIntAsync(ConfigKeys.ParserMaxRetries, 3, ct));
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var http = httpFactory.CreateClient("parser");

        HttpResponseMessage resp = null!;
        for (var attempt = 0; ; attempt++)
        {
            buffer.Position = 0;
            using var form = new MultipartFormDataContent();
            var fileContent = new StreamContent(buffer);
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
        if (!resp.IsSuccessStatusCode)
        {
            var reason = await resp.Content.ReadAsStringAsync(ct);
            throw new ParseContentException($"引擎拒绝该文件（HTTP {(int)resp.StatusCode}）：{Truncate(reason)}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var blocks = MapResponse(doc.RootElement);
        if (blocks.Count == 0)
            throw new ParseContentException("引擎未解析出任何内容块（文件可能损坏、为空或全为无法识别的图像）");
        return new ParsedDocument(blocks);
    }

    /// <summary>响应形如 {"results": {"文件名": {"content_list": …}}}。content_list 随版本
    /// 可能是数组、也可能是 JSON 字符串，两种都接。每次只送一个文件，取 results 首个条目——
    /// 键名是引擎处理过的文件名，不做精确匹配。</summary>
    public static List<ParsedBlock> MapResponse(JsonElement root)
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
            return MapContentList(cl);
        }
        catch (JsonException ex)
        {
            throw new ParseContentException($"引擎 content_list 不是合法 JSON：{ex.Message}");
        }
        finally { fromString?.Dispose(); }
    }

    private static List<ParsedBlock> MapContentList(JsonElement arr)
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
