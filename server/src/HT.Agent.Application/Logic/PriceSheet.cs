using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>一个工作表的原始内容：每行带 Excel 行号；合并单元格单独给。</summary>
public record SheetGrid(string Name, List<SheetRow> Rows, List<CellRange> Merges);
public record SheetRow(int RowNo, string[] Cells);
/// <summary>合并区域：行是 Excel 行号，列从 0 起。</summary>
public record CellRange(int R1, int C1, int R2, int C2);

/// <summary>一列对应什么。Supplier 不空表示「多家供应商并排」的表里这一列属于哪一家。</summary>
public record ColumnMap(int Col, string Header, string Field, string? Supplier = null);

public record ParsedPrice(
    int RowNo, string? Group, string Name, string? Spec, string? Material, string? Supplier, string? Model, string? Code,
    string? Unit, string? PriceUnit, decimal? TaxLow, decimal? TaxHigh, decimal? NetLow, decimal? NetHigh, bool TaxDerived,
    string? LeadTime, string? Frequency, string? Note)
{
    public string Key => PriceSheet.KeyOf(Name, Spec, Supplier, Model);
}

/// <summary>导入时发现的一类问题：数量、涉及的行号（最多 20 个）、给人看的说明。</summary>
public record PriceIssue(string Kind, string Label, int Count, List<int> Rows, string Message);

public record SheetPreview(string Sheet, int HeaderRowNo, int DataRowNo, List<ColumnMap> Map, List<ParsedPrice> Items, List<PriceIssue> Issues);

public record PriceChange(string Name, string? Spec, string? Supplier, decimal? Old, decimal? New, double Pct);
public record PriceDiff(int Up, int Down, int Added, int Removed, int Same, List<PriceChange> Top);

/// <summary>价格表导入：认表头、猜每列是什么、逐行取出价格、找出问题、和上一版比。
/// 只按表格的形态和表头的通用叫法来认（名称、规格、单价、型号…），不针对某一张表写死；认错了由人在导入页改。</summary>
public static class PriceSheet
{
    /// <summary>可选的列含义（导入页下拉框用同一套）。</summary>
    public static readonly (string Field, string Label)[] Fields =
    [
        ("name", "名称"), ("spec", "规格"), ("group", "分组"), ("unit", "数量单位"), ("price", "含税单价"), ("price_net", "未税单价"),
        ("price_unit", "计价单位"), ("supplier", "供应商 / 品牌"), ("model", "型号"), ("code", "物料代码"), ("lead", "货期"),
        ("freq", "采购频率"), ("note", "备注"), ("ignore", "不导入"),
    ];

    // ── 表头 ───────────────────────────────────────────

    private static string Norm(string s) => Regex.Replace((s ?? "").Normalize(NormalizationForm.FormKC), @"\s+", "").ToLowerInvariant();

    /// <summary>按表头文字猜含义；认不出返回 null。</summary>
    public static string? FieldOf(string header)
    {
        var h = Norm(header);
        if (h.Length == 0) return null;
        if (h.Contains("未税") || h.Contains("不含税")) return "price_net";
        if (h.Contains("含税")) return "price";
        if (h.Contains("单价") || h.Contains("价格") || h.Contains("采购价") || h.Contains("报价") || h == "价" || h.Contains("price")) return "price";
        if (h.Contains("型号") || h == "model") return "model";
        if (h.Contains("代码") || h.Contains("编码") || h.Contains("料号")) return "code";
        if (h.Contains("分组") || h.Contains("类别") || h.Contains("分类") || h.Contains("大类")) return "group";
        if (h.Contains("名称") || h.Contains("品名") || h == "name") return "name";
        if (h.Contains("规格") || h.Contains("描述") || h.Contains("尺寸") || h.Contains("通径") || h.Contains("外径") || h.Contains("直径") || h.Contains("材质")) return "spec";
        if (h.Contains("计价单位")) return "price_unit";
        if (h.Contains("单位") || h == "unit") return "unit";
        if (h.Contains("品牌") || h.Contains("供应商") || h.Contains("厂家") || h.Contains("厂商")) return "supplier";
        if (h.Contains("货期") || h.Contains("交期") || h.Contains("交货")) return "lead";
        if (h.Contains("频率")) return "freq";
        if (h.Contains("备注") || h.Contains("说明")) return "note";
        if (h.Contains("序号") || h == "no" || h == "no.") return "ignore";
        return null;
    }

    private static string Cell(SheetRow r, int c) => c < r.Cells.Length ? (r.Cells[c] ?? "").Trim() : "";

    /// <summary>表头在哪一行：前 15 行里认得出的表头最多的那行；再看下一行是不是二级表头（多家供应商并排时的「单价 / 型号」）。</summary>
    public static (int Index, bool TwoRow) FindHeader(List<SheetRow> rows)
    {
        int best = 0, bestScore = -1;
        for (var i = 0; i < Math.Min(15, rows.Count); i++)
        {
            var score = rows[i].Cells.Count(c => FieldOf(c ?? "") is { } f && f != "ignore");
            if (score > bestScore) { best = i; bestScore = score; }
        }
        // 选中的是二级表头（只有「单价 / 型号 / 货期」）而上一行才是真表头：退回上一行
        static bool SubOnly(SheetRow r) => r.Cells.Select(c => FieldOf(c ?? "")).Where(f => f is not null and not "ignore")
            .All(f => f is "price" or "price_net" or "model" or "lead");
        if (best > 0 && SubOnly(rows[best]) && rows[best - 1].Cells.Count(c => FieldOf(c ?? "") is { } f && f != "ignore") >= 2)
            return (best - 1, true);
        var twoRow = false;
        if (best + 1 < rows.Count)
        {
            var next = rows[best + 1].Cells.Select(c => FieldOf(c ?? "")).Where(f => f is "price" or "price_net" or "model" or "lead").Count();
            var nextNumeric = rows[best + 1].Cells.Count(c => ParsePrice(c ?? "").Low is not null);
            twoRow = next >= 2 && nextNumeric == 0;
        }
        return (best, twoRow);
    }

    /// <summary>把合并单元格的值补到它覆盖的格子里：表头区域横竖都补；数据区域只往下补（如「元/kg」写一次管几十行）。</summary>
    public static SheetGrid ApplyMerges(SheetGrid g, int headerEndRowNo)
    {
        var byRow = g.Rows.ToDictionary(r => r.RowNo);
        foreach (var m in g.Merges)
        {
            if (!byRow.TryGetValue(m.R1, out var top)) continue;
            var value = Cell(top, m.C1);
            if (value.Length == 0) continue;
            var inHeader = m.R1 <= headerEndRowNo;
            for (var r = m.R1; r <= m.R2; r++)
            {
                if (!byRow.TryGetValue(r, out var row)) continue;
                var lastCol = inHeader ? m.C2 : m.C1;
                for (var c = m.C1; c <= lastCol; c++)
                {
                    if (r == m.R1 && c == m.C1) continue;
                    if (c >= row.Cells.Length)
                    {
                        var grown = new string[c + 1];
                        Array.Fill(grown, "");
                        row.Cells.CopyTo(grown, 0);
                        row = row with { Cells = grown };
                        byRow[r] = row;
                    }
                    if (string.IsNullOrWhiteSpace(row.Cells[c])) row.Cells[c] = value;
                }
            }
        }
        return g with { Rows = g.Rows.Select(r => byRow[r.RowNo]).ToList() };
    }

    /// <summary>猜整张表的列对应关系。返回表头行号和数据起始行在 Rows 里的下标。</summary>
    public static (SheetGrid Grid, List<ColumnMap> Map, int DataIndex, int HeaderRowNo) Guess(SheetGrid raw)
    {
        if (raw.Rows.Count == 0) return (raw, [], 0, 0);
        var (hi, twoRow) = FindHeader(raw.Rows);
        var headerRowNo = raw.Rows[hi].RowNo;
        var g = ApplyMerges(raw, twoRow ? raw.Rows[hi + 1].RowNo : headerRowNo);
        var head = g.Rows[hi];
        var sub = twoRow ? g.Rows[hi + 1] : null;
        var dataIndex = hi + (twoRow ? 2 : 1);
        var width = g.Rows.Max(r => r.Cells.Length);
        var data = g.Rows.Skip(dataIndex).ToList();

        var map = new List<ColumnMap>();
        for (var c = 0; c < width; c++)
        {
            var top = Cell(head, c);
            var s = sub is null ? "" : Cell(sub, c);
            string header; string? field; string? supplier = null;
            if (s.Length > 0 && top.Length > 0 && top != s && FieldOf(top) is null or "price" or "price_net")
            {
                // 二级表头：上面是供应商名、下面是「单价 / 型号」
                header = $"{top} / {s}";
                field = FieldOf(s);
                if (FieldOf(top) is null && field is "price" or "price_net" or "model" or "lead") supplier = top;
            }
            else if (sub is not null && (s.Length == 0 || s == top) && top.Length > 0 && FieldOf(top) is null && NumericShare(data, c) >= 0.5)
            {
                // 二级表头里只写了供应商名、下面直接是价格
                header = top;
                field = "price";
                supplier = top;
            }
            else
            {
                header = top.Length > 0 ? top : s;
                field = FieldOf(header);
            }
            if (field is null && header.Length == 0)
                field = GuessByValues(data, c);
            map.Add(new ColumnMap(c, header, field ?? "ignore", supplier));
        }

        // 没有一列被认成价格：找一列大多是数字的、没有表头的，当价格（导入页会提示确认）
        if (!map.Any(m => m.Field is "price" or "price_net"))
        {
            var cand = map.FirstOrDefault(m => m.Field == "ignore" && m.Header.Length == 0 && NumericShare(data, m.Col) >= 0.5);
            if (cand is not null) map[cand.Col] = cand with { Field = "price" };
        }
        // 同一含义只认第一列，规格可以多列
        var seen = new HashSet<string>();
        for (var i = 0; i < map.Count; i++)
        {
            var m = map[i];
            if (m.Field is "ignore" or "spec") continue;
            var k = m.Field + "|" + m.Supplier;
            if (!seen.Add(k)) map[i] = m with { Field = "ignore" };
        }
        return (g, map, dataIndex, headerRowNo);
    }

    private static double NumericShare(List<SheetRow> data, int c)
    {
        var vals = data.Select(r => Cell(r, c)).Where(v => v.Length > 0).ToList();
        return vals.Count == 0 ? 0 : (double)vals.Count(v => ParsePrice(v).Low is not null) / vals.Count;
    }

    private static string? GuessByValues(List<SheetRow> data, int c)
    {
        var vals = data.Select(r => Cell(r, c)).Where(v => v.Length > 0).ToList();
        if (vals.Count == 0) return null;
        if ((double)vals.Count(v => v.StartsWith("元/") || v.StartsWith("元／")) / vals.Count >= 0.6) return "price_unit";
        return null;
    }

    // ── 取值 ───────────────────────────────────────────

    /// <summary>价格文字 → 最低 / 最高。认得「26.1」「¥1,120」「36-40」「36~40」；「无」「/」「-」之类返回空。</summary>
    public static (decimal? Low, decimal? High) ParsePrice(string s)
    {
        var t = (s ?? "").Normalize(NormalizationForm.FormKC).Replace(",", "").Replace("¥", "").Replace("元", "").Replace("约", "").Trim();
        if (t.Length == 0) return (null, null);
        var m = Regex.Match(t, @"^(\d+(?:\.\d+)?)\s*(?:-|~|～|—|至|到)\s*(\d+(?:\.\d+)?)$");
        if (m.Success)
        {
            var a = decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var b = decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return (Math.Min(a, b), Math.Max(a, b));
        }
        if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
            return (Math.Round(v, 4), Math.Round(v, 4));
        return (null, null);
    }

    private static readonly Regex MaterialRe = new(
        @"(316L|316|304L|304|310S|321|2205|2507|904L|C-?276|C-?22|哈氏合金|Inconel\s?625|625|825|Monel|蒙乃尔|M400|TA\d+|TC4|Zr\s?702|702|锆|钛|PTFE|PEEK|PPL|PP|尼龙|铝|6061|碳钢|Q235|Q345|45#|20#|35Cr\w*|黄铜|紫铜|青铜|铜)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? MaterialOf(string text)
    {
        var m = MaterialRe.Match(text ?? "");
        return m.Success ? m.Value.ToUpperInvariant().Replace("C-276", "C276").Replace("C-22", "C22") : null;
    }

    public static string KeyOf(string name, string? spec, string? supplier, string? model)
    {
        static string n(string? s) => Regex.Replace((s ?? "").Normalize(NormalizationForm.FormKC), @"\s+", "").ToLowerInvariant()
            .Replace('ф', 'φ').Replace('Φ', 'φ').Replace('ϕ', 'φ').Replace('×', 'x').Replace('*', 'x');
        return $"{n(name)}|{n(spec)}|{n(supplier)}|{n(model)}";
    }

    /// <summary>单位归一：kg/公斤/KG（含常见手误 GK）→ kg；米/m → m；其余原样小写。</summary>
    public static string UnitBase(string? unit)
    {
        var u = Norm(unit ?? "").Trim('(', ')', '（', '）');
        return u switch
        {
            "kg" or "公斤" or "千克" or "gk" => "kg",
            "米" or "m" => "m",
            "毫米" or "mm" => "mm",
            "克" or "g" => "g",
            "吨" or "t" => "t",
            "个" or "只" or "件" or "pcs" or "pc" => "个",
            _ => u,
        };
    }

    /// <summary>计价单位「元/5kg」→ (基本单位 kg, 每份 5)。</summary>
    public static (string Base, decimal Per) PriceUnitOf(string? priceUnit)
    {
        var p = Norm(priceUnit ?? "").Replace("／", "/");
        var i = p.IndexOf('/');
        var denom = i >= 0 ? p[(i + 1)..] : p;
        var m = Regex.Match(denom, @"^(\d+(?:\.\d+)?)(.*)$");
        if (m.Success && m.Groups[2].Value.Length > 0)
            return (UnitBase(m.Groups[2].Value), decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        return (UnitBase(denom), 1);
    }

    /// <summary>逐行取出价格并汇总问题。</summary>
    public static (List<ParsedPrice> Items, List<PriceIssue> Issues) Extract(SheetGrid g, List<ColumnMap> map, int dataIndex, string tableName, decimal taxRate = 1.13m)
    {
        var items = new List<ParsedPrice>();
        var issues = new Dictionary<string, (string Label, List<int> Rows, string Msg)>();
        void Issue(string kind, string label, int row, string msg)
        {
            if (!issues.TryGetValue(kind, out var x)) issues[kind] = x = (label, new List<int>(), msg);
            x.Rows.Add(row);
        }

        string? One(SheetRow r, string field, string? supplier = null)
        {
            var cols = map.Where(m => m.Field == field && m.Supplier == supplier).ToList();
            var vals = cols.Select(m => Cell(r, m.Col)).Where(v => v.Length > 0).Distinct().ToList();
            return vals.Count == 0 ? null : string.Join(field == "spec" ? " · " : " ", vals);
        }

        var suppliers = map.Where(m => m.Supplier is not null).Select(m => m.Supplier!).Distinct().ToList();
        var headerTexts = map.Select(m => Norm(m.Header)).Where(h => h.Length > 0).ToHashSet();
        var hasNameCol = map.Any(m => m.Field == "name");

        foreach (var r in g.Rows.Skip(dataIndex))
        {
            if (r.Cells.All(c => string.IsNullOrWhiteSpace(c))) continue;
            // 表中间重复出现的表头行
            var nonEmpty = r.Cells.Where(c => !string.IsNullOrWhiteSpace(c)).Select(Norm).ToList();
            if (nonEmpty.Count >= 2 && nonEmpty.Count(headerTexts.Contains) >= nonEmpty.Count / 2 + 1) continue;

            var group = One(r, "group");
            var name = One(r, "name");
            var spec = One(r, "spec");
            if (!hasNameCol) name = tableName;
            if (string.IsNullOrWhiteSpace(name)) name = group;
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(spec)) continue;
            name ??= tableName;

            var unit = One(r, "unit");
            var priceUnit = One(r, "price_unit");
            var common = (group, name: name.Trim(), spec, code: One(r, "code"), unit, freq: One(r, "freq"), note: One(r, "note"),
                material: MaterialOf($"{name} {spec}"));

            var offers = new List<(string? Supplier, string? Model, string? Lead, (decimal?, decimal?) Tax, (decimal?, decimal?) Net)>();
            if (suppliers.Count == 0)
                offers.Add((One(r, "supplier"), One(r, "model"), One(r, "lead"), ParsePrice(One(r, "price") ?? ""), ParsePrice(One(r, "price_net") ?? "")));
            else
            {
                foreach (var s in suppliers)
                {
                    var tax = ParsePrice(One(r, "price", s) ?? "");
                    var net = ParsePrice(One(r, "price_net", s) ?? "");
                    var model = One(r, "model", s);
                    if (tax.Low is null && net.Low is null && model is null) continue; // 这家没报
                    offers.Add((s, model, One(r, "lead", s) ?? One(r, "lead"), tax, net));
                }
                if (offers.Count == 0) offers.Add((null, null, One(r, "lead"), (null, null), (null, null)));
            }

            foreach (var o in offers)
            {
                var (tl, th) = o.Tax;
                var (nl, nh) = o.Net;
                var derived = false;
                if (tl is null && nl is not null) { tl = Math.Round(nl.Value * taxRate, 2); th = Math.Round(nh!.Value * taxRate, 2); derived = true; }
                else if (nl is null && tl is not null) { nl = Math.Round(tl.Value / taxRate, 2); nh = Math.Round(th!.Value / taxRate, 2); }

                if (tl is null) Issue("missing", "缺价格", r.RowNo, "这些行没有价格，导入后标为「没有价格」，估算时不会计入");
                else
                {
                    if (th != tl) Issue("range", "区间价", r.RowNo, "价格写的是区间（如 36-40），按最低和最高记，估算时两个都给");
                    if (derived) Issue("derived", "推算含税", r.RowNo, $"只有未税价，含税价按未税 × {taxRate} 推算，并标为推算");
                }
                var pu = priceUnit ?? (unit is null ? null : $"元/{unit}");
                if (priceUnit is not null && unit is not null)
                {
                    var (pb, _) = PriceUnitOf(priceUnit);
                    if (pb != UnitBase(unit)) Issue("unit", "单位冲突", r.RowNo, "数量单位和计价单位对不上（如单位写「米」，旁边写「元/kg」），按计价单位导入，请确认");
                }
                if (Norm(unit ?? "") == "gk") Issue("typo", "单位疑似写错", r.RowNo, "单位写成了「GK」，按 kg 处理");
                items.Add(new ParsedPrice(r.RowNo, common.group, common.name, common.spec, common.material, o.Supplier, o.Model, common.code,
                    common.unit, pu, tl, th, nl, nh, derived, o.Lead, common.freq, common.note));
            }
        }

        if (map.Any(m => m.Field is "price" && m.Header.Length == 0))
            issues["noheader"] = ("价格列没有表头", [], "价格所在的列没有表头，是按内容认出来的，请确认");
        var list = issues.Select(kv => new PriceIssue(kv.Key, kv.Value.Label, kv.Value.Rows.Count, kv.Value.Rows.Take(20).ToList(), kv.Value.Msg))
            .OrderByDescending(i => i.Kind is "unit" or "missing" or "noheader").ThenByDescending(i => i.Count).ToList();
        return (items, list);
    }

    /// <summary>认文件名里的日期（HT20240409、2024-04-09、2024.4.9）。</summary>
    public static DateOnly? DateInName(string fileName)
    {
        var m = Regex.Match(fileName ?? "", @"(20\d{2})[-_.年]?(\d{1,2})[-_.月]?(\d{1,2})");
        if (!m.Success) return null;
        try { return new DateOnly(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>和上一版比：按识别键对上，比含税最低价。</summary>
    public static PriceDiff Diff(IEnumerable<(string Key, string Name, string? Spec, string? Supplier, decimal? Price)> old,
        IEnumerable<(string Key, string Name, string? Spec, string? Supplier, decimal? Price)> now)
    {
        var o = old.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First());
        var n = now.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First());
        int up = 0, down = 0, same = 0;
        var changes = new List<PriceChange>();
        foreach (var (k, v) in n)
        {
            if (!o.TryGetValue(k, out var before)) continue;
            if (before.Price is null || v.Price is null || before.Price == v.Price) { same++; continue; }
            var pct = (double)((v.Price.Value - before.Price.Value) / before.Price.Value * 100);
            if (pct > 0) up++; else down++;
            changes.Add(new PriceChange(v.Name, v.Spec, v.Supplier, before.Price, v.Price, Math.Round(pct, 1)));
        }
        return new PriceDiff(up, down, n.Keys.Count(k => !o.ContainsKey(k)), o.Keys.Count(k => !n.ContainsKey(k)), same,
            changes.OrderByDescending(c => Math.Abs(c.Pct)).Take(10).ToList());
    }
}

/// <summary>材料成本估算的一行（助手给用量，系统按价格库算价）。</summary>
public record CostLineInput(string Name, string? Spec, decimal? Qty, string? QtyUnit, decimal? PriceLow, decimal? PriceHigh, string? PriceUnit);
public record CostLineResult(string Status, decimal? Factor, decimal? SubLow, decimal? SubHigh, string? Why);

public static class CostMath
{
    private static readonly Dictionary<(string From, string To), decimal> Conv = new()
    {
        [("m", "mm")] = 1000m, [("mm", "m")] = 0.001m, [("kg", "g")] = 1000m, [("g", "kg")] = 0.001m, [("t", "kg")] = 1000m, [("kg", "t")] = 0.001m,
    };

    /// <summary>一行的小计：用量换成计价单位再乘单价。单位对不上、没有价格、没有用量都不算，并说明原因。</summary>
    public static CostLineResult Line(CostLineInput l)
    {
        if (l.PriceLow is null) return new("missing", null, null, null, "价格库里没有这一项的价格");
        if (l.Qty is null or <= 0) return new("noqty", null, null, null, "没有给用量");
        var (pb, per) = PriceSheet.PriceUnitOf(l.PriceUnit);
        var qb = PriceSheet.UnitBase(l.QtyUnit);
        decimal factor;
        if (string.IsNullOrEmpty(pb) || string.IsNullOrEmpty(qb) || pb == qb) factor = 1m;
        else if (Conv.TryGetValue((qb, pb), out var f)) factor = f;
        else return new("unit", null, null, null, $"用量单位「{l.QtyUnit}」和计价单位「{l.PriceUnit}」对不上");
        var qty = l.Qty.Value * factor / per;
        return new("ok", factor, Math.Round(qty * l.PriceLow.Value, 2), Math.Round(qty * (l.PriceHigh ?? l.PriceLow.Value), 2), null);
    }
}
