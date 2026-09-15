namespace HT.Agent.Application.Logic;

/// <summary>分段分批（FR-6.2）：按段落切开，凑批不超过单批长度上限；
/// 单段超长时按句边界硬切。批内段落用哨兵行分隔，模型按行对应输出，双语对照按段配对。</summary>
public static class TextSegmenter
{
    /// <summary>段落分隔哨兵：进提示词，要求译文按同样的哨兵行分隔返回。</summary>
    public const string Sentinel = "<<<SEG>>>";

    public static IReadOnlyList<string> SplitParagraphs(string text)
        => text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .ToList();

    /// <summary>把段落装批：每批总长不超 maxChars；超长单段按句号/换行硬切。</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Batch(IReadOnlyList<string> paragraphs, int maxChars)
    {
        maxChars = Math.Max(200, maxChars);
        var batches = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        var size = 0;
        foreach (var para in paragraphs.SelectMany(p => HardSplit(p, maxChars)))
        {
            if (size > 0 && size + para.Length > maxChars)
            {
                batches.Add(current);
                current = [];
                size = 0;
            }
            current.Add(para);
            size += para.Length;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    private static IEnumerable<string> HardSplit(string para, int maxChars)
    {
        if (para.Length <= maxChars) { yield return para; yield break; }
        var start = 0;
        while (start < para.Length)
        {
            var len = Math.Min(maxChars, para.Length - start);
            if (start + len < para.Length)
            {
                // 回退到最近的句边界，避免拦腰截断
                var window = para.AsSpan(start, len);
                var cut = window.LastIndexOfAny("。！？.;；".AsSpan());
                if (cut > maxChars / 2) len = cut + 1;
            }
            yield return para.Substring(start, len).Trim();
            start += len;
        }
    }
}
