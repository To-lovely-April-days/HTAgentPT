namespace HT.Agent.Application.Abstractions;

/// <summary>中英翻译（M6）。模型只做语言转换；术语一致性由术语表强制（FR-6.1）。</summary>
public interface ITranslationService
{
    Task<TextTranslationResult> TranslateTextAsync(TextTranslationRequest req, CancellationToken ct = default);
    /// <summary>docx 整篇翻译并格式回填（FR-6.3）。返回任务号与回填报告，文件另行下载。</summary>
    Task<FileTranslationResult> TranslateDocxAsync(Stream file, string fileName, string direction, string? termDomain, CancellationToken ct = default);
    Task<(Stream Content, string FileName)> OpenOutputAsync(Guid taskId, CancellationToken ct = default);
}

/// <summary>direction：zh2en / en2zh。domain 限定术语领域，空则全部已审定术语参与。</summary>
public record TextTranslationRequest(string Text, string Direction, string? TermDomain = null,
    /// <summary>是否输出双语对照（FR-6.5）。</summary>
    bool Bilingual = false);

public record TextTranslationResult(
    string Translation,
    /// <summary>双语对照段（FR-6.5）：源段与译段成对。Bilingual=false 时为空。</summary>
    IReadOnlyList<BilingualPair> Pairs,
    /// <summary>命中并强制注入的术语（FR-6.1），界面可标记「译法不当」走提交（FR-6.4）。</summary>
    IReadOnlyList<TermHit> TermsApplied,
    /// <summary>合同类提示（FR-6.6）：识别为合同文本时非空，法律文本须经人工复核。</summary>
    string? ContractNotice);

public record BilingualPair(string Source, string Target);
public record TermHit(string Zh, string En, string Domain);

public record FileTranslationResult(
    Guid TaskId,
    string OutputFileName,
    /// <summary>回填报告：总段数、译出段数、无法回填的元素（FR-6.3 末句：必须列出，不许静默）。</summary>
    DocxReport Report,
    IReadOnlyList<TermHit> TermsApplied,
    string? ContractNotice);

public record DocxReport(int Paragraphs, int Translated, IReadOnlyList<string> Unfillable);

/// <summary>术语表维护（FR-3.5、FR-6.4）。</summary>
public interface ITermService
{
    Task<IReadOnlyList<TermRow>> ListAsync(string? domain, string? status, CancellationToken ct = default);
    /// <summary>管理员直接新增（即时生效）。</summary>
    Task<Guid> AddAsync(TermEdit edit, CancellationToken ct = default);
    /// <summary>批量导入（FR-3.5）：重复对照跳过，返回导入数。</summary>
    Task<int> ImportAsync(IReadOnlyList<TermEdit> rows, CancellationToken ct = default);
    /// <summary>翻译中提交的补充，待管理员确认（FR-6.4）。</summary>
    Task<Guid> SuggestAsync(TermEdit edit, CancellationToken ct = default);
    Task ApproveAsync(Guid termId, CancellationToken ct = default);
    Task RejectAsync(Guid termId, CancellationToken ct = default);
}

public record TermRow(Guid Id, string Domain, string Zh, string En, string? Note, string Status,
    Guid? SubmittedBy, DateTimeOffset CreatedAt);
public record TermEdit(string Domain, string Zh, string En, string? Note = null);
