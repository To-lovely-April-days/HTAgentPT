using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace HT.Agent.Infrastructure.Services;

/// <summary>混合检索（FR-4.3/4.4）：向量与关键词两路并行召回，密级、公司归属与数据范围
/// 作为查询条件参与检索——过滤发生在候选选取之前，不在应用层二次筛选（7.2）。</summary>
public class RetrievalService(
    AppDbContext db,
    IEmbeddingClient embedder,
    IRerankClient reranker,
    IRuntimeConfig config,
    ICurrentUser me) : IRetrievalService
{
    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest req, CancellationToken ct = default)
    {
        var recallK = await config.GetIntAsync(ConfigKeys.RecallTopK, 50, ct);
        var rerankN = await config.GetIntAsync(ConfigKeys.RerankTopN, 8, ct);
        var threshold = await config.GetDoubleAsync(ConfigKeys.ScoreThreshold, 0.62, ct);
        var alpha = await config.GetDoubleAsync(ConfigKeys.HybridAlpha, 0.6, ct);
        var candFactor = await config.GetIntAsync(ConfigKeys.AnnCandidateFactor, 4, ct);

        // 数据范围（表 3-1）：总部审核人仅共享库；客户仅公开库；员工按功能权限展开
        var tiers = ResolveTierScope();
        if (tiers.Count == 0 || me.Classifications.Count == 0)
            return new RetrievalResult(false, [], [], 0, req.Query);

        // 向量路。向量化服务不可用时降级为纯关键词，不让在线问答跟着离线组件一起倒
        Vector? qvec = null;
        try
        {
            var v = await embedder.EmbedAsync([req.Query], ct);
            qvec = new Vector(v[0]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }

        var tsQuery = ChineseTokenizer.ToTsQuery(req.Query);

        var vectorRanked = qvec is null
            ? []
            : await VectorPathAsync(qvec, tiers, req, recallK * Math.Max(1, candFactor) / Math.Max(1, candFactor), recallK, ct);
        var keywordRanked = string.IsNullOrEmpty(tsQuery)
            ? new List<long>()
            : await KeywordPathAsync(tsQuery, tiers, req, recallK, ct);

        var effectiveAlpha = qvec is null ? 0 : (keywordRanked.Count == 0 ? 1 : alpha);
        var fused = RrfFusion.Fuse(vectorRanked, keywordRanked, effectiveAlpha);
        if (fused.Count == 0)
            return new RetrievalResult(false, [], [], 0, req.Query);

        // 载入候选内容（顺序保持融合排名）
        var ids = fused.Select(f => f.ChunkId).ToList();
        var rows = await db.Chunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Join(db.Documents.AsNoTracking(), c => c.DocId, d => d.Id, (c, d) => new
            {
                c.Id, c.DocId, DocTitle = d.Title, c.SectionPath, c.PageNo, c.Text, c.Classification
            })
            .ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);
        var ordered = fused.Where(f => byId.ContainsKey(f.ChunkId)).ToList();

        // 重排（FR-4.6）：全部召回送打分，取最高若干条
        var passages = ordered.Select(f => byId[f.ChunkId].Text).ToList();
        var scores = await reranker.ScoreAsync(req.Query, passages, ct);

        var reranked = ordered.Select((f, i) => (Fused: f, Score: scores[i]))
            .OrderByDescending(x => x.Score)
            .ToList();
        var top = reranked.Take(rerankN).ToList();
        var topScore = top.Count > 0 ? top[0].Score : 0;

        // 阈值拦截（FR-4.7）：最高分低于阈值不进入生成，列出可能相关的文档名供用户判断
        if (topScore < threshold)
        {
            var hints = reranked.Take(10)
                .Select(x => byId[x.Fused.ChunkId].DocTitle)
                .Distinct().Take(5).ToList();
            return new RetrievalResult(false, [], hints, topScore, req.Query);
        }

        var chunks = top.Select(x =>
        {
            var r = byId[x.Fused.ChunkId];
            return new RetrievedChunk(r.Id, r.DocId, r.DocTitle, r.SectionPath, r.PageNo,
                r.Text, x.Fused.Score, x.Score, r.Classification);
        }).ToList();
        return new RetrievalResult(true, chunks, [], topScore, req.Query);
    }

    /// <summary>可检索的库分层范围。总部审核人不可访问任何公司私有库——这是数据范围而非权限开关（表 3-1）。</summary>
    private List<KnowledgeBaseTier> ResolveTierScope()
    {
        var tiers = new List<KnowledgeBaseTier>();
        if (me.Permissions.Contains(PermissionKeys.QaInternal))
        {
            tiers.Add(KnowledgeBaseTier.Shared);
            if (me.RoleCode != RoleCodes.HqReviewer)
                tiers.Add(KnowledgeBaseTier.Private);
        }
        if (me.Permissions.Contains(PermissionKeys.QaPublic))
            tiers.Add(KnowledgeBaseTier.Public);
        return tiers;
    }

    // 单条 SQL：密级 + 库范围 + 公司归属 + 元数据筛选 + 近似最近邻，一次完成（FR-4.4、7.2）。
    private async Task<List<long>> VectorPathAsync(
        Vector qvec, List<KnowledgeBaseTier> tiers, RetrievalRequest req, int _, int recallK, CancellationToken ct)
    {
        var sql = $"""
            SELECT c.id AS "Value"
            FROM chunk c
            JOIN document d ON d.id = c.doc_id
            LEFT JOIN doc_metadata m ON m.document_id = d.id
            WHERE c.is_active
              AND c.embedding IS NOT NULL
              AND c.classification = ANY(@cls)
              AND d.parse_status = 'Parsed'
              AND NOT d.is_withdrawn
              AND c.kb_id IN (SELECT kb.id FROM knowledge_base kb
                              WHERE kb.is_active AND kb.tier = ANY(@tiers)
                                AND (kb.tier = 'Shared' OR kb.company_id = @company)
                                AND (@kb_filter = FALSE OR kb.id = ANY(@kb_ids)))
              AND (@customer::text IS NULL OR m.customer_name = @customer)
              AND (@year::int IS NULL OR m.year = @year)
              AND (@device::text IS NULL OR m.device_type = @device)
              AND (@category::text IS NULL OR m.doc_category = @category)
            ORDER BY c.embedding <=> @qvec
            LIMIT @limit
            """;
        return await QueryIdsAsync(sql, tiers, req, recallK, p => p.Add(new NpgsqlParameter("qvec", qvec)), ct);
    }

    private async Task<List<long>> KeywordPathAsync(
        string tsQuery, List<KnowledgeBaseTier> tiers, RetrievalRequest req, int recallK, CancellationToken ct)
    {
        var sql = $"""
            SELECT c.id AS "Value"
            FROM chunk c
            JOIN document d ON d.id = c.doc_id
            LEFT JOIN doc_metadata m ON m.document_id = d.id
            WHERE c.is_active
              AND c.classification = ANY(@cls)
              AND d.parse_status = 'Parsed'
              AND NOT d.is_withdrawn
              AND c.kb_id IN (SELECT kb.id FROM knowledge_base kb
                              WHERE kb.is_active AND kb.tier = ANY(@tiers)
                                AND (kb.tier = 'Shared' OR kb.company_id = @company)
                                AND (@kb_filter = FALSE OR kb.id = ANY(@kb_ids)))
              AND (@customer::text IS NULL OR m.customer_name = @customer)
              AND (@year::int IS NULL OR m.year = @year)
              AND (@device::text IS NULL OR m.device_type = @device)
              AND (@category::text IS NULL OR m.doc_category = @category)
              AND to_tsvector('simple', c.search_text) @@ to_tsquery('simple', @tsq)
            ORDER BY ts_rank_cd(to_tsvector('simple', c.search_text), to_tsquery('simple', @tsq)) DESC
            LIMIT @limit
            """;
        return await QueryIdsAsync(sql, tiers, req, recallK, p => p.Add(new NpgsqlParameter("tsq", tsQuery)), ct);
    }

    private async Task<List<long>> QueryIdsAsync(
        string sql, List<KnowledgeBaseTier> tiers, RetrievalRequest req, int limit,
        Action<List<NpgsqlParameter>> extra, CancellationToken ct)
    {
        var pars = new List<NpgsqlParameter>
        {
            new("cls", me.Classifications.Select(c => c.ToString()).ToArray()),
            new("tiers", tiers.Select(t => t.ToString()).ToArray()),
            new("company", me.CompanyId),
            new("kb_filter", req.KbIds is { Count: > 0 }),
            new("kb_ids", req.KbIds?.ToArray() ?? []),
            new("customer", (object?)req.CustomerName ?? DBNull.Value) { NpgsqlDbType = NpgsqlDbType.Text },
            new("year", (object?)req.Year ?? DBNull.Value) { NpgsqlDbType = NpgsqlDbType.Integer },
            new("device", (object?)req.DeviceType ?? DBNull.Value) { NpgsqlDbType = NpgsqlDbType.Text },
            new("category", (object?)req.DocCategory ?? DBNull.Value) { NpgsqlDbType = NpgsqlDbType.Text },
            new("limit", limit)
        };
        extra(pars);
        return await db.Database.SqlQueryRaw<long>(sql, pars.ToArray()).ToListAsync(ct);
    }
}
