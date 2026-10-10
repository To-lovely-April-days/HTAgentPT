using System.Text;
using HT.Agent.Application.Abstractions;

namespace HT.Agent.Application.Logic;

/// <summary>
/// Formats retrieved chunks for the specialist and synthesis agents.
/// Keep several adjacent table chunks in the prompt; cutting the evidence at a
/// small character limit makes a table look like a missing fact even when
/// retrieval already found it.
/// </summary>
public static class RetrievalEvidenceFormatter
{
    // The chat model's context budget is shared with agent opinions and history.
    // This is large enough for several table chunks while leaving room for the
    // final answer, and is deliberately a character budget rather than a token
    // claim because providers use different tokenizers.
    public const int DefaultMaxChars = 4800;
    public const int DefaultMaxChunkChars = 1600;

    public static string Format(
        RetrievalResult result,
        int maxChars = DefaultMaxChars,
        int maxChunkChars = DefaultMaxChunkChars)
    {
        if (!result.AboveThreshold || result.Chunks.Count == 0)
            return result.Notice is null
                ? "没有找到达到可信阈值的企业资料。"
                : $"检索提示：{result.Notice}\n没有找到达到可信阈值的企业资料。";

        maxChars = Math.Max(256, maxChars);
        maxChunkChars = Math.Max(128, maxChunkChars);
        var sb = new StringBuilder(Math.Min(maxChars, 8192));
        var used = 0;
        var truncated = false;

        for (var i = 0; i < result.Chunks.Count; i++)
        {
            var chunk = result.Chunks[i];
            var header = $"[{i + 1}] 《{chunk.DocTitle}》" +
                (chunk.SectionPath is null ? "" : $" · {chunk.SectionPath}") +
                (chunk.PageNo is null ? "" : $" · 第 {chunk.PageNo} 页") +
                (string.IsNullOrWhiteSpace(chunk.ProjectNo) ? "" : $" · 关联项目 {chunk.ProjectNo}");
            var text = chunk.Text;
            if (text.Length > maxChunkChars)
            {
                text = text[..maxChunkChars] + "\n（该分块内容较长，后续内容未显示）";
                truncated = true;
            }

            var block = header + "\n" + text + "\n";
            // Do not cut a table in the middle of a row merely to use the last
            // few characters of the budget. The next chunk is more useful as a
            // complete unit, and the marker tells the model why it is absent.
            if (used > 0 && used + block.Length > maxChars)
            {
                truncated = true;
                break;
            }

            if (used == 0 && block.Length > maxChars)
            {
                block = header + "\n" + text[..Math.Max(0, maxChars - header.Length - 2)] +
                    "\n（证据内容达到上下文上限）\n";
                truncated = true;
            }

            sb.Append(block);
            used += block.Length;
        }

        if (truncated)
            sb.AppendLine("[检索证据达到上下文上限，未显示更多候选分块。]");
        return sb.ToString();
    }
}
