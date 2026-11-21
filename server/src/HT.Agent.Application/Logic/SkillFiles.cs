using System.Text;
using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>技能文件（开放标准 Agent Skills 的 SKILL.md）：读、写、导入前的检查。
/// SKILL.md = 开头 --- 包着的几行元数据（name、description、license……）+ 下面的 Markdown 正文。</summary>
public static class SkillFiles
{
    public record Parsed(string Name, string Description, string? License, string Body, Dictionary<string, string> Meta);

    /// <summary>助手现有的工具名。技能正文里提到别的「工具名」（比如开源技能里的 bash、web_fetch）会被标出来。</summary>
    public static readonly string[] KnownTools =
    [
        "use_skill", "ask_user", "remember", "forget", "inquiry_form", "search_standards", "search_literature", "view_image", "ocr_image",
        "translate_file", "search_prices", "cost_estimate", "show_table", "note_correction", "show_block", "run_python", "chemical_info",
        "web_search", "fetch_url", "compare_documents", "search_documents", "read_document", "query_projects", "list_catalog",
        "read_attachments", "calculate", "convert_unit",
    ];

    /// <summary>回答组件里带下划线的名字（show_block 的 kind），正文里提到它们不算未知工具。</summary>
    public static readonly string[] KnownWords = ["fault_tree", "risk_matrix", "signal_to", "drawing_no"];

    /// <summary>允许导入的许可证（可商用、可修改）。没声明的不让导入。</summary>
    public static readonly string[] OpenLicenses = ["apache-2.0", "apache 2.0", "mit", "bsd-2-clause", "bsd-3-clause", "isc", "cc-by-4.0", "cc0-1.0", "mpl-2.0", "unlicense"];

    /// <summary>正文上限（字）：本地模型一次看得完。</summary>
    public const int MaxBody = 3000;

    public static Parsed Parse(string text)
    {
        var t = (text ?? "").Replace("\r\n", "\n").TrimStart('﻿');
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = t;
        var m = Regex.Match(t, @"^---\s*\n(.*?)\n---\s*(\n|$)", RegexOptions.Singleline);
        if (m.Success)
        {
            body = t[m.Length..];
            string? key = null;
            var block = new StringBuilder();
            void Flush() { if (key is not null) meta[key] = block.ToString().Trim().Trim('"', '\''); }
            foreach (var line in m.Groups[1].Value.Split('\n'))
            {
                var kv = Regex.Match(line, @"^([A-Za-z_][\w-]*)\s*:\s*(.*)$");
                if (kv.Success && !line.StartsWith(' ') && !line.StartsWith('\t'))
                {
                    Flush();
                    key = kv.Groups[1].Value;
                    var v = kv.Groups[2].Value.Trim();
                    block.Clear();
                    if (v is not ("|" or ">" or "|-" or ">-")) block.Append(v);
                }
                else if (key is not null) block.Append(block.Length > 0 ? " " : "").Append(line.Trim());
            }
            Flush();
        }
        meta.TryGetValue("name", out var name);
        meta.TryGetValue("description", out var desc);
        meta.TryGetValue("license", out var lic);
        return new Parsed(name ?? "", desc ?? "", string.IsNullOrWhiteSpace(lic) ? null : lic, body.Trim(), meta);
    }

    public static string Write(string code, string description, string body, string? license = null)
    {
        var sb = new StringBuilder("---\n");
        sb.Append("name: ").Append(code).Append('\n');
        sb.Append("description: ").Append(description.Replace("\n", " ").Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(license)) sb.Append("license: ").Append(license).Append('\n');
        sb.Append("---\n\n").Append(body.Trim()).Append('\n');
        return sb.ToString();
    }

    public static bool LicenseOk(string? license)
    {
        if (string.IsNullOrWhiteSpace(license)) return false;
        var l = license.Trim().ToLowerInvariant();
        return OpenLicenses.Any(x => l.Contains(x));
    }

    /// <summary>正文里像工具名（小写加下划线、或 bash / python 这类）但我们没有的词。</summary>
    public static List<string> UnknownTools(string body)
    {
        body = Regex.Replace(body ?? "", @"```.*?```", "", RegexOptions.Singleline);   // 代码块里的函数名、变量名不算
        var found = Regex.Matches(body, @"(?<![\w./-])([a-z]+(?:_[a-z]+)+)(?![\w./-])").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(body, @"`(bash|python|web_fetch|websearch|read|write|edit|grep|glob)`").Select(m => m.Groups[1].Value))
            .Distinct().Where(w => !KnownTools.Contains(w) && !KnownWords.Contains(w)).ToList();
        return found;
    }

    /// <summary>代号：小写字母、数字、连字符（Agent Skills 的 name 规则），最长 64。</summary>
    public static string NormalizeCode(string s)
    {
        var c = Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9-]+", "-").Trim('-');
        c = Regex.Replace(c, "-{2,}", "-");
        return c.Length > 64 ? c[..64].Trim('-') : c;
    }

    /// <summary>字数（去掉空白）。</summary>
    public static int Length(string body) => Regex.Replace(body ?? "", @"\s+", "").Length;
}
