namespace HT.Agent.Domain.Entities;

/// <summary>技能：给助手的专家答题指南（这类问题先确认什么、怎么判断、常见的坑、该查什么、回答怎么写）。
/// 格式沿用开放标准 Agent Skills（SKILL.md：代号、什么时候用、正文）。模型平时只看到「名称 + 什么时候用」一行，
/// 问题对上了用 use_skill 翻看全文。内容是公司技术骨干维护的数据，不写在代码里。</summary>
public class AssistantSkill
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    /// <summary>英文代号（SKILL.md 的 name），导出时做文件夹名。同一公司内唯一。</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    /// <summary>分类：售前与客户 / 设计与标准 / 图纸与文档 / 售后与运维 / 安全与化学……</summary>
    public required string Category { get; set; }
    /// <summary>什么时候用（SKILL.md 的 description）。模型只凭这一句决定要不要翻看。</summary>
    public required string Description { get; set; }
    /// <summary>正文（Markdown）。</summary>
    public string Body { get; set; } = "";
    /// <summary>own 自写 / adapted 改编开源 / library 用开源计算库。</summary>
    public string Source { get; set; } = "own";
    /// <summary>导入的开源技能的许可证（SKILL.md 里的 license）。</summary>
    public string? License { get; set; }
    public SkillStatus Status { get; set; } = SkillStatus.Draft;
    /// <summary>给哪些角色用（角色代码）；空 = 所有内部员工。</summary>
    public List<string> Roles { get; set; } = [];
    /// <summary>附带的参考资料（知识库文档）。</summary>
    public List<Guid> RefDocIds { get; set; } = [];
    /// <summary>出不出现在聊天「＋ → 用技能」清单里。关掉的照样启用——助手碰到相关问题自己翻看，只是不占清单
    /// （像「关于霍桐仪器」这种问一句就会自动用上的，没必要让人去点）。</summary>
    public bool InMenu { get; set; } = true;
    public int Version { get; set; } = 1;
    public Guid? UpdatedById { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? ReviewedById { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum SkillStatus { Draft, Review, Enabled }

/// <summary>技能的每一版，保存时留底，可回看、可退回。</summary>
public class AssistantSkillVersion
{
    public long Id { get; set; }
    public Guid SkillId { get; set; }
    public int Version { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public string Body { get; set; } = "";
    public string? Note { get; set; }
    public Guid? EditedById { get; set; }
    public DateTimeOffset EditedAt { get; set; }
}

/// <summary>一次「翻看技能」：哪条回答用了哪个技能哪一版。用来统计使用次数和「用了它的回答被点没帮上」。</summary>
public class SkillUse
{
    public long Id { get; set; }
    public Guid SkillId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset At { get; set; }
}
