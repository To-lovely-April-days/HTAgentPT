using HT.Agent.Application.Logic;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>本地的危险工艺、危险化学品数据（重点监管工艺目录、危险化学品目录 + 分类信息表、易制爆、易制毒）。
/// 数据不大（几千条），整份放内存里查；后台改了调 Invalidate。</summary>
public class HazardCatalog(AppDbContext db)
{
    public record Snapshot(
        IReadOnlyList<HazardProcess> Processes,
        IReadOnlyList<HazardChemical> Chemicals,
        IReadOnlyList<ControlledChemical> Controlled,
        IReadOnlyList<HazardMatch.ProcessRow> ProcessRows,
        IReadOnlyList<HazardMatch.ChemRow> ChemRows,
        IReadOnlyList<HazardMatch.ControlRow> ControlRows,
        HashSet<string> Cas);

    private static volatile Snapshot? _snap;
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static void Invalidate() => _snap = null;

    public async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        if (_snap is { } s) return s;
        await Lock.WaitAsync(ct);
        try
        {
            if (_snap is { } s2) return s2;
            var p = await db.HazardProcesses.AsNoTracking().OrderBy(x => x.No).ToListAsync(ct);
            var c = await db.HazardChemicals.AsNoTracking().OrderBy(x => x.Seq).ThenBy(x => x.Sub).ThenBy(x => x.Name).ToListAsync(ct);
            var k = await db.ControlledChemicals.AsNoTracking().OrderBy(x => x.List).ThenBy(x => x.No).ToListAsync(ct);
            return _snap = new Snapshot(p, c, k,
                p.Select(x => new HazardMatch.ProcessRow(x.Id, x.No, x.Name, x.Typical, x.Intro)).ToList(),
                c.Select(x => new HazardMatch.ChemRow(x.Id, x.Seq, x.Sub, x.Name, x.Aliases, x.English, x.Cas)).ToList(),
                k.Select(x => new HazardMatch.ControlRow(x.List, x.No, x.Name, x.Cas, x.Category)).ToList(),
                c.Where(x => x.Cas is not null).Select(x => x.Cas!).ToHashSet());
        }
        finally { Lock.Release(); }
    }

    /// <summary>这个 CAS 号在不在《危险化学品目录》里。目录还没导入（表是空的）返回 null。</summary>
    public async Task<bool?> ListedAsync(string cas, CancellationToken ct)
    {
        var s = await LoadAsync(ct);
        return s.Chemicals.Count == 0 ? null : s.Cas.Contains(cas);
    }

    public async Task<List<HazardProcess>> FindProcessesAsync(string query, CancellationToken ct)
    {
        var s = await LoadAsync(ct);
        var ids = HazardMatch.MatchProcesses(query, s.ProcessRows).Select(r => r.Id).ToList();
        return ids.Select(id => s.Processes.First(p => p.Id == id)).ToList();
    }

    public record ChemResult(string Query, List<HazardChemical> Rows, List<ControlledChemical> Controlled, List<HazardChemical> Suggest);

    public async Task<ChemResult> FindChemicalAsync(string query, CancellationToken ct)
    {
        var s = await LoadAsync(ct);
        var ids = HazardMatch.FindChemical(query, s.ChemRows).Select(r => r.Id).ToHashSet();
        var rows = s.Chemicals.Where(x => ids.Contains(x.Id)).ToList();
        var main = rows.FirstOrDefault(r => !r.Sub) ?? rows.FirstOrDefault();
        var names = new List<string> { query };
        if (main is not null) names.AddRange([main.Name, .. main.Aliases.Split('；', StringSplitOptions.RemoveEmptyEntries)]);
        var cas = HazardMatch.IsCas(query) ? query.Trim() : main?.Cas;
        var ctl = HazardMatch.FindControlled(cas, names, s.ControlRows)
            .Select(r => s.Controlled.First(x => x.List == r.List && x.No == r.No)).ToList();
        var sug = rows.Count == 0 ? HazardMatch.Suggest(query, s.ChemRows).Select(r => s.Chemicals.First(x => x.Id == r.Id)).ToList() : [];
        return new ChemResult(query, rows, ctl, sug);
    }
}
