namespace HT.Agent.Application.Logic;

/// <summary>分块的「身份」（上下文增强检索）：这块属于哪份文件、哪个客户、哪个项目、哪个设备、哪一章，
/// 外加一句「这段讲的是什么」。只拼在检索分词和向量用的文字前面，界面上显示的原文不变。
/// 例：问「本松那台釜的设计压力」时，正文里只写着「R-101 反应釜 设计压力 5MPaG」的那一块也能被查到。</summary>
public static class ChunkContext
{
    public static string Header(string title, string? customer, string? projectNo, string? deviceType, string? category, string? sectionPath, string? summary)
    {
        var parts = new List<string> { $"《{title}》" };
        if (!string.IsNullOrWhiteSpace(customer) && !title.Contains(customer)) parts.Add($"客户：{customer}");
        if (!string.IsNullOrWhiteSpace(projectNo)) parts.Add($"项目：{projectNo}");
        if (!string.IsNullOrWhiteSpace(deviceType)) parts.Add($"设备：{deviceType}");
        if (!string.IsNullOrWhiteSpace(category)) parts.Add($"类别：{category}");
        if (!string.IsNullOrWhiteSpace(sectionPath)) parts.Add($"章节：{sectionPath}");
        var head = string.Join("｜", parts);
        return string.IsNullOrWhiteSpace(summary) ? head : $"{head}\n{summary.Trim()}";
    }

    /// <summary>检索和向量用的文字 = 身份 + 原文。</summary>
    public static string ForIndex(string? context, string text)
        => string.IsNullOrWhiteSpace(context) ? text : context + "\n" + text;

    /// <summary>模型写的一句话说明：去掉引号、多余的行，限长。</summary>
    public static string? CleanSummary(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var line = raw.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("<")) ?? "";
        line = line.Trim('"', '“', '”', '「', '」', ' ');
        if (line.StartsWith("这段") is false && line.Length < 4) return null;
        return line.Length > 80 ? line[..80] : line;
    }
}
