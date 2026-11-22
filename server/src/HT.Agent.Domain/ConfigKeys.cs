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
    /// <summary>模型服务的上下文长度（vLLM 的 --max-model-len）。到 32K 才开放深度档；助手每轮按它算回答还能写多长。</summary>
    public const string ChatContextTokens = "model.chat.context_tokens";
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
    /// <summary>Word（.docx）是否本地解析（默认 true）。Word 的结构在文件里是现成的，
    /// 本地解析更准也不出网；设为 false 则与其他格式一样交给所选解析引擎。</summary>
    public const string ParserOfficeLocal = "parser.office_local";
    // ── 图片理解（知识库解析与对话附件共用）──────────────────
    /// <summary>解析时让模型看图、把图写成文字入库（true/false，默认 false）。要求对话模型能看图
    /// （vLLM 启动时不带 --language-model-only）；模型不收图时自动跳过，不影响解析。</summary>
    public const string VisionEnabled = "vision.enabled";
    /// <summary>看图用的接口地址，留空=同对话模型。</summary>
    public const string VisionUrl = "vision.url";
    /// <summary>看图用的模型名，留空=同对话模型。</summary>
    public const string VisionModelName = "vision.name";
    /// <summary>每份文档最多看几张图（默认 30，大图优先）。</summary>
    public const string VisionMaxImages = "vision.max_images";
    // ── 检索链路参数（5.1 末段：均须可配置，不得硬编码）────
    public const string RecallTopK = "retrieval.recall_top_k";
    public const string RerankTopN = "retrieval.rerank_top_n";
    /// <summary>相似度阈值。重排分值非标定值，须以真实问题集标定（FR-4.7）。</summary>
    public const string ScoreThreshold = "retrieval.score_threshold";
    public const string ContextTokens = "retrieval.context_tokens";
    public const string MaxRetrievalPerTurn = "retrieval.max_per_turn";
    public const string HistoryTurns = "retrieval.history_turns";

    // ── 回答质量 ──────────────────────────────────────
    /// <summary>跑测试题用的账号（用户名）。专门建一个普通员工角色的账号，问出来的结果和大家看到的一样；空=用点「跑一遍」的人的账号。</summary>
    public const string QualityEvalUser = "quality.eval_user";

    // ── 通用助手 ──────────────────────────────────────
    /// <summary>问答方式：assistant=通用助手（模型自己决定查什么，默认）；classic=原来的分类+固定流程（回退用）。</summary>
    public const string QaEngine = "assistant.engine";
    // 思考档位（输入框里选「自动 / 快速 / 标准 / 深度」）：每档一组参数——
    // 思考强度（off / low / medium / xhigh）、最多查几轮、每轮回答最长（token，含思考）、温度
    /// <summary>默认档位：auto / fast / standard / deep。</summary>
    public const string AssistantLevelDefault = "assistant.level.default";
    /// <summary>员工能不能在输入框里自己选档位：true/false。</summary>
    public const string AssistantLevelUserChoice = "assistant.level.user_choice";
    public const string LevelFastEffort = "assistant.level.fast.effort";
    public const string LevelFastRounds = "assistant.level.fast.rounds";
    public const string LevelFastMaxTokens = "assistant.level.fast.max_tokens";
    public const string LevelFastTemperature = "assistant.level.fast.temperature";
    public const string LevelStandardEffort = "assistant.level.standard.effort";
    public const string LevelStandardRounds = "assistant.level.standard.rounds";
    public const string LevelStandardMaxTokens = "assistant.level.standard.max_tokens";
    public const string LevelStandardTemperature = "assistant.level.standard.temperature";
    public const string LevelDeepEffort = "assistant.level.deep.effort";
    public const string LevelDeepRounds = "assistant.level.deep.rounds";
    public const string LevelDeepMaxTokens = "assistant.level.deep.max_tokens";
    public const string LevelDeepTemperature = "assistant.level.deep.temperature";
    /// <summary>放进模型的对话+资料总字数上限；超出时最早的资料换成摘要。按上下文 32K 估。</summary>
    public const string AssistantContextChars = "assistant.context_chars";
    /// <summary>每次搜索最多返回几段资料给模型。</summary>
    public const string AssistantSearchTopK = "assistant.search_top_k";
    /// <summary>公司介绍（一两句话，告诉助手我们是做什么的）。是数据不是规则，管理员可随时改。</summary>
    public const string AssistantProfile = "assistant.company_profile";

    // ── 助手做文件（在计算沙箱里写代码做 Excel、Word、PPT、PDF）：每种一个开关和可用角色（空=所有内部员工）──
    public const string FileXlsx = "assistant.file.xlsx";
    public const string FileXlsxRoles = "assistant.file.xlsx.roles";
    public const string FileDocx = "assistant.file.docx";
    public const string FileDocxRoles = "assistant.file.docx.roles";
    public const string FilePptx = "assistant.file.pptx";
    public const string FilePptxRoles = "assistant.file.pptx.roles";
    public const string FilePdf = "assistant.file.pdf";
    public const string FilePdfRoles = "assistant.file.pdf.roles";
    /// <summary>做好的文件保留几天。</summary>
    public const string FileKeepDays = "assistant.file.keep_days";

    // ── 分头查：助手按任务复杂度自己决定要不要派子代理（标准、深度档才能拆，快速档不拆）──
    /// <summary>on / off。</summary>
    public const string Delegate = "assistant.delegate";
    /// <summary>一次最多拆几个子任务。</summary>
    public const string DelegateMaxTasks = "assistant.delegate.max_tasks";
    /// <summary>同时跑几个子代理（模型服务同时 4 路，留一路给别人）。</summary>
    public const string DelegateParallel = "assistant.delegate.parallel";
    /// <summary>子代理最多查几轮，超过算没查完。</summary>
    public const string DelegateRounds = "assistant.delegate.rounds";

    // ── 工具按需加载：常用工具每次都带，流程模拟、做 PPT、浏览器这类专用工具用得上才把完整说明交给模型 ──
    /// <summary>on_demand（按需加载）/ all（全部都带，和以前一样）。</summary>
    public const string ToolLoading = "assistant.tool_loading";
    /// <summary>常驻的专用工具：逗号分隔的组名（show_block、make_ppt…），每次都带。</summary>
    public const string ToolsPinned = "assistant.tools_pinned";
    /// <summary>问题和工具用途的相似度到多少就提前加载（0.3–0.95）。</summary>
    public const string ToolPreloadScore = "assistant.tool_preload_score";

    // ── 内置浏览器（服务器上的 Chromium，助手能读、能点、能填）──
    public const string ToolBrowser = "assistant.tool.browser";
    public const string ToolBrowserRoles = "assistant.tool.browser.roles";
    /// <summary>浏览器容器地址。</summary>
    public const string BrowserUrl = "browser.url";
    /// <summary>允许打开的内网地址（逗号分隔的域名或 IP，如 erp.htlab.local）。默认内网一律拦。</summary>
    public const string BrowserAllowHosts = "browser.allow_hosts";
    /// <summary>不让打开的网站（逗号分隔），网银、支付类默认已拦。</summary>
    public const string BrowserBlockHosts = "browser.block_hosts";

    // ── 助手工具：每个工具一个开关（on/off）和可用角色（逗号分隔的角色代码，空=所有内部员工）──
    public const string ToolAskUser = "assistant.tool.ask_user";
    public const string ToolAskUserRoles = "assistant.tool.ask_user.roles";
    public const string ToolPython = "assistant.tool.run_python";
    public const string ToolPythonRoles = "assistant.tool.run_python.roles";
    public const string ToolChemical = "assistant.tool.chemical_info";
    public const string ToolChemicalRoles = "assistant.tool.chemical_info.roles";
    public const string ToolWebSearch = "assistant.tool.web_search";
    public const string ToolWebSearchRoles = "assistant.tool.web_search.roles";
    public const string ToolFetchUrl = "assistant.tool.fetch_url";
    public const string ToolFetchUrlRoles = "assistant.tool.fetch_url.roles";
    public const string ToolCompare = "assistant.tool.compare_documents";
    public const string ToolCompareRoles = "assistant.tool.compare_documents.roles";
    /// <summary>代码解释器服务地址（deploy/docker-compose.tools.yml 里的 sandbox 容器）。</summary>
    public const string SandboxUrl = "assistant.sandbox_url";
    /// <summary>流程模拟（DWSIM 官方无界面服务 dwsim-mcp）的地址；访问口令走环境变量 DWSIM_TOKEN，不进数据库。</summary>
    public const string DwsimUrl = "assistant.dwsim_url";
    /// <summary>DWSIM 服务和后端共用的目录（两边挂同一个卷、同一个路径），模拟文件存在这里交给后端。</summary>
    public const string DwsimFilesDir = "assistant.dwsim_files_dir";
    /// <summary>每次运行最长秒数。</summary>
    public const string SandboxTimeout = "assistant.sandbox_timeout";
    /// <summary>联网搜索服务地址（自建 SearXNG）。</summary>
    public const string SearxUrl = "assistant.searx_url";
    /// <summary>用哪些搜索引擎（SearXNG 的引擎名，逗号分隔）。</summary>
    public const string SearxEngines = "assistant.searx_engines";
    /// <summary>本地《危险化学品目录》文件（CSV：CAS 号,中文名；相对数据目录）。没有就不判断是否危化品。</summary>
    public const string HazchemFile = "assistant.hazchem_file";
    /// <summary>长期记忆全公司开关（on/off）。</summary>
    public const string MemoryEnabled = "assistant.memory";
    /// <summary>每人最多几条记忆。</summary>
    public const string MemoryMax = "assistant.memory_max";
    /// <summary>欧洲专利局 OPS 的 Consumer Key / Secret（developers.epo.org 免费注册）。键名带 api_key，读配置时打码。</summary>
    public const string EpoKey = "assistant.epo_api_key";
    public const string EpoSecret = "assistant.epo_secret_api_key";
    /// <summary>论文检索（OpenAlex）请求里带的联系邮箱，可空。OpenAlex 建议带上，配额更宽。</summary>
    public const string OpenAlexMailto = "assistant.openalex_mailto";
    public const string ToolLiterature = "assistant.tool.search_literature";
    public const string ToolLiteratureRoles = "assistant.tool.search_literature.roles";
    public const string ToolVision = "assistant.tool.view_image";
    public const string ToolVisionRoles = "assistant.tool.view_image.roles";
    public const string ToolTranslateFile = "assistant.tool.translate_file";
    public const string ToolTranslateFileRoles = "assistant.tool.translate_file.roles";
    public const string ToolInquiryForm = "assistant.tool.inquiry_form";
    public const string ToolInquiryFormRoles = "assistant.tool.inquiry_form.roles";
    /// <summary>查价格 + 材料成本估算（还要求「查看采购价格」权限）。</summary>
    public const string ToolPrices = "assistant.tool.search_prices";
    public const string ToolPricesRoles = "assistant.tool.search_prices.roles";
    /// <summary>按场景排版的表格。</summary>
    public const string ToolShowTable = "assistant.tool.show_table";
    public const string ToolShowTableRoles = "assistant.tool.show_table.roles";
    /// <summary>回答里的组件（关键数字、提示框、图表、步骤、数据表、偏离表、甘特图等）。</summary>
    public const string ToolShowBlock = "assistant.tool.show_block";
    public const string ToolShowBlockRoles = "assistant.tool.show_block.roles";
    /// <summary>价格表多少个月没更新就标「可能过时」。</summary>
    public const string PriceStaleMonths = "price.stale_months";
    /// <summary>只有未税价时推算含税价用的税率系数（如 1.13）。</summary>
    public const string PriceTaxRate = "price.tax_rate";
    /// <summary>关键词与向量两路权重（FR-4.3），0 纯关键词，1 纯向量。</summary>
    public const string HybridAlpha = "retrieval.hybrid_alpha";
    /// <summary>近似索引候选倍数（7.2：候选数须高于最终保留数的倍数并可配置）。</summary>
    public const string AnnCandidateFactor = "retrieval.ann_candidate_factor";
    // ── 切分参数（FR-1.4、表 4-2）──────────────────────
    public const string ChunkTargetLength = "chunking.target_length";
    /// <summary>入库时让模型给每个分块写一句「这段讲的是什么」（on/off）。只在解析时跑一次，不影响回答速度。</summary>
    public const string ChunkContextSummary = "chunking.context_summary";
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
    /// <summary>内置 PPT 模板所在目录（容器里挂载 deploy/ppt-templates）。</summary>
    public const string PptTemplateDir = "ppt.template_dir";
    /// <summary>不许放进模板库的网站（模板许可不许转发、不许放进数据库），逗号隔开；上传时来源网址含这些就拦下。</summary>
    public const string PptBlockedSources = "ppt.blocked_sources";
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
