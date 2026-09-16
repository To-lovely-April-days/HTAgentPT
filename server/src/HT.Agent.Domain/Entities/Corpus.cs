using Pgvector;

namespace HT.Agent.Domain.Entities;

/// <summary>知识库（表 7-1）。tier 创建后不可修改；需变更时新建库并迁移（FR-2.1）。</summary>
public class KnowledgeBase
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public KnowledgeBaseTier Tier { get; set; }
    /// <summary>共享库属集团，无公司归属；私有库与公开库归属本公司。</summary>
    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }
    /// <summary>库级默认切分策略（FR-1.4），单文档可覆盖。</summary>
    public ChunkStrategy DefaultChunkStrategy { get; set; } = ChunkStrategy.General;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>文档（表 7-1）。classification 决定可见范围；parse_status 控制是否参与检索。</summary>
public class Document
{
    public Guid Id { get; set; }
    public Guid KbId { get; set; }
    public KnowledgeBase? Kb { get; set; }
    public required string FileName { get; set; }
    public required string Title { get; set; }
    /// <summary>对象存储中的键。原始文件存入对象存储并保留（FR-1.8）。</summary>
    public required string FileKey { get; set; }
    public long FileSize { get; set; }
    public required string ContentType { get; set; }
    public string? Sha256 { get; set; }
    public Classification Classification { get; set; }
    public ParseStatus ParseStatus { get; set; } = ParseStatus.NotParsed;
    /// <summary>失败时记录具体原因：文件损坏、格式不支持、超时、引擎异常（FR-1.7）。</summary>
    public string? ParseError { get; set; }
    public ChunkStrategy? ChunkStrategyOverride { get; set; }
    /// <summary>重解析递增；分块携带该版本，旧版本分块作废（FR-1.6）。</summary>
    public int ParseVersion { get; set; }
    /// <summary>总部已删除或撤回的文档，同步时标记并停止参与检索（FR-2.6）。</summary>
    public bool IsWithdrawn { get; set; }
    public Guid UploadedById { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public DateTimeOffset? ParsedAt { get; set; }
    public DocMetadata? Metadata { get; set; }
}

/// <summary>文档元数据（表 4-5），与 document 一对一；受控字段须校验取值合法性。</summary>
public class DocMetadata
{
    public Guid DocumentId { get; set; }
    public Document? Document { get; set; }
    /// <summary>文档类别为方案/合同/报价/图纸/交付报告/故障记录时必填并关联台账。</summary>
    public string? ProjectNo { get; set; }
    public required string CustomerName { get; set; }
    public int Year { get; set; }
    /// <summary>受控词表取值（FR-3.2）。</summary>
    public required string DeviceType { get; set; }
    /// <summary>受控词表取值：方案、合同、报价、图纸、交付报告、故障记录、手册、资料。</summary>
    public required string DocCategory { get; set; }
    public Guid? OwnerId { get; set; }
    public string? DocVersion { get; set; }
    public DateOnly? EffectiveDate { get; set; }
}

/// <summary>受控词表（FR-3.2）：设备类型、文档类别等取值限定于词表，词表可维护。</summary>
public class VocabTerm
{
    public Guid Id { get; set; }
    /// <summary>词表键：device_type / doc_category / customer_name / term_domain ...</summary>
    public required string VocabKey { get; set; }
    public required string Value { get; set; }
    /// <summary>客户简称等别名（表 4-5：简称存入别名字段）。</summary>
    public string? Aliases { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>文本分块（表 7-1）。kb_id 与 classification 自 document 冗余存储，
/// 使权限过滤参与近似索引的候选选取（7.2），不在应用层二次筛选（FR-4.4）。</summary>
public class Chunk
{
    public long Id { get; set; }
    public Guid DocId { get; set; }
    public Document? Doc { get; set; }
    public Guid KbId { get; set; }
    public Classification Classification { get; set; }
    public int Seq { get; set; }
    /// <summary>章节路径，来源标注用（FR-4.9）。</summary>
    public string? SectionPath { get; set; }
    /// <summary>页码与位置框，支持原文跳转（表 7-1）。</summary>
    public int? PageNo { get; set; }
    public string? Bbox { get; set; }
    public required string Text { get; set; }
    /// <summary>应用侧分词结果（中文二元切分，型号/报警代码整串保留），供 simple 全文索引使用（7.2）。</summary>
    public required string SearchText { get; set; }
    public Vector? Embedding { get; set; }
    /// <summary>算出该向量的模型与版本。与当前配置不一致的分块必须可查出（FR-2.2、E15 哨兵）。</summary>
    public string? EmbeddingModel { get; set; }
    /// <summary>所属解析版本；重解析后旧版本分块整体失效（FR-1.6）。</summary>
    public int ParseVersion { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>解析任务队列（FR-1.2）：入库排队、串行或有限并行，避免占满算力影响在线问答。</summary>
/// <summary>解析抽取的文档图片（FR-4.9 来源精确到图）：图片本体在文件存储，这里记录归属、
/// 题注、页码与位置框。与分块同一生命周期——重解析整体重建，随文档级联删除。</summary>
public class DocImage
{
    public long Id { get; set; }
    public Guid DocId { get; set; }
    public Document? Doc { get; set; }
    public required string FileKey { get; set; }
    public string ContentType { get; set; } = "image/jpeg";
    public string? Caption { get; set; }
    /// <summary>页码从 1 起，与分块的 PageNo 同口径——来源命中分块后按页取图。</summary>
    public int? PageNo { get; set; }
    public string? Bbox { get; set; }
    public int Seq { get; set; }
    public int ParseVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ParseJob
{
    public Guid Id { get; set; }
    public Guid DocId { get; set; }
    public Document? Doc { get; set; }
    public ParseJobKind Kind { get; set; }
    /// <summary>EmbedOnly 时指向要重算向量的分块（FR-1.5）。</summary>
    public long? ChunkId { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public Guid QueuedBy { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>公开库发布记录（FR-2.3/2.4）：发布即建独立副本，记录操作人与内容快照；撤回记录原因。</summary>
public class PublishRecord
{
    public Guid Id { get; set; }
    public Guid SourceDocId { get; set; }
    public Guid PublicDocId { get; set; }
    public Guid OperatorId { get; set; }
    /// <summary>发布时内容快照的存储键。</summary>
    public required string SnapshotKey { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    /// <summary>确认时间。副本创建后可先编辑（FR-2.3：删除不宜公开的段落），确认后才对外可见。</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
    public string? WithdrawReason { get; set; }
    public Guid? WithdrawnBy { get; set; }
}
