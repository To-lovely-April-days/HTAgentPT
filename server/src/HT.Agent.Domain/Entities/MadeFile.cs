namespace HT.Agent.Domain.Entities;

/// <summary>助手做的文件（Excel、Word、PPT、PDF）：模型在计算沙箱里写代码做出来，检查过了才给员工下载。
/// 没检查过的也记一条（Ok=false、不存文件），后台看每种文件的失败率。过了保留期删文件。</summary>
public class MadeFile
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    /// <summary>做出这份文件的那条回答。</summary>
    public Guid? MessageId { get; set; }
    public required string FileName { get; set; }
    /// <summary>xlsx / docx / pptx / pdf / csv。</summary>
    public required string Kind { get; set; }
    public long Size { get; set; }
    /// <summary>存储里的键；检查没过、或已过保留期删掉了的为空。</summary>
    public string? FileKey { get; set; }
    public bool Ok { get; set; }
    /// <summary>检查发现的问题，一行一条。</summary>
    public string? Problems { get; set; }
    /// <summary>jsonb：检查给的信息（工作表数、公式数、修订几处、页数……）。</summary>
    public string? Info { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
