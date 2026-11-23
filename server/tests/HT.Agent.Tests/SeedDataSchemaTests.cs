using System.Text.Json;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Xunit;

namespace HT.Agent.Tests;

/// <summary>启动时要导进库的内置数据放得进表；迁移和模型对得上。
/// 起因：危化品目录里有的条目登记几个 CAS 号（31 个字符），表里只留了 20，服务器上导入报错、后端起不来。</summary>
public class SeedDataSchemaTests
{
    private static AppDbContext Db() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=none", o => o.UseVector()).Options,
        Options.Create(new PersistenceOptions()));

    private static JsonElement Items(string name)
    {
        using var st = typeof(Seeder).Assembly.GetManifestResourceStream($"hazard/{name}.json")!;
        using var doc = JsonDocument.Parse(st);
        return doc.RootElement.GetProperty("items").Clone();
    }

    private static void Fits(IEntityType et, string prop, JsonElement items, Func<JsonElement, string?> value, List<string> bad)
    {
        var max = et.FindProperty(prop)!.GetMaxLength();
        if (max is null) return;
        foreach (var it in items.EnumerateArray())
        {
            var v = value(it);
            if (v is not null && v.Length > max) bad.Add($"{et.ClrType.Name}.{prop} 限 {max}，实际 {v.Length}：{v}");
        }
    }

    private static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [Fact]
    public void HazardData_FitsColumns()
    {
        using var db = Db();
        var bad = new List<string>();
        var chem = db.Model.FindEntityType(typeof(HazardChemical))!;
        var chems = Items("chemicals");
        Fits(chem, nameof(HazardChemical.Name), chems, e => S(e, "name"), bad);
        Fits(chem, nameof(HazardChemical.Cas), chems, e => S(e, "cas"), bad);
        Fits(chem, nameof(HazardChemical.English), chems, e => S(e, "en"), bad);
        Fits(chem, nameof(HazardChemical.Note), chems, e => S(e, "changed"), bad);
        Fits(chem, nameof(HazardChemical.Aliases), chems, e => string.Join("；", e.GetProperty("aliases").EnumerateArray().Select(a => a.GetString())), bad);
        Fits(chem, nameof(HazardChemical.Classes), chems, e => string.Join("\n", e.GetProperty("classes").EnumerateArray().Select(a => a.GetString())), bad);

        var ctl = db.Model.FindEntityType(typeof(ControlledChemical))!;
        var pre = Items("precursors");
        Fits(ctl, nameof(ControlledChemical.List), pre, e => S(e, "list"), bad);
        Fits(ctl, nameof(ControlledChemical.No), pre, e => S(e, "no"), bad);
        Fits(ctl, nameof(ControlledChemical.Name), pre, e => S(e, "name"), bad);
        Fits(ctl, nameof(ControlledChemical.Cas), pre, e => S(e, "cas"), bad);
        Fits(ctl, nameof(ControlledChemical.Category), pre, e => S(e, "cls"), bad);

        var proc = db.Model.FindEntityType(typeof(HazardProcess))!;
        Fits(proc, nameof(HazardProcess.Name), Items("processes"), e => S(e, "name"), bad);

        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(10)));
    }

    /// <summary>改了模型（字段长度、新表）却没加迁移，服务器上的表就和代码对不上。</summary>
    [Fact]
    public void Migrations_MatchModel()
    {
        using var db = Db();
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        var initialized = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot);
        var current = db.GetService<IDesignTimeModel>().Model;
        var diffs = db.GetService<IMigrationsModelDiffer>().GetDifferences(initialized.GetRelationalModel(), current.GetRelationalModel());
        Assert.True(diffs.Count == 0, "模型改了但没有对应的迁移：" + string.Join("；", diffs.Select(d => d.GetType().Name)));
    }
}
