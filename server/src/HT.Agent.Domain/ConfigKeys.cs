namespace HT.Agent.Domain;

/// <summary>运行时配置键（sys_config 表）。全部可在管理界面修改，改后即时生效（FR-9.6）；
/// 外部组件地址一律为配置项，更换引擎或模型不得修改业务代码（表 8-2、10.4）。</summary>
public static class ConfigKeys
{
    // ── 模型接口（FR-9.5）──────────────────────────────
    /// <summary>对话模型提供方（E15 模型选择）：stub=内置演示应答；其余值走 OpenAI 兼容端点。
    /// 运行时可切，改后即时生效——嵌入/重排/解析不随此切换。</summary>
    public const string ChatProvider = "model.chat.provider";
    public const string ChatModelUrl = "model.chat.url";
    public const string ChatModelName = "model.chat.name";
    /// <summary>在线服务的接口密钥（如 DeepSeek）。读取端只回显掩码，不外传。</summary>
    public const string ChatApiKey = "model.chat.api_key";
    public const string ChatTemperature = "model.chat.temperature";
    public const string ChatMaxTokens = "model.chat.max_tokens";
    /// <summary>向量化提供方（E15 服务选择）：stub=内置演示向量；其余值（siliconflow/local…）
    /// 走兼容端点。运行时可切——但切换即换向量空间，须配合全量重建（页底哨兵行）。</summary>
    public const string EmbeddingProvider = "model.embedding.provider";
    public const string EmbeddingUrl = "model.embedding.url";
    public const string EmbeddingModelName = "model.embedding.name";
    /// <summary>在线向量化服务密钥（如硅基流动）。读取端只回显掩码。</summary>
    public const string EmbeddingApiKey = "model.embedding.api_key";
    /// <summary>向量维度随模型定；换模型必须全量重建索引（FR-2.2、E15）。</summary>
    public const string EmbeddingDimension = "model.embedding.dimension";
    public const string EmbeddingBatchSize = "model.embedding.batch_size";
    /// <summary>重排提供方：stub=内置演示；其余值走 /rerank 兼容端点。运行时可切，无重建代价。</summary>
    public const string RerankProvider = "model.rerank.provider";
    public const string RerankUrl = "model.rerank.url";
    public const string RerankModelName = "model.rerank.name";
    /// <summary>在线重排服务密钥。读取端只回显掩码。</summary>
    public const string RerankApiKey = "model.rerank.api_key";
    // ── 解析引擎（表 8-2）──────────────────────────────
    /// <summary>解析提供方：stub=内置演示（仅纯文本）/ mineru-local=本地 MinerU 容器 /
    /// mineru-online=MinerU 官方在线 API / http=通用契约。运行时可切，改后即时生效。</summary>
    public const string ParserProvider = "parser.provider";
    public const string ParserUrl = "parser.url";
    /// <summary>MinerU 官方在线服务基址（mineru-online 时使用）。</summary>
    public const string ParserOnlineBase = "parser.online_base";
    /// <summary>在线解析令牌（mineru.net 用户中心领取）。读取端只回显掩码。</summary>
    public const string ParserApiKey = "parser.api_key";
    /// <summary>解析后端（MinerU 的 backend 参数）：pipeline / vlm-transformers / vlm-vllm-engine 等，
    /// 取值随所接引擎版本。仅 Models:Parser=mineru 时生效。</summary>
    public const string ParserBackend = "parser.backend";
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
    // ── 意图路由（10.4：编号规则不得硬编码）───────────────
    public const string IntentProjectNoPattern = "intent.project_no_pattern";
    // ── 故障案例 ─────────────────────────────────
    /// <summary>案例编号前缀（10.4）。编号形如 {前缀}-{年}-{序号:0000}。</summary>
    public const string CaseNoPrefix = "case.no_prefix";
    /// <summary>报修工单编号前缀（10.4）。</summary>
    public const string TicketNoPrefix = "ticket.no_prefix";
    /// <summary>docx→PDF 转换服务地址（FR-5.14）。空 = 未接入，预览走标色 HTML。</summary>
    public const string PdfConverterUrl = "pdf.converter_url";
    // ── 备份（FR-9.3/9.4）────────────────────────────
    public const string BackupDir = "backup.dir";
    /// <summary>异地目标目录（挂载的远端卷/同步盘）。空 = 未配置，异地任务如实标注不执行。</summary>
    public const string BackupRemoteDir = "backup.remote_dir";
    public const string BackupLocalIncrementalHours = "backup.local_incremental_hours";
    public const string BackupLocalFullDays = "backup.local_full_days";
    public const string BackupRemoteFullDays = "backup.remote_full_days";
    // ── 共享库同步（FR-2.2）──────────────────────────
    /// <summary>总部节点地址（公司节点侧配置）。</summary>
    public const string SyncHqUrl = "sync.hq_url";
    /// <summary>拉取共享库时携带的令牌（公司节点侧配置）。</summary>
    public const string SyncToken = "sync.token";
    /// <summary>接受的拉取令牌（总部节点侧配置）。</summary>
    public const string SyncAcceptToken = "sync.accept_token";
}
