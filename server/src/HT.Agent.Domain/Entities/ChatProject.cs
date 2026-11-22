namespace HT.Agent.Domain.Entities;

/// <summary>对话的「项目」：把相关的对话归在一起，带项目说明（给助手看的背景）、项目资料（只在这个项目里用的文件），
/// 可以关联台账的项目号（助手直接知道客户、设备、交付情况）。第一版只有建的人自己能看。</summary>
public class ChatProject
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    /// <summary>项目说明：每次在这个项目里提问，助手都先看这段。</summary>
    public string? Description { get; set; }
    /// <summary>关联的台账项目号（可空）。只读引用，不改台账。</summary>
    public string? LedgerNo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>归档了就不在侧栏里显示；对话和资料都还在。</summary>
    public DateTimeOffset? ArchivedAt { get; set; }
}
