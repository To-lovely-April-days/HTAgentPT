namespace HT.Agent.Domain.Entities;

/// <summary>审计日志（FR-9.1）：只写不改，按月分区（7.2），留存不少于十二个月。</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? UserId { get; set; }
    public required string Username { get; set; }
    public Guid? CompanyId { get; set; }
    /// <summary>操作键，如 auth.login / doc.upload / qa.ask / user.reset_password。</summary>
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    /// <summary>结构化细节（jsonb）。问答留痕含提问、改写、命中分块与回答摘要（FR-4.12）。</summary>
    public string? Detail { get; set; }
    public AuditResult Result { get; set; }
    public string? Ip { get; set; }
    public string? TerminalId { get; set; }
}

/// <summary>同步记录（表 7-1 sync_log）：共享库同步与案例回流的批次与结果。</summary>
public class SyncLog
{
    public Guid Id { get; set; }
    /// <summary>shared_pull / case_push / public_publish。</summary>
    public required string Kind { get; set; }
    public required string BatchNo { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool? Ok { get; set; }
    public string? Detail { get; set; }
}

/// <summary>运行参数与外部组件地址（FR-9.5/9.6、10.4）：一律入库配置，界面可改，改后即时生效。</summary>
public class SysConfig
{
    public required string Key { get; set; }
    /// <summary>JSON 值。</summary>
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>备份运行记录（FR-9.3/9.4）：失败须告警，不得仅写日志；界面显示最近一次时间、结果与大小。</summary>
public class BackupRun
{
    public Guid Id { get; set; }
    public BackupKind Kind { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool? Ok { get; set; }
    public long? SizeBytes { get; set; }
    public string? Target { get; set; }
    public string? Error { get; set; }
    /// <summary>告警知悉记录：知悉只入审计，不撤告警（E0 语义）。</summary>
    public Guid? AcknowledgedBy { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
}
