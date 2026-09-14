namespace HT.Agent.Domain.Entities;

/// <summary>项目台账（表 4-6）。以项目编号为主键；金额等字段单独标记机密，查询时按角色裁剪返回列（表 7-1）。</summary>
public class Project
{
    public required string ProjectNo { get; set; }
    public Guid CompanyId { get; set; }
    public required string CustomerName { get; set; }
    public int Year { get; set; }
    public required string DeviceType { get; set; }
    public string? DeviceModel { get; set; }
    /// <summary>规模参数：容积、通道数、功率等关键规格。</summary>
    public string? SpecParams { get; set; }
    /// <summary>合同金额，属机密密级，仅售前可见（3.4 第五条：字段级裁剪，不是行级）。</summary>
    public decimal? ContractAmount { get; set; }
    public DeliveryStatus? DeliveryStatus { get; set; }
    public Guid? OwnerId { get; set; }
    public string? DocPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>故障案例（表 7-1）。录入后本公司立即可检索，不依赖审核（FR-8.2）。</summary>
public class FaultCase
{
    public Guid Id { get; set; }
    public required string CaseNo { get; set; }
    public Guid CompanyId { get; set; }
    public required string DeviceModel { get; set; }
    public string? AlarmCode { get; set; }
    public required string Phenomenon { get; set; }
    public required string CauseAnalysis { get; set; }
    public required string Steps { get; set; }
    public string? SpareParts { get; set; }
    public required string Result { get; set; }
    /// <summary>可配置的扩展表单字段（FR-8.1：表单字段可配置），jsonb。</summary>
    public string? Extra { get; set; }
    public CaseSyncStatus SyncStatus { get; set; } = CaseSyncStatus.Local;
    /// <summary>案例以固定模板渲染为一段文本写入私有库的分块；修改时同步更新（FR-8.2）。</summary>
    public long? ChunkId { get; set; }
    /// <summary>回流入库时标注来源公司与录入时间（FR-8.5）。</summary>
    public string? SourceCompany { get; set; }
    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    /// <summary>驳回须填写原因并回传至提交人（FR-8.4）。</summary>
    public string? RejectReason { get; set; }
}

/// <summary>报修工单（FR-8.8）：本期仅最简流转，不含派工调度与备件管理。</summary>
public class Ticket
{
    public Guid Id { get; set; }
    public required string TicketNo { get; set; }
    public required string CustomerNo { get; set; }
    public required string DeviceNo { get; set; }
    public required string Description { get; set; }
    public required string Contact { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.Submitted;
    public Guid? AssigneeId { get; set; }
    /// <summary>状态更新轨迹（jsonb 数组），客户可查本人工单进度。</summary>
    public string? Updates { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>客户设备（客户入口·我的设备）：设备与客户编号绑定。</summary>
public class CustomerDevice
{
    public Guid Id { get; set; }
    public required string CustomerNo { get; set; }
    public required string DeviceNo { get; set; }
    public required string Model { get; set; }
    public string? ProjectNo { get; set; }
    public DateOnly? DeliveredAt { get; set; }
}

/// <summary>术语（表 7-1）：中英对照，按领域分类，供翻译与查询改写使用。</summary>
public class Term
{
    public Guid Id { get; set; }
    public required string Domain { get; set; }
    public required string Zh { get; set; }
    public required string En { get; set; }
    public string? Note { get; set; }
    /// <summary>翻译中提交的补充须经管理员确认后生效（FR-6.4）。</summary>
    public TermStatus Status { get; set; } = TermStatus.Approved;
    public Guid? SubmittedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>合同条款（表 7-1）：仅收录已审定条款；记录审定人与生效日期。
/// 已审定条款不可就地编辑，修改生成待审新版本（FR-5.18）。</summary>
public class Clause
{
    public Guid Id { get; set; }
    public required string Category { get; set; }
    public required string Code { get; set; }
    public required string Title { get; set; }
    public required string Text { get; set; }
    public ClauseStatus Status { get; set; } = ClauseStatus.Draft;
    public Guid? ApprovedBy { get; set; }
    public DateOnly? EffectiveDate { get; set; }
    /// <summary>拟修改：新版本指向被替代的旧版本，通过后旧版停用但保留。</summary>
    public Guid? SupersedesId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
