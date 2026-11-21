namespace HT.Agent.Domain.Entities;

/// <summary>一条「答得不好」的记录：员工点了「没帮上」，或者系统自己发现（查了资料一条没用上、数字核对不上）。
/// 管理员在「回答质量」里看当时怎么查的、错在哪一步，修完标已处理；提问人要求的话，处理完告诉他。</summary>
public class AnswerIssue
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid MessageId { get; set; }
    public QaMessage? Message { get; set; }
    public IssueSource Source { get; set; }
    /// <summary>员工选的「哪里不对」：no_docs / wrong_docs / wrong / incomplete / other。系统发现的为空。</summary>
    public string? Reason { get; set; }
    /// <summary>员工补的一句：正确的是什么、应该在哪份资料里。</summary>
    public string? Comment { get; set; }
    /// <summary>处理完要不要告诉提问人。</summary>
    public bool Notify { get; set; }
    public Guid? ReporterId { get; set; }
    public IssueStatus Status { get; set; } = IssueStatus.Open;
    /// <summary>管理员判断的「错在哪一步」：missing / parse / alias / crowded / model / not_issue。</summary>
    public string? Category { get; set; }
    /// <summary>处理说明，告诉提问人时用。</summary>
    public string? ResolveNote { get; set; }
    public Guid? ResolvedById { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    /// <summary>提问人看过「处理好了」的提示。</summary>
    public DateTimeOffset? SeenAt { get; set; }
    public Guid? EvalQuestionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum IssueSource { Feedback, NoEvidence, Unverified }

public enum IssueStatus { Open, Resolved, Dismissed }

/// <summary>测试题：以后每次调整都拿这些题问一遍，看变好还是变差。题目里可能有客户信息，只存在公司服务器上。</summary>
public class EvalQuestion
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    /// <summary>题号（Q001…），比较两次结果时靠它对齐。</summary>
    public required string Code { get; set; }
    public required string Question { get; set; }
    /// <summary>应该查到的资料（文件名或标题里的一段文字）。</summary>
    public List<string> ExpectDocs { get; set; } = [];
    public List<string> ExpectProjects { get; set; } = [];
    /// <summary>答案要点；一条里用 | 隔开表示「任意一种说法都算」。</summary>
    public List<string> ExpectPoints { get; set; } = [];
    /// <summary>这题应该先问清楚再答。</summary>
    public bool ExpectAsk { get; set; }
    public string? Dept { get; set; }
    /// <summary>issue：从错题存的；import：批量导入；manual：手动加。</summary>
    public required string Source { get; set; }
    public Guid? IssueId { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>跑一遍测试题。Config 记下当时的检索配置和资料、别名数量，和上一次比就知道中间改了什么。</summary>
public class EvalRun
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public EvalRunStatus Status { get; set; } = EvalRunStatus.Running;
    public int Total { get; set; }
    public int Finished { get; set; }
    /// <summary>有「应该查到的资料/项目」的题数，和其中查到了的题数。</summary>
    public int RetrievalTotal { get; set; }
    public int RetrievalOk { get; set; }
    /// <summary>有「答案要点」的题数，和其中要点都说到了的题数。</summary>
    public int AnswerTotal { get; set; }
    public int AnswerOk { get; set; }
    /// <summary>用哪个账号问的。</summary>
    public required string RunAs { get; set; }
    /// <summary>jsonb：当时的配置和计数快照。</summary>
    public string? Config { get; set; }
    /// <summary>和上一次比改了什么，一行一项。</summary>
    public string? Changes { get; set; }
    public string? Error { get; set; }
    public Guid StartedById { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public enum EvalRunStatus { Running, Done, Failed }

/// <summary>一题的结果。</summary>
public class EvalResult
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public Guid QuestionId { get; set; }
    /// <summary>应该查到的都查到了；题目没写要查什么时为空。</summary>
    public bool? RetrievalOk { get; set; }
    /// <summary>要点都说到了（且该问的问了）；题目没写要点时为空。</summary>
    public bool? AnswerOk { get; set; }
    public bool AskOk { get; set; } = true;
    public List<string> MissingPoints { get; set; } = [];
    public List<string> FoundDocs { get; set; } = [];
    public string? Answer { get; set; }
    public string? Error { get; set; }
    public double Seconds { get; set; }
}
