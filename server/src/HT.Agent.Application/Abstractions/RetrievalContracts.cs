using HT.Agent.Domain;

namespace HT.Agent.Application.Abstractions;

/// <summary>检索服务（M4 第 3 步）：混合检索与权限过滤在同一次数据库查询内完成（FR-4.3/4.4）。</summary>
public interface IRetrievalService
{
    Task<RetrievalResult> RetrieveAsync(RetrievalRequest req, CancellationToken ct = default);
}

public record RetrievalRequest(
    string Query,
    /// <summary>限定知识库范围；空为该用户可及的全部库。</summary>
    IReadOnlyList<Guid>? KbIds = null,
    /// <summary>元数据筛选（FR-4.5）：客户、年份、设备类型，与检索条件联合生效。</summary>
    string? CustomerName = null,
    int? Year = null,
    string? DeviceType = null,
    string? DocCategory = null);

public record RetrievalResult(
    bool AboveThreshold,
    IReadOnlyList<RetrievedChunk> Chunks,
    /// <summary>低于阈值时给出的可能相关文档名（FR-4.7）。</summary>
    IReadOnlyList<string> PossiblyRelatedDocs,
    double TopScore,
    string RewrittenQuery,
    /// <summary>降级说明（10.3：明确提示而非静默）。如「向量服务不可用，本轮仅按关键词检索」。空为正常。</summary>
    string? Notice = null);

public record RetrievedChunk(
    long ChunkId, Guid DocId, string DocTitle, string? SectionPath, int? PageNo,
    string Text, double FusedScore, double RerankScore, Classification Classification,
    string? ProjectNo = null);

/// <summary>问答服务（FR-4.8/4.9/4.12）。</summary>
public interface IQaService
{
    /// <summary>流式问答。事件流：meta（改写与命中）、delta（增量文本）、sources、done / no_result。</summary>
    IAsyncEnumerable<QaEvent> AskStreamAsync(QaRequest req, CancellationToken ct = default);
}

public record QaRequest(Guid? SessionId, string Question, RetrievalRequest Retrieval,
    /// <summary>用户手动纠正的意图（FR-4.1）。空则由路由器判定。</summary>
    string? ForcedIntent = null,
    /// <summary>随这句话一起发出的附件（已上传、还没绑到对话上的）。绑定后这条对话后面的提问都能用。</summary>
    IReadOnlyList<Guid>? AttachmentIds = null,
    /// <summary>后台「试一试」：只开这一个技能（草稿也行），或者一个技能都不开。只有能管理技能的人能用。</summary>
    SkillTest? SkillTest = null,
    /// <summary>思考档位：auto / fast / standard / deep（空=自动）。员工不能自己选时以后台默认为准。</summary>
    string? Level = null,
    /// <summary>跑测试题时按指定档位跑，不管员工能不能自己选。</summary>
    bool LevelForced = false,
    /// <summary>用户在右边面板里开着的网页（内置浏览器的标签号）：助手自动读它。</summary>
    string? BrowserTab = null,
    /// <summary>新对话开在哪个项目里（已有对话按它自己的项目）。</summary>
    Guid? ProjectId = null,
    /// <summary>用户在「＋ → 用技能」里点名的技能代号：这一问先按它答。</summary>
    string? Skill = null);

/// <summary>试技能：Code 是技能代号；On=true 只开这一个并要求先翻看，On=false 不开任何技能。</summary>
public record SkillTest(string Code, bool On);

/// <summary>SSE 事件。Kind: meta | progress | delta | sources | no_result | done | error。</summary>
public record QaEvent(string Kind, object Payload);
