using System.Text;
using System.Text.Json.Nodes;

namespace HT.Agent.Application.Logic;

/// <summary>「做 PPT」工具的规矩：模型交来的每页内容先查一遍（页面类型、数量、字数），挑模板，
/// 把沙箱里套模板的结果（缩了字号、放不下、模板里没有这种页）翻成给模型的话。</summary>
public static class PptPages
{
    public static readonly string[] Types = ["cover", "section", "toc", "list", "items", "num", "table", "img", "end"];
    public const int MaxPages = 30;

    public static readonly IReadOnlyDictionary<string, string> TypeLabels = new Dictionary<string, string>
    {
        ["cover"] = "封面", ["section"] = "过渡页", ["toc"] = "目录", ["list"] = "要点", ["items"] = "几栏",
        ["num"] = "大数字", ["table"] = "表格", ["img"] = "图文", ["end"] = "结尾",
    };

    static string Clip(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : (s ?? "").Trim();

    /// <summary>查每页内容；有问题返回要模型改的话（列出来），没问题给出整理好的一份（多余字段去掉、太长的截断）。</summary>
    public static (JsonArray? Pages, List<string> Errors) Validate(JsonNode? input)
    {
        var errors = new List<string>();
        if (input is not JsonArray arr || arr.Count == 0)
            return (null, ["pages 要是一个数组，每页一项（至少一页）。"]);
        if (arr.Count > MaxPages) errors.Add($"一份最多 {MaxPages} 页，现在 {arr.Count} 页：合并或删减。");
        var outp = new JsonArray();
        var n = 0;
        foreach (var node in arr)
        {
            n++;
            if (node is not JsonObject o) { errors.Add($"第 {n} 页不是对象。"); continue; }
            var type = o["type"]?.ToString()?.Trim().ToLowerInvariant() ?? "";
            if (type is "two" or "cols" or "columns") type = "items";
            if (type is "agenda") type = "toc";
            if (type is "numbers" or "kpi") type = "num";
            if (!Types.Contains(type)) { errors.Add($"第 {n} 页 type「{type}」不认识，只能是 {string.Join(" / ", Types)}。"); continue; }
            var p = new JsonObject { ["type"] = type };
            var title = Clip(o["title"]?.ToString(), 40);
            if (title.Length == 0 && type is not ("end")) errors.Add($"第 {n} 页（{TypeLabels[type]}）没写 title。");
            p["title"] = title;
            if (o["subtitle"]?.ToString() is { Length: > 0 } sub) p["subtitle"] = Clip(sub, 80);
            switch (type)
            {
                case "list" or "img":
                {
                    var bullets = (o["bullets"] as JsonArray)?.Select(x => Clip(x?.ToString(), 80)).Where(x => x.Length > 0).ToList() ?? [];
                    if (bullets.Count == 0 && o["text"]?.ToString() is { Length: > 0 } txt) bullets.Add(Clip(txt, 200));
                    if (bullets.Count == 0 && type == "list") errors.Add($"第 {n} 页（要点）没有 bullets。");
                    if (bullets.Count > 7) errors.Add($"第 {n} 页要点 {bullets.Count} 条太多：一页最多 7 条，拆成两页。");
                    p["bullets"] = new JsonArray(bullets.Take(7).Select(x => (JsonNode)x).ToArray());
                    if (type == "img" && o["image"]?.ToString() is { Length: > 0 } img) p["image"] = Clip(img, 200);
                    break;
                }
                case "items" or "toc":
                {
                    var items = new JsonArray();
                    foreach (var it in (o["items"] as JsonArray) ?? [])
                    {
                        if (it is JsonObject io)
                            items.Add(new JsonObject { ["title"] = Clip(io["title"]?.ToString(), 24), ["text"] = Clip(io["text"]?.ToString(), 90) });
                        else if (it?.ToString() is { Length: > 0 } s)
                            items.Add(new JsonObject { ["title"] = Clip(s, 24), ["text"] = "" });
                    }
                    var max = type == "toc" ? 8 : 6;
                    if (items.Count == 0) errors.Add($"第 {n} 页（{TypeLabels[type]}）没有 items。");
                    else if (items.Count > max) errors.Add($"第 {n} 页 {items.Count} 栏太多：最多 {max} 栏，拆页。");
                    p["items"] = items;
                    break;
                }
                case "num":
                {
                    var nums = new JsonArray();
                    foreach (var it in (o["numbers"] as JsonArray) ?? [])
                        if (it is JsonObject io && io["value"]?.ToString() is { Length: > 0 } v)
                            nums.Add(new JsonObject { ["value"] = Clip(v, 14), ["label"] = Clip(io["label"]?.ToString(), 30) });
                    if (nums.Count == 0) errors.Add($"第 {n} 页（大数字）没有 numbers（每个写 value 和 label）。");
                    else if (nums.Count > 6) errors.Add($"第 {n} 页数字 {nums.Count} 个太多：最多 6 个。");
                    p["numbers"] = nums;
                    break;
                }
                case "table":
                {
                    var rows = new JsonArray();
                    foreach (var r in (o["rows"] as JsonArray) ?? [])
                        if (r is JsonArray ra && ra.Count > 0)
                            rows.Add(new JsonArray(ra.Take(6).Select(c => (JsonNode)Clip(c?.ToString(), 40)).ToArray()));
                    if (rows.Count < 2) errors.Add($"第 {n} 页（表格）rows 至少两行（第一行是表头）。");
                    else if (rows.Count > 12) errors.Add($"第 {n} 页表格 {rows.Count} 行太多：最多 12 行，拆页。");
                    p["rows"] = rows;
                    break;
                }
            }
            outp.Add(p);
        }
        return errors.Count > 0 ? (null, errors) : (outp, errors);
    }

    /// <summary>挑模板：模型点名的（编号或名字）→ 用户在输入框选的 → 没有就用第一套带「商务」标签的。</summary>
    public static T? Pick<T>(IReadOnlyList<T> all, Func<T, (Guid Id, string Name, string Tags)> key, string? wanted, Guid? chosen)
    {
        if (all.Count == 0) return default;
        // 元素可能是值类型（元组），不能用 FirstOrDefault 判断找没找到
        int Find(Func<(Guid Id, string Name, string Tags), bool> f)
        {
            for (var i = 0; i < all.Count; i++) if (f(key(all[i]))) return i;
            return -1;
        }
        var w = (wanted ?? "").Trim().Trim('「', '」', '"');
        var hit = -1;
        if (w.Length > 0)
        {
            if (Guid.TryParse(w, out var id)) hit = Find(t => t.Id == id);
            if (hit < 0) hit = Find(t => t.Name == w);
            if (hit < 0) hit = Find(t => t.Name.Contains(w) || w.Contains(t.Name));
            if (hit < 0) hit = Find(t => t.Tags.Split(',').Contains(w));
        }
        if (hit < 0 && chosen is { } c) hit = Find(t => t.Id == c);
        if (hit < 0) hit = Find(t => t.Tags.Split(',').Contains("商务"));
        return all[hit < 0 ? 0 : hit];
    }

    /// <summary>上传模板时的来源网址是不是不许入库的网站。</summary>
    public static string? Blocked(string? source, string blockedList)
    {
        var s = (source ?? "").ToLowerInvariant();
        if (s.Length == 0) return null;
        return blockedList.Split([',', '，', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(d => s.Contains(d.ToLowerInvariant()));
    }

    public const string DefaultBlocked = "slidesgo.com,slidesmania.com,officeplus.cn,create.microsoft.com,templates.office.com,freepik.com,canva.com";

    /// <summary>许可里能放进公司模板库的几种（上传时下拉选）。</summary>
    public static readonly string[] Licenses = ["CC BY 4.0", "CC BY-SA 4.0", "CC0", "MIT", "Apache-2.0", "公司自有"];

    public static string SafeFileName(string? name, string fallback)
    {
        var n = (name ?? "").Trim();
        if (n.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)) n = n[..^5];
        var sb = new StringBuilder();
        foreach (var ch in n)
            if (!char.IsControl(ch) && "\\/:*?\"<>|'`$".IndexOf(ch) < 0) sb.Append(ch);
        var s = sb.ToString().Trim().Trim('.');
        if (s.Length == 0) s = fallback;
        return (s.Length > 60 ? s[..60] : s) + ".pptx";
    }

    /// <summary>沙箱套模板的报告 → 给模型的话：哪些页换了页面类型、缩了字号、放不下要拆页。</summary>
    public static (string ForModel, List<string> Checks) Report(JsonNode? report, string templateName, bool credit)
    {
        var sb = new StringBuilder();
        var checks = new List<string>();
        var slides = report?["slides"]?.GetValue<int>() ?? 0;
        var fallback = (report?["fallback"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? [];
        var shrunk = (report?["shrunk"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? [];
        var overflow = (report?["overflow"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? [];
        var cleared = report?["cleared"]?.GetValue<int>() ?? 0;
        var photos = report?["photos"]?.GetValue<bool>() ?? false;
        checks.Add($"{slides} 页");
        checks.Add(overflow.Count == 0 ? "没有字超出框" : $"{overflow.Count} 处放不下");
        if (cleared > 0) checks.Add("模板原文都换掉了");
        if (shrunk.Count > 0) checks.Add($"{shrunk.Count} 处缩了字号");
        if (overflow.Count > 0)
        {
            sb.AppendLine("有放不下的地方（用户看得到）：");
            foreach (var o in overflow.Take(8)) sb.AppendLine("- " + o);
            sb.AppendLine("把这些页拆开或删减后再调一次 make_ppt（同一个 file_name 会覆盖成新的一份）；实在放不下就在回答里说明。");
        }
        if (fallback.Count > 0)
        {
            sb.AppendLine($"模板「{templateName}」里有些页面类型没有，换成了相近的：");
            foreach (var f in fallback.Take(6)) sb.AppendLine("- " + f);
        }
        if (shrunk.Count > 0) sb.AppendLine($"有 {shrunk.Count} 处字偏多，已自动缩小字号。");
        if (credit) sb.AppendLine("模板许可要求署名：最后一页右下角已加一行模板署名，不要删。" + (photos ? "用了模板自带的照片，已附上模板的致谢页（照片有各自的许可）。" : ""));
        return (sb.ToString(), checks);
    }
}
