namespace HT.Agent.Application.Logic;

/// <summary>倒数排名融合（FR-4.3）：向量与关键词两路并行召回后合并去重，两路权重可配置。</summary>
public static class RrfFusion
{
    /// <summary>k 为 RRF 平滑常数，业界常用 60。alpha 为向量路权重（0 纯关键词，1 纯向量）。</summary>
    public static IReadOnlyList<(long ChunkId, double Score)> Fuse(
        IReadOnlyList<long> vectorRanked,
        IReadOnlyList<long> keywordRanked,
        double alpha,
        int k = 60)
    {
        var scores = new Dictionary<long, double>();
        for (var i = 0; i < vectorRanked.Count; i++)
            scores[vectorRanked[i]] = scores.GetValueOrDefault(vectorRanked[i]) + alpha / (k + i + 1);
        for (var i = 0; i < keywordRanked.Count; i++)
            scores[keywordRanked[i]] = scores.GetValueOrDefault(keywordRanked[i]) + (1 - alpha) / (k + i + 1);
        return scores.OrderByDescending(p => p.Value).Select(p => (p.Key, p.Value)).ToList();
    }
}
