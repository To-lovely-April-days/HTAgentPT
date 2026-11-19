using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>网页阅读模式：从 HTML 里找出正文那一块，拆成标题、段落、列表、表格、图片、代码几种块。
/// 脚本、样式、导航、页脚、表单、广告位都去掉；输出的是纯数据（不是 HTML），前端按块排版，不会执行网页里的任何东西。</summary>
public static class WebReader
{
    public record Block(string Type, string? Text = null, int? Level = null, List<List<string>>? Rows = null, string? Src = null);
    public record Article(string Title, string? Site, string? Published, List<Block> Blocks);

    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly Regex Comment = new(@"<!--[\s\S]*?-->", RegexOptions.Compiled);
    private static readonly Regex Drop = new(@"<(script|style|noscript|svg|template|iframe|object|embed|canvas|form|button|select|textarea|video|audio)\b[^>]*>[\s\S]*?</\1\s*>", Opt);
    private static readonly Regex DropChrome = new(@"<(nav|footer|aside)\b[^>]*>[\s\S]*?</\1\s*>", Opt);
    private static readonly Regex TitleRe = new(@"<title[^>]*>([\s\S]*?)</title>", Opt);
    private static readonly Regex Meta = new(@"<meta\b[^>]*>", Opt);
    private static readonly Regex Attr = new(@"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", Opt);
    private static readonly Regex Token = new(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)\b([^>]*)>", Opt);
    private static readonly Regex Open = new(@"<(article|main|div|section|td)\b([^>]*)>", Opt);
    private static readonly Regex Good = new(@"article|content|post|entry|main|detail|text|body|news|story|正文", Opt);
    private static readonly Regex Bad = new(@"comment|footer|sidebar|side-bar|nav|menu|related|recommend|share|social|banner|advert|\bads?\b|breadcrumb|toolbar|login|popup|modal|cookie", Opt);
    private static readonly Regex Space = new(@"[ \t\r\n\f\v 　]+", RegexOptions.Compiled);

    public static Article Read(string html, Uri baseUri)
    {
        var title = TitleRe.Match(html) is { Success: true } t ? Clean(WebUtility.HtmlDecode(Regex.Replace(t.Groups[1].Value, "<[^>]+>", ""))) : "";
        string? site = null, published = null, ogTitle = null;
        foreach (Match m in Meta.Matches(html))
        {
            var a = Attrs(m.Value);
            var key = (a.GetValueOrDefault("property") ?? a.GetValueOrDefault("name") ?? a.GetValueOrDefault("itemprop") ?? "").ToLowerInvariant();
            var content = a.GetValueOrDefault("content");
            if (string.IsNullOrWhiteSpace(content)) continue;
            content = Clean(WebUtility.HtmlDecode(content));
            if (key == "og:site_name") site ??= content;
            else if (key == "og:title") ogTitle ??= content;
            else if (key is "article:published_time" or "pubdate" or "publishdate" or "datepublished" or "date") published ??= content.Length >= 10 ? content[..10] : content;
        }
        var s = Comment.Replace(html, " ");
        s = Drop.Replace(s, " ");
        var body = PickMain(s);
        body = DropChrome.Replace(body, " ");
        var blocks = Blocks(body, baseUri);
        // 页面标题常带「- 网站名」，正文第一个标题跟它重复就去掉
        var name = ogTitle ?? title;
        if (blocks.Count > 0 && blocks[0].Type == "heading" && (blocks[0].Text == name || name.StartsWith(blocks[0].Text ?? "\u0000", StringComparison.Ordinal)))
        {
            name = blocks[0].Text!;
            blocks.RemoveAt(0);
        }
        return new Article(string.IsNullOrWhiteSpace(name) ? baseUri.Host : name, site, published, blocks);
    }

    private static Dictionary<string, string> Attrs(string tag)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Attr.Matches(tag))
            map.TryAdd(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value);
        return map;
    }

    /// <summary>从开始标签往后找到配对的结束标签（同名标签计数）。</summary>
    private static string Element(string s, int start, string tag)
    {
        var depth = 0;
        var re = new Regex($@"<(/?){tag}\b[^>]*>", RegexOptions.IgnoreCase);
        for (var m = re.Match(s, start); m.Success; m = m.NextMatch())
        {
            depth += m.Groups[1].Value == "/" ? -1 : 1;
            if (depth == 0) return s.Substring(start, m.Index + m.Length - start);
        }
        return s[start..];
    }

    private static int TextLen(string html) => Clean(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "))).Length;

    /// <summary>正文块：article/main，或 class/id 像正文的 div/section 里文字最多的那个；都不像就用 body。</summary>
    private static string PickMain(string s)
    {
        string? best = null; var bestScore = 0.0;
        var tried = 0;
        foreach (Match m in Open.Matches(s))
        {
            var tag = m.Groups[1].Value.ToLowerInvariant();
            var attrs = Attrs(m.Groups[2].Value);
            var hint = $"{attrs.GetValueOrDefault("class")} {attrs.GetValueOrDefault("id")} {attrs.GetValueOrDefault("itemprop")}";
            var semantic = tag is "article" or "main";
            if (!semantic && !Good.IsMatch(hint)) continue;
            if (Bad.IsMatch(hint)) continue;
            if (++tried > 60) break;
            var el = Element(s, m.Index, tag);
            var len = TextLen(el);
            if (len < 140) continue;
            // 链接文字占比高的是导航列表，降分
            var linkText = Regex.Matches(el, @"<a\b[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase).Sum(x => TextLen(x.Groups[1].Value));
            var paras = Regex.Matches(el, @"<p\b", RegexOptions.IgnoreCase).Count;
            var score = len * (1 - Math.Min(0.9, (double)linkText / Math.Max(1, len))) + paras * 40 + (semantic ? 200 : 0);
            if (score > bestScore) { bestScore = score; best = el; }
        }
        if (best is not null) return best;
        var b = Regex.Match(s, @"<body\b[^>]*>", RegexOptions.IgnoreCase);
        return b.Success ? Element(s, b.Index, "body") : s;
    }

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    { "p", "div", "section", "article", "main", "br", "hr", "li", "ul", "ol", "dl", "dt", "dd", "blockquote", "figure", "figcaption", "header", "h1", "h2", "h3", "h4", "h5", "h6", "center" };

    private static List<Block> Blocks(string html, Uri baseUri)
    {
        var list = new List<Block>();
        var text = new StringBuilder();
        string kind = "p"; int level = 0;
        List<List<string>>? table = null; List<string>? row = null; StringBuilder? cell = null; var tableDepth = 0;
        var pre = 0; var preText = new StringBuilder();
        var skip = 0; string? skipTag = null;

        void Flush()
        {
            var t = Clean(WebUtility.HtmlDecode(text.ToString()));
            text.Clear();
            if (t.Length == 0) { kind = "p"; return; }
            if (kind == "heading") list.Add(new Block("heading", t, Math.Clamp(level, 1, 4)));
            else if (kind == "li") list.Add(new Block("li", t));
            else if (kind == "quote") list.Add(new Block("quote", t));
            else if (t.Length > 1) list.Add(new Block("p", t));
            kind = "p";
        }

        var last = 0;
        foreach (Match m in Token.Matches(html))
        {
            var between = html[last..m.Index];
            last = m.Index + m.Length;
            if (skip == 0)
            {
                if (pre > 0) preText.Append(between);
                else if (cell is not null) cell.Append(between);
                else text.Append(between);
            }
            var close = m.Groups[1].Value == "/";
            var tag = m.Groups[2].Value.ToLowerInvariant();
            var attrs = m.Groups[3].Value;

            // 看不见的块（display:none、aria-hidden）整个跳过
            if (skip > 0)
            {
                if (tag == skipTag) skip += close ? -1 : (attrs.TrimEnd().EndsWith('/') ? 0 : 1);
                continue;
            }
            if (!close && Regex.IsMatch(attrs, @"display\s*:\s*none|aria-hidden\s*=\s*[""']?true|(?:^|\s)hidden(?:\s|$|=)", RegexOptions.IgnoreCase)
                && tag is not ("br" or "img" or "hr" or "input" or "meta" or "link" or "source" or "wbr" or "col" or "area") && !attrs.TrimEnd().EndsWith('/'))
            {
                skip = 1; skipTag = tag; continue;
            }

            if (tag == "pre")
            {
                if (!close) { Flush(); pre++; }
                else if (pre > 0 && --pre == 0)
                {
                    var code = WebUtility.HtmlDecode(Regex.Replace(preText.ToString(), "<[^>]+>", "")).Trim('\n', '\r');
                    preText.Clear();
                    if (code.Trim().Length > 0) list.Add(new Block("code", code.Length > 8000 ? code[..8000] : code));
                }
                continue;
            }
            if (pre > 0) { if (tag == "br") preText.Append('\n'); continue; }

            if (tag == "table")
            {
                if (!close)
                {
                    if (tableDepth++ == 0) { Flush(); table = []; }
                }
                else if (tableDepth > 0 && --tableDepth == 0 && table is not null)
                {
                    if (row is { Count: > 0 }) table.Add(row);
                    row = null; cell = null;
                    var rows = table.Where(r => r.Any(c => c.Length > 0)).Take(200).ToList();
                    if (rows.Count == 1 && rows[0].Count == 1) list.Add(new Block("p", rows[0][0]));
                    else if (rows.Count > 0)
                    {
                        // 一行一格的排版表格当段落
                        if (rows.All(r => r.Count == 1)) list.AddRange(rows.Select(r => new Block("p", r[0])));
                        else list.Add(new Block("table", Rows: rows));
                    }
                    table = null;
                }
                continue;
            }
            if (table is not null && tableDepth == 1)
            {
                if (tag == "tr")
                {
                    if (row is { Count: > 0 }) table.Add(row);
                    row = close ? null : [];
                    continue;
                }
                if (tag is "td" or "th")
                {
                    if (!close) { row ??= []; cell = new StringBuilder(); }
                    else if (cell is not null)
                    {
                        var v = Clean(WebUtility.HtmlDecode(cell.ToString()));
                        (row ??= []).Add(v.Length > 300 ? v[..300] : v);
                        cell = null;
                    }
                    continue;
                }
                if (tag == "br" && cell is not null) { cell.Append(' '); continue; }
                if (tag == "img" && !close && cell is not null) continue;
                continue;
            }
            if (table is not null) continue; // 嵌套表格里的标签不再细分

            if (tag == "img" && !close)
            {
                var a = Attrs(attrs);
                var src = new[] { "data-src", "data-original", "data-actualsrc", "data-lazy-src", "src" }
                    .Select(k => a.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && !v.StartsWith("data:", StringComparison.OrdinalIgnoreCase));
                if (src is null) continue;
                if (!Uri.TryCreate(baseUri, WebUtility.HtmlDecode(src.Trim()), out var abs) || abs.Scheme is not ("http" or "https")) continue;
                // 1×1 的跟踪像素、图标不要
                if (int.TryParse(a.GetValueOrDefault("width"), out var w) && w < 48) continue;
                if (int.TryParse(a.GetValueOrDefault("height"), out var h) && h < 48) continue;
                Flush();
                list.Add(new Block("image", Clean(WebUtility.HtmlDecode(a.GetValueOrDefault("alt") ?? "")), Src: abs.ToString()));
                continue;
            }
            if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            {
                Flush();
                if (!close) { kind = "heading"; level = tag[1] - '0'; }
                continue;
            }
            if (tag == "li") { Flush(); if (!close) kind = "li"; continue; }
            if (tag == "blockquote") { Flush(); if (!close) kind = "quote"; continue; }
            if (BlockTags.Contains(tag)) { if (kind is "li" or "quote" && tag is "p" or "div" or "br") { text.Append(' '); continue; } Flush(); continue; }
        }
        if (skip == 0) text.Append(html[last..]);
        Flush();
        // 连续重复的块（轮播、移动端/桌面端两份）只留一个
        var outList = new List<Block>();
        foreach (var b in list)
        {
            if (outList.Count > 0 && outList[^1].Type == b.Type && outList[^1].Text == b.Text && b.Type != "table") continue;
            outList.Add(b);
            if (outList.Count >= 800) break;
        }
        return outList;
    }

    private static string Clean(string s) => Space.Replace(s, " ").Trim();

    /// <summary>阅读模式的纯文字（存进资料库用）：标题、段落、列表、表格按 Markdown 写出来。</summary>
    public static string ToMarkdown(Article a, string url)
    {
        var sb = new StringBuilder($"# {a.Title}\n\n来源：{url}\n\n");
        foreach (var b in a.Blocks)
        {
            switch (b.Type)
            {
                case "heading": sb.Append('#', Math.Min(6, (b.Level ?? 2) + 1)).Append(' ').AppendLine(b.Text).AppendLine(); break;
                case "li": sb.Append("- ").AppendLine(b.Text); break;
                case "quote": sb.Append("> ").AppendLine(b.Text).AppendLine(); break;
                case "code": sb.AppendLine("```").AppendLine(b.Text).AppendLine("```").AppendLine(); break;
                case "image": if (!string.IsNullOrEmpty(b.Text)) sb.AppendLine($"（图：{b.Text}）").AppendLine(); break;
                case "table" when b.Rows is { Count: > 0 }:
                    var width = b.Rows.Max(r => r.Count);
                    string Line(List<string> r) => "| " + string.Join(" | ", Enumerable.Range(0, width).Select(i => i < r.Count ? r[i].Replace("|", "/") : "")) + " |";
                    sb.AppendLine(Line(b.Rows[0]));
                    sb.AppendLine("| " + string.Join(" | ", Enumerable.Repeat("---", width)) + " |");
                    foreach (var r in b.Rows.Skip(1)) sb.AppendLine(Line(r));
                    sb.AppendLine();
                    break;
                default: sb.AppendLine(b.Text).AppendLine(); break;
            }
        }
        return sb.ToString();
    }
}
