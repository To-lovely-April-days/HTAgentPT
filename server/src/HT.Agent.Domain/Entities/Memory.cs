namespace HT.Agent.Domain.Entities;

/// <summary>助手的长期记忆：用户明确交代的事（偏好、工作范围、在跟的客户），之后的对话自动带上。
/// 只有本人看得到内容；管理员只看条数。不存密码、联系方式等敏感内容（写入前校验）。</summary>
public class UserMemory
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>偏好 / 工作 / 客户</summary>
    public required string Kind { get; set; }
    public required string Text { get; set; }
    /// <summary>chat：对话里助手记下的；manual：本人在个人中心加的。</summary>
    public required string Source { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
