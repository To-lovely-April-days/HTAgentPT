using HT.Agent.Domain;

namespace HT.Agent.Application.Abstractions;

// ─────────────────────────── 模板管理（FR-5.3）───────────────────────────

public interface ITemplateService
{
    /// <summary>上传模板：自动抽取全部内容控件 Tag 生成槽位清单，占位文字作提问话术初值。</summary>
    Task<TemplateUploadResult> UploadAsync(Stream file, string fileName, string name, string docType, CancellationToken ct = default);
    Task<IReadOnlyList<TemplateRow>> ListAsync(bool includeDisabled, CancellationToken ct = default);
    Task<TemplateDetail?> GetAsync(Guid templateId, CancellationToken ct = default);
    Task UpdateSlotAsync(Guid slotId, SlotDefEdit edit, CancellationToken ct = default);
    /// <summary>启用门：槽位定义不完整（缺显示名称或章节）的模板不可启用。</summary>
    Task EnableAsync(Guid templateId, bool enable, CancellationToken ct = default);
    /// <summary>从既有模板复制后修改（FR-5.3）。</summary>
    Task<Guid> CopyAsync(Guid templateId, string newName, CancellationToken ct = default);
}

public record TemplateUploadResult(Guid TemplateId, int SlotsExtracted, IReadOnlyList<string> Warnings);

public record TemplateRow(Guid Id, string Name, string DocType, bool IsEnabled, int SlotCount,
    int IncompleteSlots, DateTimeOffset UpdatedAt, DateTimeOffset? LastUsedAt);

public record TemplateDetail(Guid Id, string Name, string DocType, bool IsEnabled, IReadOnlyList<SlotDef> Slots);

public record SlotDef(Guid Id, string Tag, string Name, string Section, SlotDataType DataType,
    string? Unit, string? Choices, bool Required, SlotStage Stage, bool ForbidInherit,
    string? Prompt, string? SuggestSource, string? SubFields, int SortOrder);

public record SlotDefEdit(string Name, string Section, SlotDataType DataType, string? Unit, string? Choices,
    bool Required, SlotStage Stage, bool ForbidInherit, string? Prompt, string? SuggestSource,
    string? SubFields, int SortOrder);

// ─────────────────────────── 生成会话（FR-5.4 至 5.17）───────────────────────────

public interface IGenerationService
{
    /// <summary>建会话（表 8-1）：返回会话标识与槽位清单。projectHint 为项目要点（FR-5.1 的确认结果）。</summary>
    Task<SessionView> CreateSessionAsync(Guid templateId, string? projectHint, CancellationToken ct = default);
    Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionRow>> ListSessionsAsync(CancellationToken ct = default);
    /// <summary>候选基准项目（FR-5.4）：二至三个，带关键规格与可继承槽位数量。</summary>
    Task<IReadOnlyList<BaseCandidate>> GetCandidatesAsync(Guid sessionId, CancellationToken ct = default);
    /// <summary>按明确条件查候选基准（对话里「查一下历史做过的」）：客户/设备类型为筛选，
    /// 关键词（型号、容积等）只影响排序；条件全空即最近项目。</summary>
    Task<IReadOnlyList<BaseCandidate>> FindCandidatesAsync(Guid sessionId, string? customer, string? deviceType,
        string? keyword, int take, CancellationToken ct = default);
    /// <summary>选定/更换/清除基准（FR-5.5）：用户确认过的槽位保留，其余重新预填。</summary>
    Task<SessionView> SetBaseProjectAsync(Guid sessionId, string? projectNo, CancellationToken ct = default);
    /// <summary>填写或确认单个槽位（表 8-1 PUT slots/{key}）。</summary>
    Task<SlotState> PutSlotAsync(Guid sessionId, string tag, SlotPut put, CancellationToken ct = default);
    /// <summary>AI 建议（FR-5.9/5.10）：检索取得并附依据；无依据明确说暂无。</summary>
    /// <param name="hint">用户这句原话：「这个材质我想改一下，推荐一下」里对现状的描述，
    /// 并进检索词能显著提准——不给也能用。</param>
    Task<SlotSuggestion> SuggestAsync(Guid sessionId, string tag, string? hint = null, CancellationToken ct = default);
    /// <summary>单项追问（FR-5.12）：就地对话，不影响其他槽位。</summary>
    Task<string> SlotChatAsync(Guid sessionId, string tag, string question, CancellationToken ct = default);
    /// <summary>完成校验（FR-5.13）：待填或待确认的必填槽位清单。</summary>
    Task<CompletenessView> CheckCompletenessAsync(Guid sessionId, CancellationToken ct = default);
    /// <summary>预览（FR-5.14 落地口径）：按来源标色的结构化预览；PDF 经可插拔转换服务。</summary>
    Task<PreviewView> PreviewAsync(Guid sessionId, CancellationToken ct = default);
    /// <summary>校验完成度后生成 Word（FR-5.15）：槽位回填、页眉待复核、后续阶段留空。</summary>
    Task<RenderResult> RenderAsync(Guid sessionId, CancellationToken ct = default);
    Task<(Stream Content, string FileName)> OpenOutputAsync(Guid sessionId, CancellationToken ct = default);
}

/// <summary>槽位运行时状态（整会话序列化存储，7.2）。来源优先级见 5.2.2。</summary>
public record SlotState(
    string Tag,
    string? Value,
    /// <summary>Template / Inherited / AiSuggested / Confirmed 之外的空值用 null 表达未填。</summary>
    SlotFillSource? Source,
    /// <summary>已确认。AI 建议未确认时按未完成计（FR-5.13）。</summary>
    bool Confirmed,
    /// <summary>用户动过（填写或确认）：更换基准时保留（FR-5.5）。</summary>
    bool UserTouched,
    /// <summary>出处：继承记「项目编号 · 文档/字段 · 章节」，建议记依据摘要（FR-5.6/5.9）。</summary>
    string? Origin,
    DateTimeOffset? UpdatedAt);

public record SlotPut(string? Value, bool Confirm,
    /// <summary>true=采纳当前建议值（value 忽略，用建议值）。</summary>
    bool AdoptSuggestion = false);

public record SessionView(
    Guid Id, Guid TemplateId, string TemplateName, string? ProjectHint, string? BaseProjectNo,
    GenerationStatus Status, IReadOnlyList<SlotView> Slots, DateTimeOffset UpdatedAt, string? OutputFileName);

public record SlotView(SlotDef Def, SlotState State);

public record SessionRow(Guid Id, string TemplateName, string? BaseProjectNo, GenerationStatus Status,
    int Total, int Done, DateTimeOffset UpdatedAt);

public record BaseCandidate(string ProjectNo, string CustomerName, int Year, string DeviceType,
    string? DeviceModel, string? SpecParams, string? DeliveryStatus, int InheritableSlots, int LinkedDocs);

public record SlotSuggestion(
    string Tag, string? Value,
    /// <summary>依据清单（FR-5.9）：不给无依据的建议。空值+HasEvidence=false 即「暂无可参考数据」。</summary>
    IReadOnlyList<SuggestEvidence> Evidence, bool HasEvidence, string? Note);

public record SuggestEvidence(string SourceTitle, string? Section, int? PageNo, string Excerpt, long? ChunkId);

public record CompletenessView(bool CanRender, int Total, int Done,
    IReadOnlyList<IncompleteSlot> Incomplete);

public record IncompleteSlot(string Tag, string Name, string Section, string Reason);

public record PreviewView(
    IReadOnlyList<PreviewSection> Sections,
    /// <summary>PDF 转换服务未配置时为 null，前端以标色 HTML 呈现（README 记有 FR-5.14 口径）。</summary>
    string? PdfFileKey);

public record PreviewSection(string Section, IReadOnlyList<PreviewItem> Items);

public record PreviewItem(string Tag, string Name, string? Value, string SourceLabel, string? Origin);

public record RenderResult(Guid SessionId, string OutputFileName, int SlotsFilled, int LeftBlank);

// ─────────────────────────── 对话式生成（FR-5.x 对话形态）───────────────────────────

public interface IGenerationChatService
{
    /// <summary>取会话的对话记录与进度（续聊/回放）。</summary>
    Task<GenChatStateView> GetAsync(Guid sessionId, CancellationToken ct = default);
    /// <summary>一轮对话：自由文本（模型抽取多槽位，演示档退化为逐项问答）、选项点选、
    /// 选定基准、生成文档，四类输入走同一入口，返回本轮新增消息与进度。</summary>
    Task<GenChatTurnResult> TurnAsync(Guid sessionId, GenChatTurnInput input, CancellationToken ct = default);
}

/// <summary>一轮输入：Start=补开场白（会话还没有消息时）；Message=自由文本（经总指挥派工）；
/// OptionFills=选项按钮直填（不过模型）；BaseProjectNo=选定基准（空串=明确不用）；
/// AdoptTags=采纳建议按钮（空表=全部待确认建议）；Render=生成文档。</summary>
public record GenChatTurnInput(string? Message = null, List<GenTagValue>? OptionFills = null,
    string? BaseProjectNo = null, bool Render = false, bool Start = false, List<string>? AdoptTags = null);

public record GenTagValue(string Tag, string Value);

public record GenChatMessageView(long Id, string Role, string Content, string? Payload, DateTimeOffset At);

public record GenChatProgress(int Total, int Done, bool CanRender, string? OutputFileName);

public record GenChatStateView(IReadOnlyList<GenChatMessageView> Messages, GenChatProgress Progress);

public record GenChatTurnResult(IReadOnlyList<GenChatMessageView> NewMessages, GenChatProgress Progress);

// ─────────────────────────── 条款库（FR-5.18）───────────────────────────

public interface IClauseService
{
    Task<IReadOnlyList<ClauseRow>> ListAsync(string? category, string? status, CancellationToken ct = default);
    Task<Guid> DraftAsync(ClauseEdit edit, CancellationToken ct = default);
    /// <summary>拟修改：已审定条款不可就地改，生成待审新版本，通过后替代旧版（旧版停用保留）。</summary>
    Task<Guid> ReviseAsync(Guid clauseId, ClauseEdit edit, CancellationToken ct = default);
    Task ApproveAsync(Guid clauseId, CancellationToken ct = default);
    Task RejectAsync(Guid clauseId, CancellationToken ct = default);
    /// <summary>拼装（表 8-1 /api/generate/contract）：仅取已审定条款，含任何非 Active 即拒。</summary>
    Task<ClauseAssembly> AssembleAsync(IReadOnlyList<Guid> clauseIds, CancellationToken ct = default);
}

public record ClauseRow(Guid Id, string Category, string Code, string Title, string Text, string Status,
    string? ApprovedByName, DateOnly? EffectiveDate, Guid? SupersedesId, DateTimeOffset CreatedAt);

public record ClauseEdit(string Category, string Code, string Title, string Text);

public record ClauseAssembly(IReadOnlyList<ClauseRow> Clauses, string AssembledText);
