using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>网页 → 正文：去掉脚本、样式、导航这些，留下标题和可读文字。不求完美，够模型读就行。</summary>
public static class WebText
{
    private static readonly Regex Drop = new(@"<(script|style|noscript|svg|nav|footer|header|form|iframe)[^>]*>[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Comment = new(@"<!--[\s\S]*?-->", RegexOptions.Compiled);
    private static readonly Regex Block = new(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr|/section|/article|/table|p|li|h[1-6]|tr)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Cell = new(@"<\s*/\s*(td|th)\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CellOpen = new(@"<\s*(td|th)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Title = new(@"<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string Title, string Text) Extract(string html)
    {
        var title = Title.Match(html) is { Success: true } m ? Clean(WebUtility.HtmlDecode(Tag.Replace(m.Groups[1].Value, ""))) : "";
        var s = Comment.Replace(html, " ");
        s = Drop.Replace(s, " ");
        s = CellOpen.Replace(s, " ");
        s = Cell.Replace(s, " | ");
        s = Block.Replace(s, "\n");
        s = Tag.Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        var lines = s.Split('\n').Select(Clean).Where(l => l.Length > 1);
        return (title, string.Join("\n", lines));
    }

    private static string Clean(string s) => Regex.Replace(s, @"[ \t\r\f\v 　]+", " ").Trim().Trim('|').Trim();

    /// <summary>正文切成段（按空行/换行聚合到约 size 字），给排序挑相关段用。</summary>
    public static List<string> Pieces(string text, int size = 900)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (sb.Length > 0 && sb.Length + line.Length > size) { list.Add(sb.ToString()); sb.Clear(); }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line.Length > size ? line[..size] : line);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }
}

/// <summary>出站请求的地址检查：只许访问公网。挡住本机、内网、链路本地、组播等——
/// 否则「打开网页」就成了从服务器探内网（数据库、模型服务、云元数据接口）的跳板。</summary>
public static class NetGuard
{
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] == 127
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // 运营商 NAT
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))  // 测试网段
                || b[0] >= 224);                                // 组播与保留
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any)) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false; // fc00::/7 唯一本地
            return true;
        }
        return false;
    }

    /// <summary>网址本身合不合规矩：只认 http/https，不带账号密码，端口只许 80/443。</summary>
    public static bool IsAllowedUrl(Uri u)
        => (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
           && string.IsNullOrEmpty(u.UserInfo)
           && (u.IsDefaultPort || u.Port is 80 or 443);
}

/// <summary>发到外网的搜索词：把客户名、项目编号、金额换成泛称。宁可搜得粗一点，也不把内部信息送出去。</summary>
public static class Redactor
{
    private static readonly Regex Money = new(@"\d+(?:\.\d+)?\s*(?:万元|亿元|元|万|RMB|CNY|USD|美元)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string Text, bool Changed) Redact(string text, IEnumerable<string> customerNames, IEnumerable<string> projectNos, string? projectNoPattern)
    {
        var s = text;
        foreach (var n in customerNames.Where(n => n.Length >= 2).OrderByDescending(n => n.Length))
            s = s.Replace(n, "某客户", StringComparison.OrdinalIgnoreCase);
        foreach (var p in projectNos.Where(p => p.Length >= 3).OrderByDescending(p => p.Length))
            s = s.Replace(p, "某项目", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(projectNoPattern))
            try { s = Regex.Replace(s, projectNoPattern, "某项目"); } catch (ArgumentException) { /* 配置的正则写错了就不用它 */ }
        s = Money.Replace(s, "某金额");
        return (s, s != text);
    }
}

/// <summary>文件对比：把 A（如需求书）切成一条条，每条在 B（如我们的方案）里找最像的那段，配成对交给模型判断。
/// 只做「找对应」，满足不满足由模型看原文判断——不写任何业务规则。</summary>
public static class DocCompare
{
    public record Item(int Index, string Text, string? Where);
    public record Pair(Item A, Item? B, double Score);

    /// <summary>按条款编号、换行、句号切条；太短的并到前一条。</summary>
    public static List<Item> SplitClauses(IEnumerable<(string Text, string? Where)> blocks, int maxLen = 220)
    {
        var items = new List<Item>();
        foreach (var (text, where) in blocks)
        {
            // 按换行、句末标点切；段内的「（1）（2）」也切开。不按句中数字切——「不低于 2.5 MPa」里的 2.5 不是条款号
            foreach (var raw in Regex.Split(text, @"\n+|(?<=[。；;])\s*|(?=[（(]\d{1,2}[)）])"))
            {
                var t = raw.Trim();
                if (t.Length == 0) continue;
                if (t.Length < 8 && items.Count > 0) { var last = items[^1]; items[^1] = last with { Text = last.Text + t }; continue; }
                items.Add(new Item(items.Count + 1, t.Length > maxLen ? t[..maxLen] + "…" : t, where));
            }
        }
        return items;
    }

    public static List<Pair> Match(IReadOnlyList<Item> a, IReadOnlyList<Item> b)
    {
        var bGrams = b.Select(x => Grams(x.Text)).ToList();
        return a.Select(x =>
        {
            var g = Grams(x.Text);
            var best = -1; var score = 0.0;
            for (var i = 0; i < b.Count; i++)
            {
                if (g.Count == 0 || bGrams[i].Count == 0) continue;
                var inter = g.Count(bGrams[i].Contains);
                var sc = inter / Math.Sqrt(g.Count * (double)bGrams[i].Count);
                if (sc > score) { score = sc; best = i; }
            }
            return new Pair(x, best >= 0 && score >= 0.08 ? b[best] : null, Math.Round(score, 3));
        }).ToList();
    }

    private static HashSet<string> Grams(string s)
    {
        var t = Regex.Replace(s.ToLowerInvariant(), @"\s+", "");
        var set = new HashSet<string>();
        for (var i = 0; i + 1 < t.Length; i++)
            if (!char.IsPunctuation(t[i]) && !char.IsPunctuation(t[i + 1])) set.Add(t.Substring(i, 2));
        return set;
    }
}
