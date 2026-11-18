using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace HT.Agent.Infrastructure.Services;

/// <summary>价格库：导入价格表（认列、找问题、和上一版比）、查价格、看价格历史。
/// 看要 price.view，导入和删除要 price.manage；全部按公司隔离。</summary>
public class PriceService(AppDbContext db, IFileStorage storage, IRuntimeConfig config, IAuditWriter audit, ICurrentUser me, IMemoryCache cache)
    : IPriceService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool CanView => me.Permissions.Contains(PermissionKeys.PriceView) || me.Permissions.Contains(PermissionKeys.PriceManage);

    private void RequireView()
    {
        if (!CanView) throw new ForbiddenException("PRICE_FORBIDDEN", "没有查看采购价格的权限");
    }

    private void RequireManage()
    {
        if (!me.Permissions.Contains(PermissionKeys.PriceManage)) throw new ForbiddenException("PRICE_FORBIDDEN", "没有管理价格库的权限");
    }

    private Task Log(string action, object detail, string? target, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success, UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "price_table", TargetId: target, Detail: detail), ct);

    private async Task<int> StaleMonthsAsync(CancellationToken ct) => Math.Clamp(await config.GetIntAsync(ConfigKeys.PriceStaleMonths, 12, ct), 1, 120);

    private async Task<decimal> TaxRateAsync(CancellationToken ct)
        => decimal.TryParse(await config.GetStringAsync(ConfigKeys.PriceTaxRate, "1.13", ct), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r is >= 1 and < 2 ? r : 1.13m;

    private static int AgeMonths(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return Math.Max(0, (today.Year - d.Year) * 12 + today.Month - d.Month - (today.Day < d.Day ? 1 : 0));
    }

    private IQueryable<PriceTable> Tables => db.PriceTables.Where(t => t.CompanyId == me.CompanyId);

    // ── 总览 ──────────────────────────────────────────

    public async Task<PriceOverview> OverviewAsync(CancellationToken ct = default)
    {
        RequireView();
        var stale = await StaleMonthsAsync(ct);
        var tables = await Tables.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync(ct);
        var ids = tables.Select(t => t.Id).ToList();
        var versions = await db.PriceVersions.AsNoTracking().Where(v => ids.Contains(v.TableId)).ToListAsync(ct);
        var rows = new List<PriceTableRow>();
        foreach (var t in tables)
        {
            var cur = versions.FirstOrDefault(v => v.Id == t.CurrentVersionId);
            var age = cur is null ? 0 : AgeMonths(cur.VersionDate);
            rows.Add(new PriceTableRow(t.Id, t.Name, t.Description, cur?.ItemCount ?? 0, cur?.VersionDate, age, cur is not null && age >= stale,
                cur?.UploadedByName, cur?.UploadedAt, versions.Count(v => v.TableId == t.Id),
                cur is null ? [] : JsonSerializer.Deserialize<List<PriceIssue>>(cur.Issues, Json) ?? []));
        }
        var curIds = tables.Where(t => t.CurrentVersionId != null).Select(t => t.CurrentVersionId!.Value).ToList();
        var cur30 = DateTimeOffset.UtcNow.AddDays(-30);
        // detail 是 jsonb：按其中的 tool 字段数，不能对整列做字符串匹配
        var companyId = me.CompanyId;
        var queries = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM audit_log
            WHERE at >= {cur30} AND company_id = {companyId} AND action = 'qa.tool'
              AND detail->>'tool' IN ('search_prices', 'cost_estimate')
            """).SingleAsync(ct);
        var materials = await db.PriceItems.AsNoTracking().Where(i => curIds.Contains(i.VersionId) && i.Material != null)
            .Select(i => i.Material!).Distinct().OrderBy(x => x).Take(60).ToListAsync(ct);
        var suppliers = await db.PriceItems.AsNoTracking().Where(i => curIds.Contains(i.VersionId) && i.Supplier != null)
            .Select(i => i.Supplier!).Distinct().OrderBy(x => x).Take(60).ToListAsync(ct);
        return new PriceOverview(rows, rows.Sum(r => r.ItemCount), queries, stale, materials, suppliers);
    }

    // ── 导入 ──────────────────────────────────────────

    private string StageKey(string fileKey) => $"price-stage:{me.UserId}:{fileKey}";

    public async Task<PriceUploadResult> UploadAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        RequireManage();
        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new DomainRuleException("PRICE_BAD_FILE", "只支持 .xlsx；老的 .xls 请用 Excel 另存为 .xlsx 再上传");
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        if (ms.Length > 30 * 1024 * 1024) throw new DomainRuleException("PRICE_TOO_LARGE", "文件太大（超过 30MB）");
        ms.Position = 0;
        var sheets = PriceSheetReader.Read(ms);
        if (sheets.All(s => s.Rows.Count == 0)) throw new DomainRuleException("PRICE_EMPTY", "这个文件里没有内容");
        ms.Position = 0;
        var key = await storage.SaveAsync(ms, "price_" + Path.GetFileName(fileName), ct);
        // 只有上传的人、在两小时内能拿这个文件去预览和导入
        cache.Set(StageKey(key), fileName, TimeSpan.FromHours(2));
        return new PriceUploadResult(key, fileName, sheets.Where(s => s.Rows.Count > 0).Select(s => s.Name).ToList());
    }

    private async Task<List<SheetGrid>> StagedAsync(string fileKey, CancellationToken ct)
    {
        if (!cache.TryGetValue(StageKey(fileKey), out _))
            throw new DomainRuleException("PRICE_STAGE_EXPIRED", "上传的文件已过期，请重新上传");
        await using var st = await storage.OpenAsync(fileKey, ct);
        return PriceSheetReader.Read(st);
    }

    private static SheetGrid PickSheet(List<SheetGrid> sheets, string? name)
        => sheets.FirstOrDefault(s => s.Name == name && s.Rows.Count > 0) ?? sheets.FirstOrDefault(s => s.Rows.Count > 0)
           ?? throw new DomainRuleException("PRICE_EMPTY", "这个文件里没有内容");

    /// <summary>文件名去掉日期、版本号和扩展名，当默认表名。</summary>
    private static string NameFromFile(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName);
        n = Regex.Replace(n, @"[-_ ]*([A-Za-z]{0,4}20\d{2}[-_.]?\d{1,2}[-_.]?\d{1,2}|v\d+|\(\d+\)|（\d+）)", "", RegexOptions.IgnoreCase).Trim('-', '_', ' ');
        return n.Length == 0 ? Path.GetFileNameWithoutExtension(fileName) : n;
    }

    private async Task<(SheetGrid Grid, List<ColumnMap> Map, int DataIndex, int HeaderRowNo)> MapFor(SheetGrid raw, List<ColumnMap>? given, PriceTable? table)
    {
        var guess = PriceSheet.Guess(raw);
        if (given is { Count: > 0 })
        {
            var valid = given.Where(m => m.Col >= 0 && PriceSheet.Fields.Any(f => f.Field == m.Field)).ToList();
            return (guess.Grid, valid, guess.DataIndex, guess.HeaderRowNo);
        }
        if (table is not null)
        {
            // 同一张表的新版本：表头没变就沿用上次确认过的对应关系
            var stored = JsonSerializer.Deserialize<List<ColumnMap>>(table.ColumnMap, Json) ?? [];
            if (stored.Count > 0 && stored.All(s => guess.Map.FirstOrDefault(g => g.Col == s.Col)?.Header == s.Header))
                return (guess.Grid, stored, guess.DataIndex, guess.HeaderRowNo);
        }
        await Task.CompletedTask;
        return guess;
    }

    /// <summary>选中的工作表 + 表头和它一模一样的其它工作表（如「标准品价格表」里的 Sheet1 和「阀门」）一起取。
    /// 其它工作表取出的价格，分组记成工作表名。</summary>
    private static (List<ParsedPrice> Items, List<PriceIssue> Issues, List<string> Sheets) ExtractAll(
        List<SheetGrid> sheets, SheetGrid chosen, SheetGrid grid, List<ColumnMap> map, int dataIndex, string tableName, decimal taxRate)
    {
        var (items, issues) = PriceSheet.Extract(grid, map, dataIndex, tableName, taxRate);
        var included = new List<string> { chosen.Name };
        var headers = PriceSheet.Guess(chosen).Map.Where(m => m.Header.Length > 0).Select(m => (m.Col, m.Header)).ToList();
        foreach (var other in sheets.Where(s => s != chosen && s.Rows.Count > 0))
        {
            var g = PriceSheet.Guess(other);
            var oh = g.Map.Where(m => m.Header.Length > 0).Select(m => (m.Col, m.Header)).ToList();
            if (!oh.SequenceEqual(headers)) continue;
            var (more, moreIssues) = PriceSheet.Extract(g.Grid, map, g.DataIndex, tableName, taxRate);
            items.AddRange(more.Select(p => p with { Group = p.Group ?? other.Name }));
            foreach (var mi in moreIssues)
            {
                var i = issues.FindIndex(x => x.Kind == mi.Kind);
                if (i < 0) issues.Add(mi with { Rows = [] });
                else issues[i] = issues[i] with { Count = issues[i].Count + mi.Count };
            }
            included.Add(other.Name);
        }
        return (items, issues, included);
    }

    private async Task<List<(string Key, string Name, string? Spec, string? Supplier, decimal? Price)>> CurrentKeysAsync(PriceTable? t, CancellationToken ct)
    {
        if (t?.CurrentVersionId is not { } vid) return [];
        var rows = await db.PriceItems.AsNoTracking().Where(i => i.VersionId == vid)
            .Select(i => new { i.Key, i.Name, i.Spec, i.Supplier, i.TaxLow }).ToListAsync(ct);
        return rows.Select(r => (r.Key, r.Name, r.Spec, r.Supplier, r.TaxLow)).ToList();
    }

    public async Task<PricePreview> PreviewAsync(PricePreviewRequest req, CancellationToken ct = default)
    {
        RequireManage();
        var sheets = await StagedAsync(req.FileKey, ct);
        PriceTable? table = null;
        if (req.TableId is { } tid) table = await Tables.FirstOrDefaultAsync(t => t.Id == tid, ct) ?? throw new DomainRuleException("PRICE_NOT_FOUND", "这张价格表不存在");
        var tableName = req.TableName?.Trim() is { Length: > 0 } tn ? tn : table?.Name ?? NameFromFile(req.FileName);
        if (table is null && req.TableId is null)
            table = await Tables.FirstOrDefaultAsync(t => t.Name == tableName, ct); // 同名的表：多半是上传新版
        var raw = PickSheet(sheets, req.Sheet ?? table?.SheetName);
        var (grid, map, dataIndex, headerRow) = await MapFor(raw, req.Map, table);
        var (items, issues, included) = ExtractAll(sheets, raw, grid, map, dataIndex, tableName, await TaxRateAsync(ct));
        PriceDiff? diff = null;
        if (table?.CurrentVersionId is not null)
            diff = PriceSheet.Diff(await CurrentKeysAsync(table, ct), items.Select(i => (i.Key, i.Name, i.Spec, i.Supplier, i.TaxLow)));
        return new PricePreview(sheets.Where(s => s.Rows.Count > 0).Select(s => s.Name).ToList(), raw.Name, included, headerRow, map,
            items.Take(8).ToList(), items.Count, issues, diff,
            PriceSheet.DateInName(req.FileName) ?? DateOnly.FromDateTime(DateTime.UtcNow), table?.Id, tableName);
    }

    private static string SearchTextOf(ParsedPrice p)
        => Normalize(string.Join(" ", new[] { p.Group, p.Name, p.Spec, p.Material, p.Supplier, p.Model, p.Code, p.Note }.Where(s => !string.IsNullOrEmpty(s))));

    private static string Normalize(string s) => (s ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant()
        .Replace('ф', 'φ').Replace('Φ', 'φ').Replace('ϕ', 'φ').Replace('×', 'x').Replace('*', 'x').Replace(" ", "");

    public async Task<Guid> CommitAsync(PriceCommitRequest req, CancellationToken ct = default)
    {
        RequireManage();
        if (req.Map is null || !req.Map.Any(m => m.Field is "price" or "price_net"))
            throw new DomainRuleException("PRICE_NO_PRICE_COL", "至少要有一列是单价");
        var sheets = await StagedAsync(req.FileKey, ct);
        var now = DateTimeOffset.UtcNow;
        PriceTable table;
        if (req.TableId is { } tid)
            table = await Tables.FirstOrDefaultAsync(t => t.Id == tid, ct) ?? throw new DomainRuleException("PRICE_NOT_FOUND", "这张价格表不存在");
        else
        {
            var name = (req.TableName ?? "").Trim();
            if (name.Length is 0 or > 60) throw new DomainRuleException("PRICE_NAME", "请填写价格表名称（60 字以内）");
            if (await Tables.AnyAsync(t => t.Name == name, ct)) throw new DomainRuleException("PRICE_NAME_TAKEN", $"已经有一张叫「{name}」的价格表，要更新它请选「上传新版」");
            table = new PriceTable { Id = Guid.NewGuid(), CompanyId = me.CompanyId, Name = name, CreatedAt = now };
            db.PriceTables.Add(table);
        }
        if (req.Description is not null) table.Description = req.Description.Trim() is { Length: > 0 } d ? d[..Math.Min(d.Length, 200)] : null;

        var raw = PickSheet(sheets, req.Sheet);
        var (grid, map, dataIndex, _) = await MapFor(raw, req.Map, null);
        var (items, issues, _) = ExtractAll(sheets, raw, grid, map, dataIndex, table.Name, await TaxRateAsync(ct));
        if (items.Count == 0) throw new DomainRuleException("PRICE_EMPTY", "按现在的列对应关系一条价格都取不出来，请检查「名称」「单价」列");

        var diff = table.CurrentVersionId is null ? null
            : PriceSheet.Diff(await CurrentKeysAsync(table, ct), items.Select(i => (i.Key, i.Name, i.Spec, i.Supplier, i.TaxLow)));
        var uploader = await db.Users.AsNoTracking().Where(u => u.Id == me.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? me.Username;
        var version = new PriceVersion
        {
            Id = Guid.NewGuid(), TableId = table.Id, FileName = Path.GetFileName(req.FileName), FileKey = req.FileKey,
            VersionDate = req.VersionDate, UploadedById = me.UserId, UploadedByName = uploader, UploadedAt = now, ItemCount = items.Count,
            Issues = JsonSerializer.Serialize(issues, Json), Diff = diff is null ? "{}" : JsonSerializer.Serialize(diff, Json),
        };
        db.PriceVersions.Add(version);
        db.PriceItems.AddRange(items.Select(p => new PriceItem
        {
            TableId = table.Id, VersionId = version.Id, RowNo = p.RowNo, Key = p.Key, Group = Clip(p.Group, 100), Name = Clip(p.Name, 200)!,
            Spec = Clip(p.Spec, 300), Material = Clip(p.Material, 40), Supplier = Clip(p.Supplier, 80), Model = Clip(p.Model, 120), Code = Clip(p.Code, 60),
            Unit = Clip(p.Unit, 20), PriceUnit = Clip(p.PriceUnit, 30), TaxLow = p.TaxLow, TaxHigh = p.TaxHigh, NetLow = p.NetLow, NetHigh = p.NetHigh,
            TaxDerived = p.TaxDerived, LeadTime = Clip(p.LeadTime, 200), Frequency = Clip(p.Frequency, 20), Note = Clip(p.Note, 300),
            SearchText = SearchTextOf(p),
        }));
        table.CurrentVersionId = version.Id;
        table.ColumnMap = JsonSerializer.Serialize(map, Json);
        table.SheetName = raw.Name;
        table.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        cache.Remove(StageKey(req.FileKey));
        await Log("price.import", new { table = table.Name, file = version.FileName, items = items.Count, date = req.VersionDate.ToString("yyyy-MM-dd"),
            up = diff?.Up, down = diff?.Down, added = diff?.Added, removed = diff?.Removed }, table.Id.ToString(), ct);
        return table.Id;
    }

    private static string? Clip(string? s, int n) => s is null ? null : s.Length <= n ? s : s[..n];

    // ── 查询 ──────────────────────────────────────────

    private async Task<List<PriceHit>> HitsAsync(List<PriceItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        var stale = await StaleMonthsAsync(ct);
        var tableIds = items.Select(i => i.TableId).Distinct().ToList();
        var tables = await Tables.AsNoTracking().Where(t => tableIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var versions = await db.PriceVersions.AsNoTracking().Where(v => tableIds.Contains(v.TableId)).ToListAsync(ct);
        var vmap = versions.ToDictionary(v => v.Id);
        var keys = items.Select(i => i.Key).Distinct().ToList();
        // 同一物料在各版本里的价格：算较上一版的涨跌和版本数
        var all = await db.PriceItems.AsNoTracking().Where(i => tableIds.Contains(i.TableId) && keys.Contains(i.Key))
            .Select(i => new { i.TableId, i.Key, i.VersionId, i.TaxLow }).ToListAsync(ct);
        var list = new List<PriceHit>();
        foreach (var i in items)
        {
            if (!tables.TryGetValue(i.TableId, out var t) || !vmap.TryGetValue(i.VersionId, out var v)) continue;
            var hist = all.Where(x => x.TableId == i.TableId && x.Key == i.Key && vmap.ContainsKey(x.VersionId))
                .OrderBy(x => vmap[x.VersionId].VersionDate).ThenBy(x => vmap[x.VersionId].UploadedAt).ToList();
            var idx = hist.FindIndex(x => x.VersionId == i.VersionId);
            double? change = null;
            if (idx > 0 && hist[idx - 1].TaxLow is { } before && before > 0 && i.TaxLow is { } now)
                change = Math.Round((double)((now - before) / before * 100), 1);
            list.Add(new PriceHit(i.Id, t.Id, t.Name, v.VersionDate, AgeMonths(v.VersionDate) >= stale, i.RowNo, i.Group, i.Name, i.Spec, i.Material,
                i.Supplier, i.Model, i.Code, i.Unit, i.PriceUnit, i.TaxLow, i.TaxHigh, i.NetLow, i.NetHigh, i.TaxDerived, i.LeadTime, i.Note,
                change, hist.Count));
        }
        return list;
    }

    public async Task<List<PriceHit>> SearchAsync(string? query, Guid? tableId, string? material, string? supplier, int take, CancellationToken ct = default)
    {
        RequireView();
        take = Math.Clamp(take, 1, 200);
        var cur = await Tables.AsNoTracking().Where(t => t.CurrentVersionId != null && (tableId == null || t.Id == tableId))
            .Select(t => t.CurrentVersionId!.Value).ToListAsync(ct);
        var q = db.PriceItems.AsNoTracking().Where(i => cur.Contains(i.VersionId));
        if (!string.IsNullOrWhiteSpace(material)) q = q.Where(i => i.Material == material);
        if (!string.IsNullOrWhiteSpace(supplier)) q = q.Where(i => i.Supplier == supplier);
        var tokens = Regex.Split(query ?? "", @"[\s,，、;；/]+").Select(Normalize).Where(t => t.Length > 0).Distinct().Take(8).ToList();
        foreach (var tok in tokens) { var t = tok; q = q.Where(i => i.SearchText.Contains(t)); }
        var rows = await q.OrderBy(i => i.TableId).ThenBy(i => i.RowNo).Take(600).ToListAsync(ct);
        // 名称里就有关键词的排前面
        var ranked = rows.OrderByDescending(i => tokens.Count(t => Normalize(i.Name).Contains(t))).ThenBy(i => i.TaxLow == null)
            .ThenBy(i => i.RowNo).Take(take).ToList();
        return await HitsAsync(ranked, ct);
    }

    public async Task<List<PriceHit>> GetAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        RequireView();
        var tables = await Tables.Select(t => t.Id).ToListAsync(ct);
        var items = await db.PriceItems.AsNoTracking().Where(i => ids.Contains(i.Id) && tables.Contains(i.TableId)).ToListAsync(ct);
        return await HitsAsync(items, ct);
    }

    public async Task<List<PricePoint>> HistoryAsync(long itemId, CancellationToken ct = default)
    {
        RequireView();
        var tables = await Tables.Select(t => t.Id).ToListAsync(ct);
        var item = await db.PriceItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && tables.Contains(i.TableId), ct)
                   ?? throw new DomainRuleException("PRICE_NOT_FOUND", "这条价格不存在");
        var rows = await (from i in db.PriceItems.AsNoTracking()
                          join v in db.PriceVersions.AsNoTracking() on i.VersionId equals v.Id
                          where i.TableId == item.TableId && i.Key == item.Key
                          select new { v.VersionDate, v.UploadedAt, i.TaxLow, i.TaxHigh, v.FileName }).ToListAsync(ct);
        return rows.OrderBy(r => r.VersionDate).ThenBy(r => r.UploadedAt).Select(r => new PricePoint(r.VersionDate, r.TaxLow, r.TaxHigh, r.FileName)).ToList();
    }

    public async Task DeleteTableAsync(Guid tableId, CancellationToken ct = default)
    {
        RequireManage();
        var t = await Tables.FirstOrDefaultAsync(x => x.Id == tableId, ct) ?? throw new DomainRuleException("PRICE_NOT_FOUND", "这张价格表不存在");
        var versions = await db.PriceVersions.Where(v => v.TableId == tableId).ToListAsync(ct);
        await db.PriceItems.Where(i => i.TableId == tableId).ExecuteDeleteAsync(ct);
        db.PriceVersions.RemoveRange(versions);
        db.PriceTables.Remove(t);
        await db.SaveChangesAsync(ct);
        foreach (var v in versions)
            try { await storage.DeleteAsync(v.FileKey, ct); } catch (IOException) { /* 文件已经不在就算了 */ }
        await Log("price.delete", new { table = t.Name, versions = versions.Count }, tableId.ToString(), ct);
    }
}
