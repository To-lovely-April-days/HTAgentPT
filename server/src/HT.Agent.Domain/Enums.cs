namespace HT.Agent.Domain;

/// <summary>知识库三级分层（表 7-1：tier 创建后不可修改）。物理分库维度，与文档密级是两个维度。</summary>
public enum KnowledgeBaseTier
{
    /// <summary>集团共享库：总部下发，本地只读（FR-2.2 同步为单向覆盖）。</summary>
    Shared,
    /// <summary>本公司私有库。</summary>
    Private,
    /// <summary>对外公开库：仅承载已审定发布的副本（FR-2.3）。</summary>
    Public
}

/// <summary>文档密级（表 3-1：机密/内部/公开）。驱动角色可见性，作为检索的 SQL 级过滤条件（FR-4.4）。</summary>
public enum Classification
{
    /// <summary>机密：售前可见，售后不可见（报价、合同、金额）。</summary>
    Confidential,
    /// <summary>内部。</summary>
    Internal,
    /// <summary>公开。</summary>
    Public
}

/// <summary>解析状态（表 7-1：parse_status 控制是否参与检索）。只有 Parsed 参与检索。</summary>
public enum ParseStatus
{
    /// <summary>已上传未解析（FR-1.2：上传后不自动解析）。</summary>
    NotParsed,
    Queued,
    Parsing,
    Parsed,
    /// <summary>解析失败，parse_error 记录具体原因（FR-1.7：不得仅显示失败二字）。</summary>
    Failed,
    /// <summary>引擎不可用，任务等待而非失败（10.3）。</summary>
    Waiting,
    /// <summary>重新解析中，暂不参与检索（FR-1.6）。</summary>
    Reparsing
}

/// <summary>切分策略（表 4-2）。库级配置，可单文档覆盖（FR-1.4）。</summary>
public enum ChunkStrategy
{
    /// <summary>按章节层级：手册、规程、方案、投标书。</summary>
    ByHeading,
    /// <summary>按条款：合同、报价单。</summary>
    ByClause,
    /// <summary>按行：参数表、物料清单，表头作前缀。</summary>
    ByRow,
    /// <summary>按语义段落：固定窗口+重叠。</summary>
    BySemantic,
    /// <summary>通用：语义段落优先，超长按固定窗口。</summary>
    General
}

/// <summary>案例同步状态（表 7-1：local / pending / shared / rejected）。</summary>
public enum CaseSyncStatus
{
    Local,
    Pending,
    Shared,
    Rejected
}

/// <summary>台账交付状态（表 4-6）。</summary>
public enum DeliveryStatus
{
    InProgress,
    Delivered,
    Closed
}

/// <summary>槽位填充来源（5.2.5：模板固定/继承/AI 建议待确认/已确认）。</summary>
public enum SlotFillSource
{
    Template,
    Inherited,
    AiSuggested,
    Confirmed
}

/// <summary>槽位填写阶段（表 5-2：后续阶段不提问、不预填、不给建议，输出留空）。</summary>
public enum SlotStage
{
    Current,
    Later
}

/// <summary>槽位数据类型（表 5-2）。</summary>
public enum SlotDataType
{
    Text,
    Number,
    SingleChoice,
    MultiChoice,
    Date,
    LongText,
    RepeatingRows
}

/// <summary>账号类别。客户账号绑定客户编号，仅见本人名下项目（FR-7.4）。</summary>
public enum UserKind
{
    Employee,
    Customer
}

/// <summary>审计结果。越权请求拒绝并记录（FR-7.3）。</summary>
public enum AuditResult
{
    Success,
    Denied,
    Failed
}

/// <summary>后台任务状态。Waiting 专指外部引擎不可用时的等待（10.3：不得丢弃请求）。</summary>
public enum JobStatus
{
    Queued,
    Running,
    Waiting,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>解析任务种类。EmbedOnly 用于分块编辑后仅重算该块向量（FR-1.5）。</summary>
public enum ParseJobKind
{
    Parse,
    Reparse,
    EmbedOnly
}

/// <summary>术语状态（FR-6.4：提交补充经管理员确认后生效）。</summary>
public enum TermStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>条款状态（FR-5.18：新增与修改须经指定人员确认；已审定条款锁定，改动走新版本）。</summary>
public enum ClauseStatus
{
    Draft,
    PendingReview,
    Active,
    Retired
}

/// <summary>生成会话状态。</summary>
public enum GenerationStatus
{
    Draft,
    Completed
}

/// <summary>工单状态（FR-8.8：本期最简流转）。</summary>
public enum TicketStatus
{
    Submitted,
    Assigned,
    InProgress,
    Resolved,
    Closed
}

/// <summary>备份种类（FR-9.3：本地增量、本地全量、异地全量）。</summary>
public enum BackupKind
{
    LocalIncremental,
    LocalFull,
    RemoteFull
}
