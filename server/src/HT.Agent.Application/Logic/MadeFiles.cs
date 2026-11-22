using System.IO.Compression;
using System.Text.Json;

namespace HT.Agent.Application.Logic;

/// <summary>助手做的文件：哪种文件、基本检查（文件本身没坏）、卡片上的说明、检查没过时告诉模型什么。
/// 深入检查（Excel 公式重算、转一遍 PDF）在 office 容器里做，这里只放不依赖外部服务的部分。</summary>
public static class MadeFiles
{
    /// <summary>四种文件，后台各自开关。csv 算在 Excel 里。</summary>
    public static readonly string[] Kinds = ["xlsx", "docx", "pptx", "pdf"];

    /// <summary>CAD 文件（外形图 DXF、三维模型 STEP）：能运行代码就能做，不另设开关，也不过 office 检查。</summary>
    public static readonly string[] CadKinds = ["dxf", "step"];
    public static bool IsCad(string kind) => CadKinds.Contains(kind);

    /// <summary>重做最多两次：一个回答里同一种文件最多做 3 遍。</summary>
    public const int MaxAttempts = 3;

    public static string Label(string kind) => kind switch
    {
        "xlsx" => "Excel", "docx" => "Word", "pptx" => "PPT", "pdf" => "PDF", "csv" => "CSV", "dxf" => "DXF 图纸", "step" => "STEP 三维模型", _ => kind
    };

    /// <summary>文件名 → 种类（xlsx/docx/pptx/pdf/csv）；不认的返回 null。</summary>
    public static string? KindOf(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        return ext is "xlsx" or "docx" or "pptx" or "pdf" or "csv" or "dxf" or "step" ? ext : ext == "stp" ? "step" : null;
    }

    /// <summary>做文件的技能对应哪种文件：这种文件没开时，技能也不列给模型。</summary>
    public static string? SkillKind(string skillCode) => skillCode switch
    {
        "excel-files" => "xlsx", "word-files" => "docx", "ppt-files" => "pptx", "pdf-files" => "pdf", _ => null
    };

    /// <summary>开关和角色按哪一种算：csv 跟着 Excel。</summary>
    public static string SwitchOf(string kind) => kind == "csv" ? "xlsx" : kind;

    /// <summary>不依赖外部服务的检查：是不是这种文件、包里该有的部分在不在。</summary>
    public static List<string> BasicProblems(byte[] data, string kind)
    {
        var problems = new List<string>();
        if (data.Length == 0) { problems.Add("文件是空的"); return problems; }
        if (kind == "csv") return problems;
        if (kind is "dxf" or "step")
        {
            var head = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 4096));
            var tail = System.Text.Encoding.ASCII.GetString(data, Math.Max(0, data.Length - 256), Math.Min(data.Length, 256));
            if (kind == "dxf" && !(head.Contains("SECTION") && tail.Contains("EOF"))) problems.Add("不是完整的 DXF 文件（缺 SECTION 或 EOF）");
            if (kind == "step" && !head.TrimStart().StartsWith("ISO-10303-21")) problems.Add("不是 STEP 文件（开头不是 ISO-10303-21）");
            return problems;
        }
        if (kind == "pdf")
        {
            if (data.Length < 5 || data[0] != '%' || data[1] != 'P' || data[2] != 'D' || data[3] != 'F') problems.Add("不是 PDF 文件");
            return problems;
        }
        var must = kind switch { "xlsx" => "xl/workbook.xml", "docx" => "word/document.xml", _ => "ppt/presentation.xml" };
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            if (zip.GetEntry(must) is null) problems.Add($"不是 {Label(kind)} 文件（缺 {must}）");
        }
        catch (InvalidDataException) { problems.Add($"不是合法的 {Label(kind)} 文件（打不开）"); }
        return problems;
    }

    /// <summary>卡片上的一行说明，比如「3 个工作表 · 42 个公式 · 已检查：能打开，公式没有错误」。</summary>
    public static string Detail(string kind, JsonElement? info, bool fullCheck)
    {
        int? I(string k) => info is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : null;
        var parts = new List<string>();
        switch (kind)
        {
            case "xlsx":
                if (I("sheets") is { } s) parts.Add($"{s} 个工作表");
                if (I("formulas") is { } f) parts.Add($"{f} 个公式");
                break;
            case "docx":
                if (I("revisions") is { } r && r > 0) parts.Add($"修订 {r} 处");
                if (I("comments") is { } c && c > 0) parts.Add($"批注 {c} 条");
                if (I("pages") is { } p) parts.Add($"{p} 页");
                break;
            case "pptx":
                if (I("slides") is { } sl) parts.Add($"{sl} 页");
                break;
            case "pdf":
                if (I("pages") is { } pg) parts.Add($"{pg} 页");
                break;
            case "dxf":
                return "AutoCAD、中望、浩辰等 CAD 软件能打开";
            case "step":
                return "SolidWorks、FreeCAD 等三维软件能打开";
        }
        parts.Add(!fullCheck ? "检查服务没连上，只确认了文件能打开"
            : kind == "xlsx" && I("formulas") is > 0 ? "已检查：能打开，公式没有错误" : "已检查：能打开");
        return string.Join(" · ", parts);
    }

    /// <summary>检查没过时交给模型的话：错在哪、还能重做几次；次数用完就别再做文件了。</summary>
    public static string ForModelAfterFail(string fileName, IReadOnlyList<string> problems, int attempts)
    {
        var what = $"《{fileName}》检查没过，没有交给用户：\n- " + string.Join("\n- ", problems);
        return attempts < MaxAttempts
            ? what + $"\n改正后重新运行代码生成（还能再试 {MaxAttempts - attempts} 次）。"
            : what + "\n已经试了 3 次都没过，不要再做这个文件了：在回答里用表格或文字给出内容，并告诉用户文件没做成、原因是什么。";
    }
}
