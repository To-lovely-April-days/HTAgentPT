using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>回答导出成 Word / PDF 时只留正文：去掉结尾问用户的话（「如果还有问题告诉我」「要我再……吗」）、
/// 指向界面的说明（「（上方可下载）」），文档标题用回答自己的第一个标题，不用提问那句话。</summary>
public static partial class ExportText
{
    public static string Clean(string markdown)
    {
        var text = UiRef().Replace((markdown ?? "").Replace("\r\n", "\n"), "");
        var paras = Regex.Split(text.Trim(), @"\n\s*\n").Select(p => p.TrimEnd()).Where(p => p.Trim().Length > 0).ToList();
        // 从结尾往前去掉问用户的话、分隔线；正文至少留一段
        while (paras.Count > 1 && (IsFollowUp(paras[^1]) || Regex.IsMatch(paras[^1].Trim(), @"^(-{3,}|\*{3,}|_{3,})$")))
            paras.RemoveAt(paras.Count - 1);
        return string.Join("\n\n", paras);
    }

    /// <summary>是不是对用户说的收尾话：不长、不是表格或标题，带「告诉我」「要不要」「如果你还……」这类说法。</summary>
    public static bool IsFollowUp(string paragraph)
    {
        var p = paragraph.Trim();
        if (p.Length == 0 || p.Length > 200 || p.Contains('|') || p.StartsWith('#')) return false;
        return FollowUp().IsMatch(p);
    }

    /// <summary>文档标题：回答开头就是标题（# …）的用它；没有就不另加标题。</summary>
    public static string? Heading(string markdown)
    {
        var first = (markdown ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        var m = first is null ? null : Regex.Match(first, @"^#{1,3}\s+(.+)$");
        return m is { Success: true } ? m.Groups[1].Value.Trim().TrimEnd('#').Trim() : null;
    }

    [GeneratedRegex(@"告诉我|跟我说|直接说|需要我|要不要|要我|我可以|我再|我来帮|如果(?:你|您)?(?:还|需要|想|要|有|觉得)|如需|如有|随时|欢迎|还有什么|有问题")]
    private static partial Regex FollowUp();

    [GeneratedRegex(@"[（(][^（）()\n]{0,14}(?:上方|下方|上面|下面|卡片|点击|右侧|左侧)[^（）()\n]{0,14}[）)]")]
    private static partial Regex UiRef();
}
