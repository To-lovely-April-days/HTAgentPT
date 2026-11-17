namespace HT.Agent.Domain.Entities;

/// <summary>部门（组织架构树的一个节点）。公司 → 部门 → 下级部门 → 人员。
/// 功能权限仍按角色给；部门只决定「谁审、谁管」：员工上传的文件和新增客户申请交给所在部门的主管，
/// 本部门没设主管就往上找，到顶都没有就交系统管理员。</summary>
public class Department
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? HeadUserId { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>一份文档除主资料库（Document.KbId）以外还挂在哪些库里。
/// 一份文件只存一份、只解析一次；检索时按这张表认它也属于这些库。</summary>
public class DocumentKb
{
    public Guid DocumentId { get; set; }
    public Guid KbId { get; set; }
}

public enum CustomerRequestStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>新增客户申请：员工上传时客户名单里没有这个客户，提给部门主管确认；通过后进客户名单。</summary>
public class CustomerRequest
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public string? Reason { get; set; }
    public Guid RequestedById { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public CustomerRequestStatus Status { get; set; } = CustomerRequestStatus.Pending;
    /// <summary>主管改名后通过时的最终名称；否则同 Name。</summary>
    public string? FinalName { get; set; }
    public string? ReviewNote { get; set; }
    public Guid? ReviewedById { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
}
