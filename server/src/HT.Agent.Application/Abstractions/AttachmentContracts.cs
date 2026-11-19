using HT.Agent.Domain;

namespace HT.Agent.Application.Abstractions;

/// <summary>对话附件（对话里上传的文件）：只在这条对话里当资料用，不进知识库，密级按内部。</summary>
public interface IChatAttachmentService
{
    /// <summary>上传并排队解析。限制：单个文件不超过 <see cref="AttachmentLimits.MaxBytes"/>，
    /// 未发出的附件不超过 <see cref="AttachmentLimits.MaxPerMessage"/> 个，格式见 <see cref="AttachmentLimits.Extensions"/>。</summary>
    Task<ChatAttachmentView> UploadAsync(Stream content, string fileName, string contentType, long size, CancellationToken ct = default);
    Task<ChatAttachmentView?> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>本人的附件原件（在预览面板里直接翻看）。不是本人的返回 null。</summary>
    Task<(Stream Content, string FileName, string ContentType)?> OpenAsync(Guid id, CancellationToken ct = default);
    /// <summary>撤回还没发出的附件（已绑到对话上的随对话保存，不在这里删）。</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ChatAttachmentView>> ListForSessionAsync(Guid qaSessionId, CancellationToken ct = default);
    /// <summary>把这句话带的附件绑到对话上，返回新绑上的个数。别人的、已绑到别的对话上的一律不认。</summary>
    Task<int> BindAsync(Guid qaSessionId, IReadOnlyList<Guid> ids, CancellationToken ct = default);
}

public record ChatAttachmentView(Guid Id, string FileName, long Size, ParseStatus Status, string? Error,
    int CharCount, Guid? QaSessionId, DateTimeOffset CreatedAt);

public static class AttachmentLimits
{
    public const long MaxBytes = 20L * 1024 * 1024;
    public const int MaxPerMessage = 5;
    /// <summary>Office 走本地解析，PDF 走 MinerU（扫描件也能出文字）。老格式（.doc/.xls）读不了，请另存为新格式。</summary>
    public static readonly string[] Extensions = [".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pdf"];
}
