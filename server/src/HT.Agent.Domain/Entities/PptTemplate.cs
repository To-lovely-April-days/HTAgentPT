namespace HT.Agent.Domain.Entities;

/// <summary>PPT 模板库的一套模板。系统自带的（deploy/ppt-templates，开源许可）第一次用时导入；管理员也可以上传，上传要写来源和许可。
/// 入库时在沙箱里分析每页是什么类型（封面、目录、要点、几栏、大数字、表格、图文、结尾）、转每页缩略图。全公司共用。</summary>
public class PptTemplate
{
    public Guid Id { get; set; }
    /// <summary>系统自带的用文件名（不含 .pptx），上传的用随机编号。</summary>
    public required string Slug { get; set; }
    public required string Name { get; set; }
    /// <summary>风格标签，用「,」隔开：商务、科技、工业、简约、深色……</summary>
    public string Tags { get; set; } = "";
    public bool BuiltIn { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>从哪来的（网址）。</summary>
    public string Source { get; set; } = "";
    /// <summary>许可：CC BY 4.0、CC BY-SA 4.0、CC0、MIT、Apache-2.0、公司自有……</summary>
    public string License { get; set; } = "";
    /// <summary>做出来的 PPT 最后一页要写的署名（CC BY 要求），没有就不写。</summary>
    public string? Credit { get; set; }
    public string? FileKey { get; set; }
    public long Size { get; set; }
    public int Slides { get; set; }
    /// <summary>认出的页面类型，用「,」隔开。</summary>
    public string PageTypes { get; set; } = "";
    /// <summary>jsonb：每页的分析结果（htppt.analyze 的输出）。</summary>
    public string? Analysis { get; set; }
    /// <summary>转出来的缩略图有几页（存储里 ppt-templates/{Id:N}/p{n}.jpg）。</summary>
    public int Previews { get; set; }
    /// <summary>ready / failed。</summary>
    public string Status { get; set; } = "ready";
    public string? Problem { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedById { get; set; }
}
