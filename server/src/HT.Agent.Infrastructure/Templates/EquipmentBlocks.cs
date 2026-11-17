using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;

namespace HT.Agent.Infrastructure.Templates;

/// <summary>按设备展开的模板（技术协议）：模板里每个「块」是一张设备参数表（t3_1…）或一行部件（part01…），
/// 块内控件 Tag 同前缀（见 <see cref="TemplateNormalizer"/>）。会话按设备清单把块复制成实例：
/// 实例的槽位 Tag 是「块#设备Id.序号」（t3_1#2.05），没选的块整块不出现。
///
/// 这里只做槽位层面的事——展开槽位、配第四章配套行、算配套行跟着表走的值；
/// Word 里的复制删除见 <see cref="EquipmentDocExpander"/>。两边按同一套 Tag 规则对得上。</summary>
public static class EquipmentBlocks
{
    private static readonly Regex BlockTag = new(
        @"^(?<b>(?:t\d+_\d+|part\d+)(?:_\d+)?)(?:#(?<i>[^.]+))?\.(?<n>.+)$", RegexOptions.Compiled);

    private static readonly Regex TableSection = new(@"^表\s*(?<no>\d+-\d+)\s*(?<kind>.*)$", RegexOptions.Compiled);
    private static readonly Regex PartSection = new(@"\s(?<idx>\d{2,})\s(?<name>.+?)(?:（(?<pos>[^（）]*)）)?$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>展开前的原始槽位：同一个模板对象可能在一轮里按新清单再展开一次。</summary>
    private static readonly ConditionalWeakTable<Template, List<TemplateSlot>> Originals = new();

    /// <summary>Tag → (块, 设备Id, 块内序号)；不属于任何块返回 null。</summary>
    public static (string Block, string? Instance, string Local)? Parse(string tag)
    {
        var m = BlockTag.Match(tag);
        return m.Success ? (m.Groups["b"].Value, m.Groups["i"].Success ? m.Groups["i"].Value : null, m.Groups["n"].Value) : null;
    }

    public static bool IsTable(string block) => block.StartsWith('t');

    public static string InstanceTag(string block, string id, string local) => $"{block}#{id}.{local}";

    public static bool HasBlocks(IEnumerable<TemplateSlot> slots) => slots.Any(s => Parse(s.Tag) is { Instance: null });

    public static List<EquipmentItem>? ParseItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<EquipmentItem>>(json, Json) ?? []; }
        catch (JsonException) { return null; }
    }

    public static string Serialize(IReadOnlyList<EquipmentItem> items) => JsonSerializer.Serialize(items, Json);

    /// <summary>把会话的设备清单套到模板上：template.Slots 换成「非块槽位 + 各设备实例槽位」。
    /// 模板没有块时原样不动。可以对同一模板对象反复调用（清单在一轮里变了就再套一次）。</summary>
    public static Template Apply(Template template, IReadOnlyList<EquipmentItem>? items)
    {
        if (!Originals.TryGetValue(template, out var original))
        {
            if (!HasBlocks(template.Slots)) return template;
            original = template.Slots.ToList();
            Originals.Add(template, original);
        }
        template.Slots = Expand(original, items ?? []);
        return template;
    }

    /// <summary>套用过设备清单的模板的原始槽位；模板不按设备展开时为 null。</summary>
    public static IReadOnlyList<TemplateSlot>? OriginalSlots(Template template)
        => Originals.TryGetValue(template, out var o) ? o : null;

    /// <summary>展开槽位。实例按块在模板里的位置就地展开，同块多台按清单顺序；
    /// 设备参数表按出现顺序重新编号（表3-1、3-2…），章节名带上位号与名称，问的时候一眼知道是哪台。
    /// 配套行里能从参数表推出来的项（同名参数）不展开成槽位——那是同一份数据，填一次就够。</summary>
    public static List<TemplateSlot> Expand(IReadOnlyList<TemplateSlot> original, IReadOnlyList<EquipmentItem> items)
    {
        var ordered = original.OrderBy(s => s.SortOrder).ToList();
        var byBlock = ordered.Where(s => Parse(s.Tag) is { Instance: null })
            .GroupBy(s => Parse(s.Tag)!.Value.Block).ToDictionary(g => g.Key, g => g.ToList());
        var tableNo = TableNumbers(ordered, items);
        var derived = DerivedLinks(original, items).Select(d => d.RowTag).ToHashSet();

        var result = new List<TemplateSlot>();
        var emitted = new HashSet<string>();
        foreach (var slot in ordered)
        {
            var p = Parse(slot.Tag);
            if (p is null) { result.Add(Clone(slot, slot.Tag, slot.Section)); continue; }
            if (p.Value.Instance is not null || !emitted.Add(p.Value.Block)) continue;
            foreach (var item in items.Where(i => i.Block == p.Value.Block && i.Id is not null))
            {
                var section = InstanceSection(byBlock[p.Value.Block][0].Section, item, tableNo);
                foreach (var s in byBlock[p.Value.Block])
                {
                    var tag = InstanceTag(p.Value.Block, item.Id!, Parse(s.Tag)!.Value.Local);
                    if (derived.Contains(tag)) continue;
                    var clone = Clone(s, tag, section);
                    // 配套行剩下的项（表里没有的补充说明）选填：主参数已在设备参数表里问过
                    if (item.LinkedTo is not null) clone.Required = false;
                    result.Add(clone);
                }
            }
        }
        for (var i = 0; i < result.Count; i++) result[i].SortOrder = i;
        return result;
    }

    /// <summary>设备参数表实例的表号：按块在模板里的顺序、同块按清单顺序，从 1 连续编。</summary>
    public static Dictionary<string, string> TableNumbers(IReadOnlyList<TemplateSlot> original, IReadOnlyList<EquipmentItem> items)
    {
        var map = new Dictionary<string, string>();
        var blocks = original.OrderBy(s => s.SortOrder).Select(s => Parse(s.Tag))
            .Where(p => p is { Instance: null } && IsTable(p.Value.Block)).Select(p => p!.Value.Block).Distinct().ToList();
        var chapter = original.Select(s => TableSection.Match(s.Section)).Where(m => m.Success)
            .Select(m => m.Groups["no"].Value.Split('-')[0]).FirstOrDefault() ?? "3";
        var n = 0;
        foreach (var b in blocks)
            foreach (var item in items.Where(i => i.Block == b && i.Id is not null))
                map[item.Id!] = $"表{chapter}-{++n}";
        return map;
    }

    private static string InstanceSection(string blockSection, EquipmentItem item, Dictionary<string, string> tableNo)
    {
        var who = string.Join(" ", new[] { item.TagNo, item.Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (tableNo.TryGetValue(item.Id!, out var no))
        {
            var kind = TableSection.Match(blockSection) is { Success: true } m ? m.Groups["kind"].Value.Trim() : blockSection;
            return Safe($"{no} {kind}{(who.Length > 0 ? $" · {who}" : "")}");
        }
        var catalogName = PartSection.Match(blockSection) is { Success: true } pm ? pm.Groups["name"].Value : blockSection;
        return Safe($"主要部件清单 · {(who.Length > 0 ? who : catalogName)}");
    }

    private static string Safe(string s) => s.Replace('/', '、').Replace('／', '、');

    private static TemplateSlot Clone(TemplateSlot s, string tag, string section) => new()
    {
        Id = s.Id, TemplateId = s.TemplateId, Tag = tag, Name = s.Name, Section = section,
        DataType = s.DataType, Unit = s.Unit, Choices = s.Choices, Required = s.Required, Stage = s.Stage,
        ForbidInherit = s.ForbidInherit, Prompt = s.Prompt, SuggestSource = s.SuggestSource,
        SubFields = s.SubFields, SortOrder = s.SortOrder
    };

    // ── 目录 ────────────────────────────────────────────────

    /// <summary>可选设备目录：每个块一项。rowTexts 是部件行的正文（从 Word 里取），
    /// 用来认出「详见主要设备参数表3-1」这种指向参数表的配套行。</summary>
    public static List<EquipmentCatalogEntry> Catalog(IReadOnlyList<TemplateSlot> original,
        IReadOnlyDictionary<string, string>? rowTexts = null)
    {
        var list = new List<EquipmentCatalogEntry>();
        foreach (var g in original.OrderBy(s => s.SortOrder).Where(s => Parse(s.Tag) is { Instance: null })
                     .GroupBy(s => Parse(s.Tag)!.Value.Block))
        {
            var section = g.First().Section;
            if (IsTable(g.Key))
            {
                var m = TableSection.Match(section);
                var kind = m.Success && m.Groups["kind"].Value.Trim().Length > 0 ? m.Groups["kind"].Value.Trim() : section;
                list.Add(new EquipmentCatalogEntry(g.Key, "table", $"{kind}（设备参数表）", kind, null, null, g.Count()));
            }
            else
            {
                var m = PartSection.Match(section);
                var name = m.Success ? m.Groups["name"].Value.Trim() : section;
                var pos = m.Success && m.Groups["pos"].Success ? m.Groups["pos"].Value.Trim() : null;
                string? refTable = null;
                if (rowTexts?.GetValueOrDefault(g.Key) is { } text && Regex.Match(text, @"表\s*(\d+)\s*[-－—]\s*(\d+)") is { Success: true } rm)
                    refTable = $"t{rm.Groups[1].Value}_{rm.Groups[2].Value}";
                var title = $"{name}{(pos is null ? "" : $"（{pos}）")}{(refTable is null ? "" : "·详见参数表")}";
                list.Add(new EquipmentCatalogEntry(g.Key, "part", title, name, pos, refTable, g.Count()));
            }
        }
        return list;
    }

    /// <summary>给一台参数表设备挑第四章的配套行：
    /// ① 正文写着「详见表3-x」指向这张表的行；② 行名被设备名称包含（「预聚收集罐」→「罐」）；
    /// ③ 行名被参数表用途包含（「带搅拌反应釜」→「反应釜」），同名取参数多的那行。</summary>
    public static string? PickCompanion(EquipmentItem table, IReadOnlyList<EquipmentCatalogEntry> catalog)
    {
        var parts = catalog.Where(c => c.Kind == "part").ToList();
        var tableEntry = catalog.FirstOrDefault(c => c.Block == table.Block);
        var byRef = parts.FirstOrDefault(p => p.RefTable == table.Block);
        if (byRef is not null) return byRef.Block;
        var name = table.Name ?? "";
        var byName = parts.Where(p => p.Name.Length > 0 && name.Contains(p.Name) && p.RefTable is null)
            .OrderByDescending(p => p.Name.Length).ThenByDescending(p => p.SlotCount).FirstOrDefault();
        if (byName is not null) return byName.Block;
        var kind = tableEntry?.Name ?? "";
        return parts.Where(p => p.Name.Length > 0 && kind.Contains(p.Name) && p.RefTable is null)
            .OrderByDescending(p => p.Name.Length).ThenByDescending(p => p.SlotCount).FirstOrDefault()?.Block;
    }

    /// <summary>整理清单：补编号、去掉指向已删设备的配套行、给每张参数表配上（且只配一条）第四章行，
    /// 配套行的位号/名称/数量跟着参数表走。</summary>
    public static List<EquipmentItem> Normalize(IReadOnlyList<EquipmentItem> input, IReadOnlyList<EquipmentItem>? previous,
        IReadOnlyList<EquipmentCatalogEntry> catalog)
    {
        var known = catalog.Select(c => c.Block).ToHashSet();
        // 编号随机取、不按序号递增：删掉的设备编号不会被新设备接手——
        // 对话状态里「跳过了哪些项」「上一版建议」都按 Tag 记，复用编号会让新设备继承旧设备的这些痕迹
        var taken = new[] { previous ?? [], input }.SelectMany(l => l).Select(i => i.Id).Where(i => i is not null).ToHashSet();
        string NewId()
        {
            string id;
            do id = Guid.NewGuid().ToString("N")[..6]; while (!taken.Add(id));
            return id;
        }
        var items = new List<EquipmentItem>();
        foreach (var raw in input.Where(i => known.Contains(i.Block)))
        {
            var id = string.IsNullOrWhiteSpace(raw.Id) || raw.Id.Contains('.') || raw.Id.Contains('#') ? NewId() : raw.Id.Trim();
            if (items.Any(i => i.Id == id)) id = NewId();
            items.Add(raw with
            {
                Id = id,
                TagNo = Blank(raw.TagNo),
                Name = Blank(raw.Name),
                Qty = Math.Clamp(raw.Qty, 1, 999)
            });
        }
        var tables = items.Where(i => IsTable(i.Block)).ToDictionary(i => i.Id!);
        items.RemoveAll(i => i.LinkedTo is not null && !tables.ContainsKey(i.LinkedTo));
        foreach (var t in tables.Values)
        {
            var linked = items.Where(i => i.LinkedTo == t.Id).ToList();
            if (linked.Count == 0)
            {
                var block = PickCompanion(t, catalog);
                if (block is null) continue;
                items.Add(new EquipmentItem(NewId(), block, t.TagNo, t.Name, t.Qty, t.Id));
                continue;
            }
            foreach (var extra in linked.Skip(1)) items.Remove(extra);
            var keep = linked[0];
            items[items.IndexOf(keep)] = keep with { TagNo = t.TagNo, Name = t.Name, Qty = t.Qty };
        }
        return items;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ── 配套行跟着参数表走的值 ───────────────────────────────

    public record DerivedLink(string RowTag, string TableTag);

    /// <summary>配套行里哪些项由参数表推出：按名称对（去掉单位、夹套/盘管统一、型式=形式），
    /// 「名称」对「设备名称」。对上的项不单独问，生成时从表里取值。</summary>
    public static List<DerivedLink> DerivedLinks(IReadOnlyList<TemplateSlot> original, IReadOnlyList<EquipmentItem> items)
    {
        var blocks = original.Where(s => Parse(s.Tag) is { Instance: null })
            .GroupBy(s => Parse(s.Tag)!.Value.Block).ToDictionary(g => g.Key, g => g.ToList());
        var links = new List<DerivedLink>();
        foreach (var row in items.Where(i => i.LinkedTo is not null && i.Id is not null))
        {
            var table = items.FirstOrDefault(i => i.Id == row.LinkedTo);
            if (table is null || !blocks.TryGetValue(table.Block, out var tableSlots) || !blocks.TryGetValue(row.Block, out var rowSlots))
                continue;
            var used = new HashSet<string>();
            foreach (var rs in rowSlots)
            {
                var key = NormName(rs.Name);
                var hit = tableSlots.FirstOrDefault(ts => !used.Contains(ts.Tag) && NormName(ts.Name) == key)
                          ?? tableSlots.FirstOrDefault(ts => !used.Contains(ts.Tag) && NormName(ts.Name).StartsWith(key + "·"));
                if (hit is null) continue;
                used.Add(hit.Tag);
                links.Add(new DerivedLink(
                    InstanceTag(row.Block, row.Id!, Parse(rs.Tag)!.Value.Local),
                    InstanceTag(table.Block, table.Id!, Parse(hit.Tag)!.Value.Local)));
            }
        }
        return links;
    }

    private static string NormName(string name)
    {
        var s = Regex.Replace(name, @"[（(][^（）()]*[）)]", "");
        s = s.Replace("夹套/盘管", "夹套").Replace("型式", "形式").Replace(" ", "");
        return s == "名称" ? "设备名称" : s;
    }

    /// <summary>新设备落值：参数表里的位号、设备名称、数量就是清单上填的，不再问一遍。</summary>
    public static List<(string Tag, string Value)> IdentityValues(IReadOnlyList<TemplateSlot> expanded, IReadOnlyList<EquipmentItem> items)
    {
        var result = new List<(string, string)>();
        foreach (var s in expanded)
        {
            var p = Parse(s.Tag);
            if (p?.Instance is null) continue;
            var item = items.FirstOrDefault(i => i.Id == p.Value.Instance);
            if (item is null) continue;
            var v = s.Name switch
            {
                "位号" => item.TagNo,
                "设备名称" or "名称" => item.Name,
                "数量" => item.Qty.ToString(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(v)) result.Add((s.Tag, v));
        }
        return result;
    }
}
