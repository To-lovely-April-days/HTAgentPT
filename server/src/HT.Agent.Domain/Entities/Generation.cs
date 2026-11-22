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
    /// <summary>从哪条问答对话里起的。记在这边而不是反过来：
    /// 一条对话里可能先出任务单再出报价，单向指针会被后一个覆盖。</summary>
    public Guid? QaSessionId { get; set; }
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
    /// <summary>对话式填槽的会话状态（jsonb）：已跳过的槽位、上一轮在问的槽位。</summary>
    public string? ChatState { get; set; }
    /// <summary>设备清单（jsonb）：按设备展开的模板（技术协议）选了哪些设备参数表与部件行。
    /// null=还没定；"[]"=定了，一台都不要。</summary>
    public string? Equipment { get; set; }
}

/// <summary>对话式生成的消息记录：填单对话与生成记录同存同溯（FR-5.17 的对话形态）。
/// Payload 存该轮的结构化附件（本轮已填、待问清单、基准候选、汇总、渲染结果），前端据此渲染交互件。</summary>
public class GenChatMessage
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public GenerationSession? Session { get; set; }
    /// <summary>user / assistant。</summary>
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string? Payload { get; set; }
    public DateTimeOffset At { get; set; }
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
    /// <summary>归在哪个项目里（可空）。</summary>
    public Guid? ProjectId { get; set; }
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
    /// <summary>这一轮长出来的结果件（jsonb）：台账表、译文对照、案例、工单、模板推荐。
    /// 存下来是为了打开历史对话时还能原样看见——只留一句正文的话，
    /// 台账表和译文就全没了，等于没有历史。</summary>
    public string? Payload { get; set; }
    public bool? Helpful { get; set; }
    public string? FeedbackReason { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>对话里上传的文件（附件）：只在这条对话里当资料用，不进知识库。
/// 密级固定按「内部」处理；跟着对话一直保存。上传时还没有对话（首页第一句话带着文件），
/// 先挂在上传人名下，第一句话发出时绑到那条对话上。
/// 解析结果按分块存（Chunks，jsonb）：问答时按和问题的相关度挑几块交给模型，并作为可引用的依据。</summary>
public class ChatAttachment
{
    public Guid Id { get; set; }
    public Guid UploaderId { get; set; }
    public Guid CompanyId { get; set; }
    /// <summary>绑定的对话；null=已上传、还没随问题发出（或者是项目资料）。</summary>
    public Guid? QaSessionId { get; set; }
    /// <summary>项目资料：属于哪个项目（项目里的每个对话都能读）。和 QaSessionId 二选一。</summary>
    public Guid? ProjectId { get; set; }
    public required string FileName { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public required string FileKey { get; set; }
    public Classification Classification { get; set; } = Classification.Internal;
    public ParseStatus Status { get; set; } = ParseStatus.Queued;
    public string? Error { get; set; }
    /// <summary>[{seq, section, page, text}]（jsonb）。</summary>
    public string? Chunks { get; set; }
    public int CharCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>开始解析的时间：解析中停太久（进程崩了）就重新排队。</summary>
    public DateTimeOffset? StartedAt { get; set; }
    /// <summary>解析引擎不可用时下次重试的时间（10.3：等待而不是失败）。</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? ParsedAt { get; set; }
}
