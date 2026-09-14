namespace HT.Agent.Domain.Entities;

/// <summary>文档模板（5.2.1）：Word 文件与槽位定义的组合。缺少槽位定义的模板无法进入交互流程。</summary>
public class Template
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    /// <summary>模板产出的文档类别（方案/报价/合同/生产任务单等，受控词表）。</summary>
    public required string DocType { get; set; }
    public required string FileKey { get; set; }
    /// <summary>槽位定义不完整时不可启用（表 7-1）。</summary>
    public bool IsEnabled { get; set; }
    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<TemplateSlot> Slots { get; set; } = [];
}

/// <summary>模板槽位（表 5-2）。Word 内容控件的 Tag 即槽位标识，上传时自动提取，其余属性界面编辑。</summary>
public class TemplateSlot
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public Template? Template { get; set; }
    /// <summary>槽位标识：内容控件 Tag，模板内唯一。</summary>
    public required string Tag { get; set; }
    public required string Name { get; set; }
    public required string Section { get; set; }
    public SlotDataType DataType { get; set; }
    /// <summary>数值类型的单位，随槽位一并输出（附录 C）。</summary>
    public string? Unit { get; set; }
    /// <summary>单选/复选的候选项（jsonb 数组）。</summary>
    public string? Choices { get; set; }
    /// <summary>必填槽位未完成时不允许生成；「待确认」也算未完成（FR-5.13）。</summary>
    public bool Required { get; set; }
    public SlotStage Stage { get; set; } = SlotStage.Current;
    /// <summary>禁止继承的槽位不从基准项目继承，强制转为提问（表 5-2）。</summary>
    public bool ForbidInherit { get; set; }
    /// <summary>提问话术。</summary>
    public string? Prompt { get; set; }
    /// <summary>建议来源提示（供检索建议用的范围说明）。</summary>
    public string? SuggestSource { get; set; }
    /// <summary>重复行子字段声明（jsonb）。</summary>
    public string? SubFields { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>生成会话（表 7-1）。槽位取值按整会话序列化存储于 SlotValues（7.2：避免数十项规模的逐槽往返）。</summary>
public class GenerationSession
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public Template? Template { get; set; }
    /// <summary>项目要点（创建会话时的输入）。</summary>
    public string? ProjectHint { get; set; }
    /// <summary>基准项目编号，选定后触发继承预填（FR-5.x）。</summary>
    public string? BaseProjectNo { get; set; }
    public GenerationStatus Status { get; set; } = GenerationStatus.Draft;
    /// <summary>全部槽位取值的序列化（jsonb）：当前值、填充来源、来源出处、确认状态。</summary>
    public string SlotValues { get; set; } = "[]";
    public Guid CreatedById { get; set; }
    public Guid CompanyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? OutputFileKey { get; set; }
}

/// <summary>问答会话（FR-4.10 多轮）。</summary>
public class QaSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid CompanyId { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<QaMessage> Messages { get; set; } = [];
}

/// <summary>问答消息。改写结果对用户可见（FR-4.2）；反馈用于调优（FR-4.11）。</summary>
public class QaMessage
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public QaSession? Session { get; set; }
    public required string Question { get; set; }
    public string? RewrittenQuery { get; set; }
    /// <summary>意图判定结果，对用户可见并可手动纠正（FR-4.1）。</summary>
    public string? Intent { get; set; }
    public string? Answer { get; set; }
    /// <summary>未找到时列出的可能相关文档名（FR-4.7）。</summary>
    public string? NoResultHints { get; set; }
    /// <summary>来源清单（jsonb）：文档、章节、页码、分块（FR-4.9）。</summary>
    public string? Sources { get; set; }
    public bool? Helpful { get; set; }
    public string? FeedbackReason { get; set; }
    public DateTimeOffset At { get; set; }
}
