namespace HT.Agent.Domain;

/// <summary>运行时配置键（sys_config 表）。全部可在管理界面修改，改后即时生效（FR-9.6）；
/// 外部组件地址一律为配置项，更换引擎或模型不得修改业务代码（表 8-2、10.4）。</summary>
public static class ConfigKeys
{
    // ── 模型接口（FR-9.5）──────────────────────────────
    public const string ChatModelUrl = "model.chat.url";
    public const string ChatModelName = "model.chat.name";
    public const string ChatTemperature = "model.chat.temperature";
    public const string ChatMaxTokens = "model.chat.max_tokens";
    public const string EmbeddingUrl = "model.embedding.url";
    public const string EmbeddingModelName = "model.embedding.name";
    /// <summary>向量维度随模型定；换模型必须全量重建索引（FR-2.2、E15）。</summary>
    public const string EmbeddingDimension = "model.embedding.dimension";
    public const string EmbeddingBatchSize = "model.embedding.batch_size";
    public const string RerankUrl = "model.rerank.url";
    public const string RerankModelName = "model.rerank.name";
    // ── 解析引擎（表 8-2）──────────────────────────────
    public const string ParserUrl = "parser.url";
    public const string ParserTimeoutSeconds = "parser.timeout_seconds";
    public const string ParserMaxRetries = "parser.max_retries";
    /// <summary>解析并行度（FR-1.2：串行或有限并行）。</summary>
    public const string ParserConcurrency = "parser.concurrency";
    // ── 检索链路参数（5.1 末段：均须可配置，不得硬编码）────
    public const string RecallTopK = "retrieval.recall_top_k";
    public const string RerankTopN = "retrieval.rerank_top_n";
    /// <summary>相似度阈值。重排分值非标定值，须以真实问题集标定（FR-4.7）。</summary>
    public const string ScoreThreshold = "retrieval.score_threshold";
    public const string ContextTokens = "retrieval.context_tokens";
    public const string MaxRetrievalPerTurn = "retrieval.max_per_turn";
    public const string HistoryTurns = "retrieval.history_turns";
    /// <summary>关键词与向量两路权重（FR-4.3），0 纯关键词，1 纯向量。</summary>
    public const string HybridAlpha = "retrieval.hybrid_alpha";
    /// <summary>近似索引候选倍数（7.2：候选数须高于最终保留数的倍数并可配置）。</summary>
    public const string AnnCandidateFactor = "retrieval.ann_candidate_factor";
    // ── 切分参数（FR-1.4、表 4-2）──────────────────────
    public const string ChunkTargetLength = "chunking.target_length";
    public const string ChunkOverlap = "chunking.overlap";
    public const string ChunkMinLength = "chunking.min_length";
    // ── 上传限制（FR-1.1：单文件上限与总量上限可配置）────
    public const string UploadMaxFileMb = "upload.max_file_mb";
    public const string UploadMaxBatch = "upload.max_batch";
    // ── 会话与安全 ─────────────────────────────────
    public const string JwtLifetimeMinutes = "auth.jwt_lifetime_minutes";
    /// <summary>审计留存月数，不少于十二（FR-9.1）。</summary>
    public const string AuditRetentionMonths = "audit.retention_months";
    // ── 翻译（FR-6.2：单批长度可配置）───────────────────
    public const string TranslateBatchChars = "translate.batch_chars";
}
