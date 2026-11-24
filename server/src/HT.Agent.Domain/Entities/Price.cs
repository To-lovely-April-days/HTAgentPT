namespace HT.Agent.Domain.Entities;

/// <summary>价格库里的一张价格表（如「原材料单价表」）。同一张表可以反复上传新版本，
/// 当前生效的是 CurrentVersionId；列的对应关系记在 ColumnMap，下次上传新版本时沿用。</summary>
public class PriceTable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>导入哪个工作表（空 = 第一个）。</summary>
    public string? SheetName { get; set; }
    /// <summary>列对应关系（JSON：[{col, header, field, supplier}]）。</summary>
    public string ColumnMap { get; set; } = "[]";
    public Guid? CurrentVersionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>价格表的一个版本：一次上传。旧版本保留，用来看价格历史。</summary>
public class PriceVersion
{
    public Guid Id { get; set; }
    public Guid TableId { get; set; }
    public string FileName { get; set; } = "";
    public string FileKey { get; set; } = "";
    /// <summary>这版价格的日期（文件名里能认出来就用它，否则用上传当天；上传时可改）。</summary>
    public DateOnly VersionDate { get; set; }
    public Guid UploadedById { get; set; }
    public string UploadedByName { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
    public int ItemCount { get; set; }
    /// <summary>导入时发现的问题（JSON）。</summary>
    public string Issues { get; set; } = "[]";
    /// <summary>和上一版比的结果（JSON）。</summary>
    public string Diff { get; set; } = "{}";
}

/// <summary>价格库里的一条价格：一个物料在一家供应商的一个价。</summary>
public class PriceItem
{
    public long Id { get; set; }
    public Guid TableId { get; set; }
    public Guid VersionId { get; set; }
    /// <summary>原表里的行号（Excel 的行号，从 1 起）。</summary>
    public int RowNo { get; set; }
    /// <summary>同一物料在不同版本里的识别键（名称 + 规格 + 供应商 + 型号，归一化后）。</summary>
    public string Key { get; set; } = "";
    public string? Group { get; set; }
    public string Name { get; set; } = "";
    public string? Spec { get; set; }
    public string? Material { get; set; }
    public string? Supplier { get; set; }
    public string? Model { get; set; }
    public string? Code { get; set; }
    /// <summary>数量单位（米、个…）。</summary>
    public string? Unit { get; set; }
    /// <summary>计价单位（元/kg、元/米…）。</summary>
    public string? PriceUnit { get; set; }
    public decimal? TaxLow { get; set; }
    public decimal? TaxHigh { get; set; }
    public decimal? NetLow { get; set; }
    public decimal? NetHigh { get; set; }
    /// <summary>含税价是按未税价推算出来的。</summary>
    public bool TaxDerived { get; set; }
    public string? LeadTime { get; set; }
    public string? Frequency { get; set; }
    public string? Note { get; set; }
    /// <summary>这条价格的日期：采购记录里每行的采购日期（价格表没有日期列的为空，按版本日期算）。</summary>
    public DateOnly? PriceDate { get; set; }
    /// <summary>搜索用：各字段小写拼起来。</summary>
    public string SearchText { get; set; } = "";
}
