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
    public record RunResult(string Stdout, string? Error, IReadOnlyList<string> Images, double Seconds);

    public async Task<RunResult> RunAsync(string baseUrl, string code, int timeoutSeconds, CancellationToken ct)
    {
        var client = http.CreateClient("tools");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 10));
        using var resp = await client.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/run", new { code, timeout = timeoutSeconds }, cts.Token);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
        var r = doc.RootElement;
        var images = r.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Array
            ? im.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Take(3).ToList() : [];
        return new RunResult(
            r.TryGetProperty("stdout", out var o) ? o.GetString() ?? "" : "",
            r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
            images,
            r.TryGetProperty("seconds", out var s) && s.TryGetDouble(out var sec) ? sec : 0);
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

    public async Task<Page> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !NetGuard.IsAllowedUrl(uri))
            throw new InvalidOperationException("只能打开 http/https 的公开网页");
        var client = http.CreateClient("webfetch");
        for (var hop = 0; hop < 4; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; HTLAB-Assistant/1.0)");
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                uri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                if (!NetGuard.IsAllowedUrl(uri)) throw new InvalidOperationException("网页跳转到了不允许的地址");
                continue;
            }
            resp.EnsureSuccessStatusCode();
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Contains("pdf")) return new Page(uri.ToString(), uri.Host, "", "这个链接是 PDF 文件，暂时读不了网页里的 PDF；可以下载后作为附件上传");
            if (!(type.Contains("html") || type.StartsWith("text/") || type.Length == 0))
                return new Page(uri.ToString(), uri.Host, "", $"这个链接不是网页（{type}），读不了");
            var bytes = await ReadCappedAsync(await resp.Content.ReadAsStreamAsync(ct), 3 * 1024 * 1024, ct);
            var html = Decode(bytes, resp.Content.Headers.ContentType?.CharSet);
            var (title, text) = type.Contains("html") || html.Contains("<html", StringComparison.OrdinalIgnoreCase) ? WebText.Extract(html) : ("", html);
            return new Page(uri.ToString(), string.IsNullOrWhiteSpace(title) ? uri.Host : title, text, null);
        }
        throw new InvalidOperationException("网页跳转次数太多");
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
