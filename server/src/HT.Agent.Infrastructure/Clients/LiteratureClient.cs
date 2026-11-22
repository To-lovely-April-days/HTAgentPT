using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>专利与论文检索。
/// 论文：OpenAlex（免费、不用注册，覆盖全球大部分期刊）。
/// 专利：欧洲专利局 OPS（免费，需在 developers.epo.org 注册拿一对密钥；含中国专利）。没配密钥就只查论文。</summary>
public class LiteratureClient(IHttpClientFactory http)
{
    /// <summary>FullText：开放获取的全文地址（PDF 或出版社全文页），没有就是 null。</summary>
    public record Item(string Kind, string Title, string Meta, string Abstract, string Url, string? FullText = null);

    // ── 论文 ──────────────────────────────────────────
    /// <summary>fullTextOnly：只要有开放全文的（要读具体实验条件时用）。</summary>
    public async Task<IReadOnlyList<Item>> PapersAsync(string query, int take, string? mailto, CancellationToken ct, bool fullTextOnly = false)
    {
        var url = $"https://api.openalex.org/works?search={Uri.EscapeDataString(query)}&per-page={take}"
                  + (fullTextOnly ? "&filter=is_oa:true" : "")
                  + "&select=id,doi,display_name,publication_year,cited_by_count,primary_location,abstract_inverted_index,open_access,best_oa_location"
                  + (string.IsNullOrWhiteSpace(mailto) ? "" : $"&mailto={Uri.EscapeDataString(mailto)}");
        using var doc = JsonDocument.Parse(await http.CreateClient("tools").GetStringAsync(url, ct));
        var list = new List<Item>();
        foreach (var w in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var title = Str(w, "display_name") ?? "";
            if (title.Length == 0) continue;
            var year = w.TryGetProperty("publication_year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32().ToString() : "";
            var cited = w.TryGetProperty("cited_by_count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            string? venue = null;
            if (w.TryGetProperty("primary_location", out var pl) && pl.ValueKind == JsonValueKind.Object
                && pl.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object) venue = Str(src, "display_name");
            var doi = Str(w, "doi");
            var link = doi ?? Str(w, "id") ?? "";
            var meta = string.Join(" · ", new[] { venue, year, cited > 0 ? $"被引 {cited}" : null }.Where(s => !string.IsNullOrEmpty(s)));
            list.Add(new Item("论文", title, meta, Abstract(w), link, FullTextUrl(w)));
        }
        return list;
    }

    /// <summary>开放获取的全文：优先 PDF，其次出版社的全文页；不是开放获取就没有。</summary>
    internal static string? FullTextUrl(JsonElement w)
    {
        if (w.TryGetProperty("best_oa_location", out var b) && b.ValueKind == JsonValueKind.Object)
        {
            var u = Str(b, "pdf_url") ?? Str(b, "landing_page_url");
            if (!string.IsNullOrWhiteSpace(u)) return u;
        }
        if (w.TryGetProperty("open_access", out var oa) && oa.ValueKind == JsonValueKind.Object
            && oa.TryGetProperty("is_oa", out var io) && io.ValueKind == JsonValueKind.True)
            return Str(oa, "oa_url");
        return null;
    }

    /// <summary>OpenAlex 的摘要是「词 → 位置」倒排表，拼回原文。</summary>
    private static string Abstract(JsonElement w)
    {
        if (!w.TryGetProperty("abstract_inverted_index", out var inv) || inv.ValueKind != JsonValueKind.Object) return "";
        var words = new SortedDictionary<int, string>();
        foreach (var p in inv.EnumerateObject())
            foreach (var pos in p.Value.EnumerateArray())
                if (pos.TryGetInt32(out var i)) words[i] = p.Name;
        var text = string.Join(" ", words.Values);
        return text.Length > 600 ? text[..600] + "…" : text;
    }

    // ── 专利 ──────────────────────────────────────────
    private static string? _token;
    private static DateTimeOffset _tokenExpires;
    private static readonly SemaphoreSlim TokenLock = new(1, 1);

    private async Task<string> TokenAsync(string key, string secret, CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires) return _token;
        await TokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires) return _token;
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://ops.epo.org/3.2/auth/accesstoken")
            {
                Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")])
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{secret}")));
            using var resp = await http.CreateClient("tools").SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"欧洲专利局接口认证失败（{(int)resp.StatusCode}），请检查密钥");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            _token = doc.RootElement.GetProperty("access_token").GetString();
            var secs = doc.RootElement.TryGetProperty("expires_in", out var e) && int.TryParse(e.ToString(), out var s) ? s : 1200;
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, secs - 60));
            return _token!;
        }
        finally { TokenLock.Release(); }
    }

    public async Task<IReadOnlyList<Item>> PatentsAsync(string query, int take, string key, string secret, CancellationToken ct)
    {
        var token = await TokenAsync(key, secret, ct);
        // CQL：标题或摘要里同时出现这些词
        var cql = $"ta all \"{query.Replace("\"", " ")}\"";
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://ops.epo.org/3.2/rest-services/published-data/search/biblio?q={Uri.EscapeDataString(cql)}&Range=1-{take}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await http.CreateClient("tools").SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return []; // 没有结果时 OPS 回 404
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var docs = new List<JsonElement>();
        Collect(doc.RootElement, "exchange-document", docs);
        var list = new List<Item>();
        foreach (var d in docs.Take(take))
        {
            var cc = Str(d, "@country") ?? ""; var no = Str(d, "@doc-number") ?? ""; var kind = Str(d, "@kind") ?? "";
            var bib = d.TryGetProperty("bibliographic-data", out var b) ? b : d;
            var titles = Texts(bib, "invention-title");
            var title = titles.FirstOrDefault(t => t.Lang == "zh").Text ?? titles.FirstOrDefault(t => t.Lang == "en").Text ?? titles.FirstOrDefault().Text ?? $"{cc}{no}";
            var applicants = Texts(bib, "applicant-name").Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(2).ToList();
            var date = Texts(bib, "date").Select(t => t.Text).FirstOrDefault(t => t?.Length == 8);
            var abs = string.Join(" ", Texts(d, "abstract").Where(t => t.Lang is "en" or "zh" or null).Select(t => t.Text)).Trim();
            var meta = string.Join(" · ", new[] { $"{cc}{no}{kind}", date is null ? null : date[..4], applicants.Count > 0 ? "申请人：" + string.Join("、", applicants) : null }.Where(s => !string.IsNullOrEmpty(s)));
            list.Add(new Item("专利", title, meta, abs.Length > 600 ? abs[..600] + "…" : abs,
                $"https://worldwide.espacenet.com/patent/search?q=pn%3D{cc}{no}{kind}"));
        }
        return list;
    }

    private static void Collect(JsonElement el, string key, List<JsonElement> into)
    {
        if (el.ValueKind == JsonValueKind.Object)
            foreach (var p in el.EnumerateObject())
            {
                if (p.Name == key)
                {
                    if (p.Value.ValueKind == JsonValueKind.Array) into.AddRange(p.Value.EnumerateArray());
                    else into.Add(p.Value);
                }
                else Collect(p.Value, key, into);
            }
        else if (el.ValueKind == JsonValueKind.Array)
            foreach (var x in el.EnumerateArray()) Collect(x, key, into);
    }

    /// <summary>某个键下面所有 "$" 文字（带 @lang）。OPS 的 JSON 是 XML 直译，单个和多个的形状不一样，这里统一抹平。</summary>
    private static List<(string? Lang, string Text)> Texts(JsonElement el, string key)
    {
        var nodes = new List<JsonElement>();
        Collect(el, key, nodes);
        var list = new List<(string?, string)>();
        void Walk(JsonElement n, string? lang)
        {
            if (n.ValueKind == JsonValueKind.Object)
            {
                var l = Str(n, "@lang") ?? lang;
                if (n.TryGetProperty("$", out var v) && v.ValueKind == JsonValueKind.String) list.Add((l, v.GetString()!));
                foreach (var p in n.EnumerateObject()) if (p.Name != "$") Walk(p.Value, l);
            }
            else if (n.ValueKind == JsonValueKind.Array) foreach (var x in n.EnumerateArray()) Walk(x, lang);
        }
        foreach (var n in nodes) Walk(n, null);
        return list;
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? null : v.ToString()) : null;

    /// <summary>密钥能不能换到令牌（设置页「已连通」用）。</summary>
    public async Task<bool> CheckPatentKeyAsync(string key, string secret, CancellationToken ct)
    {
        try { _token = null; await TokenAsync(key, secret, ct); return true; }
        catch (HttpRequestException) { return false; }
    }
}
