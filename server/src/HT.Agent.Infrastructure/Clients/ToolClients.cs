using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HT.Agent.Application.Logic;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>代码解释器：调服务器上隔离容器里的 Python 运行服务（deploy/sandbox）。容器不能上网、碰不到数据库和文件。</summary>
public class SandboxClient(IHttpClientFactory http)
{
    /// <summary>代码写到 out/ 的文件（做好的 Excel、Word……）。</summary>
    public record OutFile(string Name, byte[] Data);
    /// <summary>放进 in/ 的文件（对话附件的只读副本）。</summary>
    public record InFile(string Name, byte[] Data);

    public record RunResult(string Stdout, string? Error, IReadOnlyList<string> Images, double Seconds,
        IReadOnlyList<OutFile>? Files = null, IReadOnlyList<string>? Skipped = null);

    public async Task<RunResult> RunAsync(string baseUrl, string code, int timeoutSeconds, CancellationToken ct, IReadOnlyList<InFile>? inputs = null)
    {
        var client = http.CreateClient("sandbox");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 30));
        var body = new
        {
            code, timeout = timeoutSeconds,
            inputs = (inputs ?? []).Select(f => new { name = f.Name, data = Convert.ToBase64String(f.Data) }).ToList()
        };
        // 一定要带 Content-Length：PostAsJsonAsync 会分块发（不带长度），沙箱用的 Python http.server 读到的就是空的
        using var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync($"{baseUrl.TrimEnd('/')}/run", content, cts.Token);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
        var r = doc.RootElement;
        var images = r.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Array
            ? im.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Take(3).ToList() : [];
        var files = r.TryGetProperty("files", out var fs) && fs.ValueKind == JsonValueKind.Array
            ? fs.EnumerateArray().Select(f => new OutFile(f.GetProperty("name").GetString() ?? "file", Convert.FromBase64String(f.GetProperty("data").GetString() ?? ""))).ToList()
            : [];
        var skipped = r.TryGetProperty("skipped", out var sk) && sk.ValueKind == JsonValueKind.Array
            ? sk.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
        return new RunResult(
            r.TryGetProperty("stdout", out var o) ? o.GetString() ?? "" : "",
            r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
            images,
            r.TryGetProperty("seconds", out var s) && s.TryGetDouble(out var sec) ? sec : 0,
            files, skipped);
    }

    public async Task<bool> HealthyAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp = await http.CreateClient("tools").GetAsync($"{baseUrl.TrimEnd('/')}/health", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }
}

/// <summary>检查助手做的文件（office 容器的 /check）：能不能打开、Excel 公式重算后有没有错、Word 修订批注几处、几页。
/// 地址跟导出 PDF 用的转换服务是同一个容器。连不上返回 null，调用方只做基本检查。</summary>
public class OfficeCheckClient(IHttpClientFactory http, HT.Agent.Application.Abstractions.IRuntimeConfig config)
{
    /// <summary>Recalculated：Excel 重算后的那一版（公式带上计算结果，手机预览不再是空白），有就交这一版。</summary>
    public record Result(bool Ok, IReadOnlyList<string> Problems, JsonElement? Info, byte[]? Recalculated = null);

    public async Task<Result?> CheckAsync(string fileName, byte[] data, CancellationToken ct)
    {
        var conv = (await config.GetStringAsync(HT.Agent.Domain.ConfigKeys.PdfConverterUrl, "", ct)).Trim();
        if (conv.Length == 0) conv = "http://office:3000/convert";
        try
        {
            var url = new Uri(new Uri(conv), "/check");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(150));
            using var form = new MultipartFormDataContent { { new ByteArrayContent(data), "file", fileName } };
            using var resp = await http.CreateClient("sandbox").PostAsync(url, form, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var r = doc.RootElement;
            var problems = r.TryGetProperty("problems", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
            JsonElement? info = r.TryGetProperty("info", out var i) ? i.Clone() : null;
            var fixedFile = r.TryGetProperty("recalculated", out var rc) && rc.ValueKind == JsonValueKind.String ? Convert.FromBase64String(rc.GetString()!) : null;
            return new Result(problems.Count == 0, problems, info, fixedFile);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or JsonException) { return null; }
    }
}

/// <summary>化学品数据：PubChem（美国国家医学图书馆的公开化学数据库，免费、不用注册）。名称要用英文名或 CAS 号。</summary>
public class PubChemClient(IHttpClientFactory http)
{
    private const string Base = "https://pubchem.ncbi.nlm.nih.gov/rest";

    public record Info(int Cid, string Title, string? Formula, string? Weight, string? Cas, string? Iupac,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Props, IReadOnlyList<string> Hazards, IReadOnlyList<string> Pictograms);

    private static readonly (string Heading, string Label)[] Headings =
    [
        ("Boiling Point", "沸点"), ("Melting Point", "熔点"), ("Flash Point", "闪点"), ("Vapor Pressure", "蒸气压"),
        ("Density", "密度"), ("Solubility", "溶解性"), ("Autoignition Temperature", "自燃温度"), ("Decomposition", "分解"),
        ("Corrosivity", "腐蚀性"), ("Stability/Shelf Life", "稳定性"),
    ];

    public async Task<Info?> LookupAsync(string nameOrCas, CancellationToken ct)
    {
        var c = http.CreateClient("tools");
        var cidResp = await c.GetAsync($"{Base}/pug/compound/name/{Uri.EscapeDataString(nameOrCas.Trim())}/cids/JSON", ct);
        if (cidResp.StatusCode == HttpStatusCode.NotFound) return null;
        cidResp.EnsureSuccessStatusCode();
        using var cidDoc = JsonDocument.Parse(await cidResp.Content.ReadAsStringAsync(ct));
        var cid = cidDoc.RootElement.GetProperty("IdentifierList").GetProperty("CID")[0].GetInt32();

        string? formula = null, weight = null, iupac = null, title = nameOrCas;
        using (var p = JsonDocument.Parse(await c.GetStringAsync($"{Base}/pug/compound/cid/{cid}/property/Title,MolecularFormula,MolecularWeight,IUPACName/JSON", ct)))
        {
            var row = p.RootElement.GetProperty("PropertyTable").GetProperty("Properties")[0];
            title = Str(row, "Title") ?? title;
            formula = Str(row, "MolecularFormula");
            weight = Str(row, "MolecularWeight");
            iupac = Str(row, "IUPACName");
        }
        string? cas = null;
        try
        {
            using var syn = JsonDocument.Parse(await c.GetStringAsync($"{Base}/pug/compound/cid/{cid}/synonyms/JSON", ct));
            cas = syn.RootElement.GetProperty("InformationList").GetProperty("Information")[0].GetProperty("Synonym")
                .EnumerateArray().Select(x => x.GetString() ?? "").FirstOrDefault(x => Regex.IsMatch(x, @"^\d{2,7}-\d{2}-\d$"));
        }
        catch (Exception ex) when (ex is HttpRequestException or KeyNotFoundException or JsonException) { }

        var props = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (heading, label) in Headings)
        {
            var vals = await SectionStringsAsync(c, cid, heading, ct);
            if (vals.Count > 0) props[label] = vals.Take(3).Select(v => v.Length > 160 ? v[..160] + "…" : v).ToList();
        }
        var ghs = await SectionStringsAsync(c, cid, "GHS Classification", ct, withMarkup: true);
        var hazards = ghs.Where(s => Regex.IsMatch(s, @"^H\d{3}")).Select(s => s.Split('[')[0].Trim()).Distinct().Take(12).ToList();
        var pictos = ghs.Where(s => s.StartsWith("pictogram:", StringComparison.Ordinal)).Select(s => s["pictogram:".Length..]).Distinct().Take(9).ToList();
        return new Info(cid, title, formula, weight, cas, iupac, props, hazards, pictos);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    /// <summary>PUG View 的某个小节下所有文字值（含数值+单位）。小节不存在返回空。</summary>
    private static async Task<List<string>> SectionStringsAsync(HttpClient c, int cid, string heading, CancellationToken ct, bool withMarkup = false)
    {
        var resp = await c.GetAsync($"{Base}/pug_view/data/compound/{cid}/JSON?heading={Uri.EscapeDataString(heading)}", ct);
        if (!resp.IsSuccessStatusCode) return [];
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<string>();
        void Walk(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("Value", out var v))
                {
                    if (v.TryGetProperty("StringWithMarkup", out var swm))
                        foreach (var x in swm.EnumerateArray())
                        {
                            if (x.TryGetProperty("String", out var s) && s.GetString() is { Length: > 0 } str) list.Add(str);
                            if (withMarkup && x.TryGetProperty("Markup", out var mk))
                                foreach (var m in mk.EnumerateArray())
                                    if (m.TryGetProperty("Type", out var t) && t.GetString() == "Icon" && m.TryGetProperty("Extra", out var ex))
                                        list.Add("pictogram:" + ex.GetString());
                        }
                    if (v.TryGetProperty("Number", out var num) && num.ValueKind == JsonValueKind.Array)
                        list.Add(string.Join(" ", num.EnumerateArray().Select(n => n.ToString())) + (v.TryGetProperty("Unit", out var u) ? " " + u.GetString() : ""));
                }
                foreach (var p in el.EnumerateObject()) if (p.Name != "Value") Walk(p.Value);
            }
            else if (el.ValueKind == JsonValueKind.Array) foreach (var x in el.EnumerateArray()) Walk(x);
        }
        Walk(doc.RootElement);
        return list.Distinct().ToList();
    }
}

/// <summary>联网搜索：自建 SearXNG（把问题转给必应、百度、搜狗等再合并结果，免费，不用密钥）。</summary>
public class SearxClient(IHttpClientFactory http)
{
    public record Hit(string Title, string Url, string Snippet, string Engine);

    public async Task<IReadOnlyList<Hit>> SearchAsync(string baseUrl, string query, string engines, int take, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/search?format=json&language=zh-CN&safesearch=0&q={Uri.EscapeDataString(query)}"
                  + (string.IsNullOrWhiteSpace(engines) ? "" : $"&engines={Uri.EscapeDataString(engines)}");
        using var doc = JsonDocument.Parse(await http.CreateClient("tools").GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];
        return results.EnumerateArray().Select(r => new Hit(
                r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                r.TryGetProperty("engine", out var e) ? e.GetString() ?? "" : ""))
            .Where(h => h.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(h => h.Url).Take(take).ToList();
    }
}

/// <summary>打开网页：只连公网地址（连接那一刻按真实解析出的 IP 判断，换 DNS 也绕不进内网），
/// 只认 http/https 的 80/443，最多跟 3 次跳转、读 3MB、等 15 秒。</summary>
public class WebFetcher(IHttpClientFactory http)
{
    public record Page(string Url, string Title, string Text, string? Note);

    static WebFetcher() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // GBK 等国内网页编码

    /// <summary>给命名客户端用的底层处理器：连接前校验目标 IP。</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ip = addrs.FirstOrDefault(NetGuard.IsPublic)
                     ?? throw new HttpRequestException("这个网址指向内网或本机地址，不能打开");
            if (addrs.Any(a => !NetGuard.IsPublic(a))) throw new HttpRequestException("这个网址解析到了内网地址，不能打开");
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
    };

    /// <summary>取回来的原始网页：最终网址、HTML、能不能被别的网站嵌入显示（看 X-Frame-Options 和 CSP frame-ancestors）。</summary>
    public record Raw(Uri Url, string Html, string Type, bool Embeddable, string? Note);

    public async Task<Page> FetchAsync(string url, CancellationToken ct)
    {
        var raw = await FetchRawAsync(url, ct);
        if (raw.Note is not null) return new Page(raw.Url.ToString(), raw.Url.Host, "", raw.Note);
        var (title, text) = raw.Type.Contains("html") || raw.Html.Contains("<html", StringComparison.OrdinalIgnoreCase) ? WebText.Extract(raw.Html) : ("", raw.Html);
        return new Page(raw.Url.ToString(), string.IsNullOrWhiteSpace(title) ? raw.Url.Host : title, text, null);
    }

    public async Task<Raw> FetchRawAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !NetGuard.IsAllowedUrl(uri))
            throw new InvalidOperationException("只能打开 http/https 的公开网页");
        var client = http.CreateClient("webfetch");
        for (var hop = 0; hop < 4; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; HTLAB-Assistant/1.0)");
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5");
            req.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.6");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                uri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                if (!NetGuard.IsAllowedUrl(uri)) throw new InvalidOperationException("网页跳转到了不允许的地址");
                continue;
            }
            if ((int)resp.StatusCode is 401 or 403) return new Raw(uri, "", "", false, "这个网站拒绝了访问（可能要登录，或者不让程序读取）");
            resp.EnsureSuccessStatusCode();
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            var embeddable = Embeddable(resp);
            if (type.Contains("pdf")) return new Raw(uri, "", type, embeddable, "这个链接是 PDF 文件，暂时读不了网页里的 PDF；可以下载后作为附件上传");
            if (!(type.Contains("html") || type.StartsWith("text/") || type.Length == 0))
                return new Raw(uri, "", type, embeddable, $"这个链接不是网页（{type}），读不了");
            var bytes = await ReadCappedAsync(await resp.Content.ReadAsStreamAsync(ct), 3 * 1024 * 1024, ct);
            return new Raw(uri, Decode(bytes, resp.Content.Headers.ContentType?.CharSet), type, embeddable, null);
        }
        throw new InvalidOperationException("网页跳转次数太多");
    }

    private static bool Embeddable(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("X-Frame-Options", out var xfo) && xfo.Any(v => v.Trim().Length > 0)) return false;
        if (resp.Headers.TryGetValues("Content-Security-Policy", out var csp))
            foreach (var v in csp)
            {
                var m = Regex.Match(v, @"frame-ancestors\s+([^;]+)", RegexOptions.IgnoreCase);
                if (m.Success && m.Groups[1].Value.Trim() != "*") return false;
            }
        return true;
    }

    /// <summary>阅读模式里的图片由服务器代取（用户电脑不一定能上外网）：只收位图，最大 5MB。SVG 不收——它能带脚本。</summary>
    public async Task<(byte[] Bytes, string Type)?> FetchImageAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !NetGuard.IsAllowedUrl(uri)) return null;
        var client = http.CreateClient("webfetch");
        for (var hop = 0; hop < 4; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; HTLAB-Assistant/1.0)");
            req.Headers.Accept.ParseAdd("image/avif,image/webp,image/png,image/jpeg,image/*;q=0.8");
            req.Headers.Referrer = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                uri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                if (!NetGuard.IsAllowedUrl(uri)) return null;
                continue;
            }
            if (!resp.IsSuccessStatusCode) return null;
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (type is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/avif" or "image/bmp")) return null;
            if (resp.Content.Headers.ContentLength is > 5 * 1024 * 1024) return null;
            var bytes = await ReadCappedAsync(await resp.Content.ReadAsStreamAsync(ct), 5 * 1024 * 1024, ct);
            return (bytes, type);
        }
        return null;
    }

    private static async Task<byte[]> ReadCappedAsync(Stream s, int cap, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, Math.Min(n, cap - (int)ms.Length));
            if (ms.Length >= cap) break;
        }
        return ms.ToArray();
    }

    internal static string Decode(byte[] bytes, string? charset)
    {
        Encoding enc = Encoding.UTF8;
        var name = charset;
        if (string.IsNullOrWhiteSpace(name))
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            name = Regex.Match(head, @"charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
        }
        if (!string.IsNullOrWhiteSpace(name))
            try { enc = Encoding.GetEncoding(name.Trim().Trim('"')); } catch (ArgumentException) { }
        return enc.GetString(bytes);
    }
}
