namespace HT.Agent.Application.Logic;

/// <summary>分块拆分的纯文本部分（FR-1.5 手动拆分）。偏移基于字符，校验后按位切开。</summary>
public static class ChunkSplitter
{
    public static IReadOnlyList<string> Split(string text, IReadOnlyList<int> offsets)
    {
        if (offsets.Count == 0) throw new ArgumentException("至少一个拆分点");
        var sorted = offsets.Distinct().OrderBy(x => x).ToList();
        if (sorted[0] <= 0 || sorted[^1] >= text.Length)
            throw new ArgumentException($"拆分点须在 (0, {text.Length}) 之内");
        var parts = new List<string>();
        var prev = 0;
        foreach (var off in sorted)
        {
            parts.Add(text[prev..off].Trim());
            prev = off;
        }
        parts.Add(text[prev..].Trim());
        if (parts.Any(p => p.Length == 0))
            throw new ArgumentException("拆分产生了空块，请调整拆分点");
        return parts;
    }
}
