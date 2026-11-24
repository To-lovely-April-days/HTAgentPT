using HT.Agent.Application.Logic;

namespace HT.Agent.Application.Abstractions;

public record PriceTableRow(Guid Id, string Name, string? Description, int ItemCount, DateOnly? VersionDate, int AgeMonths, bool Stale,
    string? UploadedBy, DateTimeOffset? UploadedAt, int Versions, List<PriceIssue> Issues);

public record PriceOverview(List<PriceTableRow> Tables, int TotalItems, int Queries30d, int StaleMonths, List<string> Materials, List<string> Suppliers);

public record PriceUploadResult(string FileKey, string FileName, List<string> Sheets);

public record PricePreviewRequest(string FileKey, string FileName, Guid? TableId, string? Sheet, List<ColumnMap>? Map, string? TableName);

public record PricePreview(List<string> Sheets, string Sheet, List<string> IncludedSheets, int HeaderRowNo, List<ColumnMap> Map, List<ParsedPrice> Sample, int ItemCount,
    List<PriceIssue> Issues, PriceDiff? Diff, DateOnly SuggestedDate, Guid? TableId, string TableName);

public record PriceCommitRequest(string FileKey, string FileName, Guid? TableId, string? TableName, string? Description, string? Sheet,
    List<ColumnMap> Map, DateOnly VersionDate);

/// <summary>查到的一条价格。采购记录里的：PriceDate 是采购日期（给的是最近一次），Purchases / MinTax / MaxTax / FirstDate 是同一物料、
/// 同一供应商在所有采购记录里的次数、含税最低最高价、最早一次的日期；ChangePct 是最近一次比上一次涨跌了多少。</summary>
public record PriceHit(long Id, Guid TableId, string TableName, DateOnly VersionDate, bool Stale, int RowNo, string? Group, string Name, string? Spec,
    string? Material, string? Supplier, string? Model, string? Code, string? Unit, string? PriceUnit, decimal? TaxLow, decimal? TaxHigh,
    decimal? NetLow, decimal? NetHigh, bool TaxDerived, string? LeadTime, string? Note, double? ChangePct, int Versions,
    DateOnly? PriceDate = null, int Purchases = 0, decimal? MinTax = null, decimal? MaxTax = null, DateOnly? FirstDate = null);

public record PricePoint(DateOnly VersionDate, decimal? TaxLow, decimal? TaxHigh, string FileName);

/// <summary>价格库。看要「查看采购价格」权限，上传和改要「管理价格库」权限；服务内部校验。</summary>
public interface IPriceService
{
    Task<PriceOverview> OverviewAsync(CancellationToken ct = default);
    Task<PriceUploadResult> UploadAsync(Stream content, string fileName, CancellationToken ct = default);
    Task<PricePreview> PreviewAsync(PricePreviewRequest req, CancellationToken ct = default);
    Task<Guid> CommitAsync(PriceCommitRequest req, CancellationToken ct = default);
    Task<List<PriceHit>> SearchAsync(string? query, Guid? tableId, string? material, string? supplier, int take, CancellationToken ct = default);
    Task<List<PriceHit>> GetAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);
    Task<List<PricePoint>> HistoryAsync(long itemId, CancellationToken ct = default);
    Task DeleteTableAsync(Guid tableId, CancellationToken ct = default);
    bool CanView { get; }
}
