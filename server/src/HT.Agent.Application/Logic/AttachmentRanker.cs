using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>从对话附件里挑和问题最相关的几块交给模型。附件是用户当场给的，没有向量索引，
/// 按「问题与分块共有的二字片段数」打分就够用：问「防爆等级」，含「防爆」「爆等」「等级」的块排前面。
/// 问题本身很笼统（「总结一下」「这份文件讲了什么」）谁都打不出分时，取文件开头几块——开头通常是概述。</summary>
public static class AttachmentRanker
{
    public record Piece(Guid SourceId, string FileName, int Seq, string? Section, int? Page, string Text);

    public static List<Piece> Pick(IReadOnlyList<Piece> pieces, string question, int take = 4)
    {
        if (pieces.Count == 0) return [];
        var grams = Grams(question);
        var scored = pieces.Select((p, i) => (p, i, score: grams.Count == 0 ? 0 : Grams(p.Text).Count(grams.Contains))).ToList();
        if (scored.All(s => s.score == 0))
        {
            // 每个文件都取开头，多个文件时轮流取，免得一个长文件把别的挤掉
            return pieces.GroupBy(p => p.SourceId)
                .SelectMany(g => g.OrderBy(p => p.Seq).Select((p, k) => (p, k)))
                .OrderBy(x => x.k).Select(x => x.p).Take(take).ToList();
        }
        return scored.Where(s => s.score > 0)
            .OrderByDescending(s => s.score).ThenBy(s => s.i)
            .Take(take).Select(s => s.p).ToList();
    }

    private static readonly Regex Noise = new(@"[\s\p{P}\p{S}]+", RegexOptions.Compiled);

    /// <summary>二字片段（中文按字、英文数字按小写）。问句里的「这份」「文件」「一下」这类虚词片段不算。</summary>
    private static HashSet<string> Grams(string text)
    {
        var t = Noise.Replace(text.ToLowerInvariant(), " ");
        var set = new HashSet<string>();
        foreach (var part in t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            for (var i = 0; i + 1 < part.Length; i++)
                set.Add(part.Substring(i, 2));
        set.ExceptWith(Stop);
        return set;
    }

    private static readonly HashSet<string> Stop =
        ["这份", "份文", "文件", "件里", "里面", "一下", "什么", "多少", "有没", "没有", "是否", "请问", "帮我", "看看", "这个", "那个", "上传", "附件"];
}
