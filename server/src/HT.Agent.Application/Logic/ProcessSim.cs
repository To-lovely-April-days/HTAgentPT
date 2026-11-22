using System.Globalization;
using System.Text.Json.Nodes;

namespace HT.Agent.Application.Logic;

/// <summary>流程模拟（DWSIM）：模型交来的流程描述 → 校验 → 一串 DWSIM 调用（单位都换成 DWSIM 要的 SI：K、Pa、kg/s、mol/s）→ 排版位置。
/// 纯函数，不连 DWSIM；执行在 ProcessSimRunner。第一版支持的设备见 Types，不支持循环（回流、循环气）。
/// 流程描述里的单位按工程习惯：温度 ℃、压力 MPa（表压）、质量流量 kg/h、摩尔流量 kmol/h、热负荷 kW、压降 MPa。</summary>
public static class ProcessSim
{
    public record Feed(string Id, string Label, double TC, double PMPaG, double? MassKgH, double? MoleKmolH, Dictionary<string, double> Comp);
    public record Unit(string Id, string Type, string Label, List<string> In, List<string> Out, JsonObject Raw);
    public record Reaction(string Name, Dictionary<string, double> Stoich, string Base, string Conversion, string Phase);
    public record Spec(string Title, List<string> Compounds, string Package, List<Reaction> Reactions, List<Feed> Feeds, List<Unit> Units);

    /// <summary>一步 DWSIM 调用：工具名、参数、出错时告诉模型是哪一步。</summary>
    public record Call(string Tool, JsonObject Args, string What);

    /// <summary>第一版支持的设备：DWSIM 类型 → 中文名、进口数、出口数（最少-最多）、要不要能量流（feed 进 / product 出）。</summary>
    public static readonly Dictionary<string, (string Name, int InMin, int InMax, int OutMin, int OutMax, string? Energy)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mixer"] = ("混合器", 2, 6, 1, 1, null),
        ["Splitter"] = ("分流器", 1, 1, 2, 3, null),
        ["Heater"] = ("加热器", 1, 1, 1, 1, "feed"),
        ["Cooler"] = ("冷却器", 1, 1, 1, 1, "product"),
        ["Pump"] = ("泵", 1, 1, 1, 1, "feed"),
        ["Compressor"] = ("压缩机", 1, 1, 1, 1, "feed"),
        ["Valve"] = ("阀", 1, 1, 1, 1, null),
        ["Vessel"] = ("气液分离罐（闪蒸）", 1, 3, 1, 2, null),
        ["ConversionReactor"] = ("转化反应器", 1, 3, 1, 2, "product"),
    };

    /// <summary>流程图组件（pfd）里的设备类型。</summary>
    public static string PfdType(string t) => t.ToLowerInvariant() switch
    {
        "mixer" or "splitter" => "mixer", "heater" => "heater", "cooler" => "cooler", "pump" => "pump", "compressor" => "compressor",
        "valve" => "valve", "vessel" => "separator", "conversionreactor" => "reactor", _ => "vessel",
    };

    public const double Atm = 101325;

    static double? Num(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (double.TryParse(v.ToString().Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        }
        return null;
    }

    static string Str(JsonNode? n) => n is JsonValue ? n.ToString().Trim() : "";

    static Dictionary<string, double> Map(JsonNode? n)
    {
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (n is JsonObject o) foreach (var kv in o) if (Num(kv.Value) is { } x) d[kv.Key.Trim()] = x;
        return d;
    }

    static List<string> Ids(JsonNode? n) => n switch
    {
        JsonArray a => a.Select(Str).Where(s => s.Length > 0).ToList(),
        JsonValue v when Str(v).Length > 0 => [Str(v)],
        _ => [],
    };

    /// <summary>读模型交来的流程描述，并把所有问题一次列出来（模型改起来省事）。</summary>
    public static (Spec? Spec, List<string> Errors) Parse(JsonObject a)
    {
        var errors = new List<string>();
        var compounds = Ids(a["compounds"]);
        if (compounds.Count == 0) errors.Add("compounds 要列出组分（DWSIM 的英文名，如 Water、Methanol、Hydrogen、Benzene）");
        if (compounds.Count > 20) errors.Add("组分最多 20 个");
        var package = Str(a["package"]);
        if (package.Length == 0) errors.Add("package 要写物性方法（如 Peng-Robinson (PR)、Soave-Redlich-Kwong (SRK)、NRTL、UNIQUAC、Raoult's Law、Steam Tables (IAPWS-IF97)）");

        var feeds = new List<Feed>();
        foreach (var f in (a["feeds"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = Str(f["id"]);
            var t = Num(f["T"]); var p = Num(f["P"]); var mass = Num(f["mass"]); var mole = Num(f["mole"]);
            var comp = Map(f["comp"]);
            if (id.Length == 0) { errors.Add("每股进料要有 id"); continue; }
            if (t is null || p is null) errors.Add($"进料 {id} 要给温度 T（℃）和压力 P（MPa 表压）");
            if (mass is null == mole is null) errors.Add($"进料 {id} 的流量 mass（kg/h）和 mole（kmol/h）给一个");
            if (comp.Count == 0) errors.Add($"进料 {id} 要给组成 comp（质量分率，如 {{\"Water\":0.6,\"Methanol\":0.4}}）");
            foreach (var c in comp.Keys.Where(c => !compounds.Contains(c, StringComparer.OrdinalIgnoreCase))) errors.Add($"进料 {id} 的组分 {c} 不在 compounds 里");
            if (p is < -0.1) errors.Add($"进料 {id} 的压力是表压，不能低于 -0.1 MPa");
            if (t is < -273) errors.Add($"进料 {id} 的温度不对");
            feeds.Add(new Feed(id, Str(f["label"]), t ?? 25, p ?? 0, mass, mole, comp));
        }
        if (feeds.Count == 0) errors.Add("feeds 至少要有一股进料");

        var units = new List<Unit>();
        foreach (var u in (a["units"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = Str(u["id"]); var type = Str(u["type"]);
            if (id.Length == 0) { errors.Add("每台设备要有 id（位号，如 R-101）"); continue; }
            if (!Types.TryGetValue(type, out var ty))
            {
                errors.Add($"设备 {id} 的类型「{type}」第一版不支持；能用的：{string.Join("、", Types.Select(x => $"{x.Key}（{x.Value.Name}）"))}");
                continue;
            }
            var ins = Ids(u["in"]); var outs = Ids(u["out"]);
            if (ins.Count < ty.InMin || ins.Count > ty.InMax) errors.Add($"设备 {id}（{ty.Name}）的进口 in 要 {ty.InMin}{(ty.InMax > ty.InMin ? $"–{ty.InMax}" : "")} 股");
            if (outs.Count < ty.OutMin || outs.Count > ty.OutMax) errors.Add($"设备 {id}（{ty.Name}）的出口 out 要 {ty.OutMin}{(ty.OutMax > ty.OutMin ? $"–{ty.OutMax}" : "")} 股"
                + (type.Equals("Vessel", StringComparison.OrdinalIgnoreCase) || type.Equals("ConversionReactor", StringComparison.OrdinalIgnoreCase) ? "（第 1 股气相、第 2 股液相）" : ""));
            var key = Types.Keys.First(k => k.Equals(type, StringComparison.OrdinalIgnoreCase));
            units.Add(new Unit(id, key, Str(u["label"]), ins, outs, u));
            errors.AddRange(UnitErrors(key, id, u, outs.Count));
        }
        if (units.Count == 0) errors.Add("units 至少要有一台设备");
        if (units.Count > 25) errors.Add("设备最多 25 台");

        var reactions = new List<Reaction>();
        foreach (var r in (a["reactions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = Str(r["name"]); var st = Map(r["stoich"]); var bas = Str(r["base"]); var conv = Num(r["conversion"]);
            if (name.Length == 0) name = $"R{reactions.Count + 1}";
            if (st.Count < 2) errors.Add($"反应 {name} 的计量系数 stoich 要写全（反应物为负，如 {{\"Benzene\":-1,\"Hydrogen\":-3,\"Cyclohexane\":1}}）");
            if (bas.Length == 0 || !st.TryGetValue(bas, out var bc) || bc >= 0) errors.Add($"反应 {name} 的基准组分 base 要是一个反应物");
            if (conv is not (> 0 and <= 100)) errors.Add($"反应 {name} 的转化率 conversion 要在 0–100（%）之间");
            foreach (var c in st.Keys.Where(c => !compounds.Contains(c, StringComparer.OrdinalIgnoreCase))) errors.Add($"反应 {name} 的组分 {c} 不在 compounds 里");
            var phase = Str(r["phase"]);
            reactions.Add(new Reaction(name, st, bas, (conv ?? 100).ToString(CultureInfo.InvariantCulture), phase is "Vapor" or "Liquid" ? phase : "Mixture"));
        }
        if (units.Any(u => u.Type == "ConversionReactor") && reactions.Count == 0) errors.Add("有转化反应器，要在 reactions 里写反应和转化率");

        // 物流连接：每股物流只能有一个来处（进料或某台设备的出口）、最多一个去处
        var producers = new Dictionary<string, string>();
        foreach (var f in feeds) if (!producers.TryAdd(f.Id, "进料")) errors.Add($"物流号 {f.Id} 重复了");
        foreach (var u in units) foreach (var o in u.Out)
            if (!producers.TryAdd(o, u.Id)) errors.Add($"物流 {o} 有两个来处（{producers[o]} 和 {u.Id}）");
        var consumers = new Dictionary<string, string>();
        foreach (var u in units) foreach (var i in u.In)
        {
            if (!producers.ContainsKey(i)) errors.Add($"设备 {u.Id} 的进口 {i} 没有来处：要么是 feeds 里的进料，要么是某台设备的出口");
            if (!consumers.TryAdd(i, u.Id)) errors.Add($"物流 {i} 同时进了 {consumers[i]} 和 {u.Id}，一股物流只能进一台设备（要分开就加 Splitter）");
        }
        foreach (var u in units.Where(u => producers.ContainsKey(u.Id) || consumers.ContainsKey(u.Id))) errors.Add($"设备位号 {u.Id} 和物流号重名了");
        foreach (var g in units.GroupBy(u => u.Id).Where(g => g.Count() > 1)) errors.Add($"设备位号 {g.Key} 重复了");
        if (errors.Count == 0 && HasCycle(units)) errors.Add("流程里有循环（循环气、回流），第一版不支持；先把循环断开，用估计的循环量作为进料来算");

        return errors.Count > 0 ? (null, errors)
            : (new Spec(Str(a["title"]) is { Length: > 0 } ti ? ti : "流程模拟", compounds, package, reactions, feeds, units), errors);
    }

    static List<string> UnitErrors(string type, string id, JsonObject u, int outs)
    {
        var e = new List<string>();
        bool has(string k) => Num(u[k]) is not null;
        switch (type)
        {
            case "Heater" or "Cooler":
                if (!has("T_out") && !has("duty") && !has("vf_out")) e.Add($"{id} 要给出口温度 T_out（℃）、热负荷 duty（kW）、出口汽化率 vf_out（0–1）之一");
                break;
            case "Pump" or "Compressor":
                if (!has("P_out") && !has("dP")) e.Add($"{id} 要给出口压力 P_out（MPa 表压）或升压 dP（MPa）");
                break;
            case "Valve":
                if (!has("P_out") && !has("dP")) e.Add($"{id} 要给出口压力 P_out（MPa 表压）或压降 dP（MPa）");
                break;
            case "Splitter":
                var ratio = (u["ratio"] as JsonArray ?? []).Select(Num).ToList();
                if (ratio.Count != outs || ratio.Any(r => r is not (>= 0 and <= 1)) || Math.Abs(ratio.Sum(r => r ?? 0) - 1) > 0.01)
                    e.Add($"{id} 要给分流比 ratio（每个出口一个，0–1，加起来等于 1）");
                break;
            case "ConversionReactor":
                var mode = Str(u["mode"]).ToLowerInvariant();
                if (!has("T_out") && mode is not ("adiabatic" or "isothermal")) e.Add($"{id} 要给出口温度 T_out（℃），或 mode 写 adiabatic（绝热）/ isothermal（等温，出口温度等于进口）");
                break;
        }
        return e;
    }

    static bool HasCycle(List<Unit> units)
    {
        var byOut = units.SelectMany(u => u.Out.Select(o => (o, u))).ToDictionary(x => x.o, x => x.u.Id);
        var next = units.ToDictionary(u => u.Id, u => units.Where(v => v.In.Any(i => u.Out.Contains(i))).Select(v => v.Id).ToList());
        var state = new Dictionary<string, int>();
        bool Dfs(string n)
        {
            state[n] = 1;
            foreach (var m in next[n])
            {
                if (state.GetValueOrDefault(m) == 1) return true;
                if (state.GetValueOrDefault(m) == 0 && Dfs(m)) return true;
            }
            state[n] = 2;
            return false;
        }
        return units.Any(u => state.GetValueOrDefault(u.Id) == 0 && Dfs(u.Id));
    }

    static double K(double c) => c + 273.15;
    static double Pa(double mpaG) => mpaG * 1e6 + Atm;

    /// <summary>设备的设定：流程描述的工程单位 → DWSIM 的属性名和 SI 值（K、Pa、kW）。在真的 DWSIM 10.2 上核对过属性名。</summary>
    public static JsonObject UnitProps(Unit u)
    {
        var r = u.Raw; var p = new JsonObject();
        double? n(string k) => Num(r[k]);
        switch (u.Type)
        {
            case "Heater" or "Cooler":
                if (n("T_out") is { } t) { p["CalcMode"] = "OutletTemperature"; p["OutletTemperature"] = K(t); }
                else if (n("duty") is { } q) { p["CalcMode"] = u.Type == "Heater" ? "HeatAdded" : "HeatRemoved"; p["HeatDuty"] = Math.Abs(q); }
                else if (n("vf_out") is { } vf) { p["CalcMode"] = "OutletVaporFraction"; p["OutletVaporFraction"] = vf; }
                if (n("dP") is { } dp) p["DeltaP"] = dp * 1e6;
                break;
            case "Pump":
                if (n("P_out") is { } po) { p["CalcMode"] = "OutletPressure"; p["Pout"] = Pa(po); }
                else if (n("dP") is { } dpp) { p["CalcMode"] = "Delta_P"; p["DeltaP"] = dpp * 1e6; }
                if (n("efficiency") is { } ef) p["Efficiency"] = ef;
                break;
            case "Compressor":
                if (n("P_out") is { } pc) { p["CalcMode"] = "OutletPressure"; p["POut"] = Pa(pc); }
                else if (n("dP") is { } dpc) { p["CalcMode"] = "Delta_P"; p["DeltaP"] = dpc * 1e6; }
                if (n("efficiency") is { } ec) p["AdiabaticEfficiency"] = ec;
                break;
            case "Valve":
                if (n("P_out") is { } pv) { p["CalcMode"] = "OutletPressure"; p["OutletPressure"] = Pa(pv); }
                else if (n("dP") is { } dpv) { p["CalcMode"] = "DeltaP"; p["DeltaP"] = dpv * 1e6; }
                break;
            case "Splitter":
                var ratio = (r["ratio"] as JsonArray ?? []).Select(Num).ToList();
                p["OperationMode"] = "SplitRatios";
                for (var i = 0; i < ratio.Count - 1; i++) p[$"SR{i + 1}"] = ratio[i];
                break;
            case "ConversionReactor":
                if (n("T_out") is { } tr) { p["ReactorOperationMode"] = "OutletTemperature"; p["OutletTemperature"] = K(tr); }
                else p["ReactorOperationMode"] = Str(r["mode"]).Equals("adiabatic", StringComparison.OrdinalIgnoreCase) ? "Adiabatic" : "Isothermic";
                if (n("dP") is { } dr) p["DeltaP"] = dr * 1e6;
                break;
        }
        return p;
    }

    public static string EnergyId(Unit u) => $"Q-{u.Id}";

    /// <summary>流程描述 → DWSIM 调用顺序。flowsheet_id 由执行时填。</summary>
    public static List<Call> Plan(Spec s)
    {
        var calls = new List<Call>
        {
            new("dwsim_thermo_add_compounds", new() { ["names"] = new JsonArray(s.Compounds.Select(c => (JsonNode?)c).ToArray()) }, "加组分"),
            new("dwsim_thermo_set_property_package", new() { ["name"] = s.Package }, "设物性方法"),
        };
        foreach (var r in s.Reactions)
            calls.Add(new("dwsim_reaction_add_conversion", new()
            {
                ["name"] = r.Name, ["stoichiometry"] = new JsonObject(r.Stoich.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["base_compound"] = r.Base, ["conversion"] = r.Conversion, ["phase"] = r.Phase,
            }, $"反应 {r.Name}"));
        foreach (var f in s.Feeds)
        {
            var args = new JsonObject
            {
                ["name"] = f.Id, ["temperature_K"] = K(f.TC), ["pressure_Pa"] = Pa(f.PMPaG),
                ["composition"] = new JsonObject(f.Comp.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            };
            if (f.MassKgH is { } m) args["mass_flow_kg_s"] = m / 3600; else args["molar_flow_mol_s"] = f.MoleKmolH!.Value * 1000 / 3600;
            calls.Add(new("dwsim_stream_add_material", args, $"进料 {f.Id}"));
        }
        foreach (var id in s.Units.SelectMany(u => u.Out).Distinct())
            calls.Add(new("dwsim_stream_add_material", new() { ["name"] = id }, $"物流 {id}"));
        foreach (var u in s.Units)
        {
            calls.Add(new("dwsim_unitop_add", new() { ["type"] = u.Type, ["name"] = u.Id }, $"设备 {u.Id}"));
            if (Types[u.Type].Energy is not null) calls.Add(new("dwsim_stream_add_energy", new() { ["name"] = EnergyId(u) }, $"{u.Id} 的能量流"));
        }
        foreach (var u in s.Units)
        {
            for (var i = 0; i < u.In.Count; i++)
                calls.Add(new("dwsim_unitop_connect", new() { ["unitop"] = u.Id, ["feed_stream"] = u.In[i], ["feed_port"] = i }, $"{u.Id} 进口 {u.In[i]}"));
            for (var i = 0; i < u.Out.Count; i++)
                calls.Add(new("dwsim_unitop_connect", new() { ["unitop"] = u.Id, ["product_stream"] = u.Out[i], ["product_port"] = i }, $"{u.Id} 出口 {u.Out[i]}"));
            if (Types[u.Type].Energy is { } e)
                calls.Add(new("dwsim_unitop_connect", new() { ["unitop"] = u.Id, [e == "feed" ? "energy_feed" : "energy_product"] = EnergyId(u) }, $"{u.Id} 的能量流"));
            var props = UnitProps(u);
            if (props.Count > 0) calls.Add(new("dwsim_unitop_set", new() { ["name"] = u.Id, ["properties"] = props }, $"{u.Id} 的设定"));
        }
        return calls;
    }

    /// <summary>排版：设备按流程先后分列（左进右出），进料在最左，中间物流放在两台设备之间，产品放在设备右边，能量流放在设备下面。
    /// 坐标给 DWSIM（下载的文件打开就是排好的），也给它的截图。</summary>
    public static Dictionary<string, (int X, int Y)> Layout(Spec s)
    {
        var producer = s.Units.SelectMany(u => u.Out.Select(o => (o, u))).ToDictionary(x => x.o, x => x.u);
        var col = new Dictionary<string, int>();
        int Col(Unit u)
        {
            if (col.TryGetValue(u.Id, out var c)) return c;
            col[u.Id] = 1;
            var v = 1 + u.In.Select(i => producer.TryGetValue(i, out var p) ? Col(p) : 0).DefaultIfEmpty(0).Max();
            return col[u.Id] = v;
        }
        foreach (var u in s.Units) Col(u);
        var pos = new Dictionary<string, (int X, int Y)>();
        const int W = 230, H = 200, X0 = 60, Y0 = 120;
        foreach (var g in s.Units.GroupBy(u => col[u.Id]))
        {
            var row = 0;
            foreach (var u in g) pos[u.Id] = (X0 + g.Key * W, Y0 + row++ * H);
        }
        foreach (var u in s.Units)
        {
            var (ux, uy) = pos[u.Id];
            if (Types[u.Type].Energy is not null) pos[EnergyId(u)] = (ux + 10, uy + 130);
            for (var i = 0; i < u.In.Count; i++)
                if (!producer.ContainsKey(u.In[i])) pos[u.In[i]] = (Math.Max(20, ux - W + 20), uy - 40 + i * 90);
            for (var i = 0; i < u.Out.Count; i++)
            {
                var o = u.Out[i];
                var next = s.Units.FirstOrDefault(v => v.In.Contains(o));
                pos[o] = next is null ? (ux + 120, uy - 30 + i * 110) : ((ux + (pos.TryGetValue(next.Id, out var np) ? np.X : ux + W)) / 2 + 20, uy + 10 + i * 110);
            }
        }
        return pos;
    }
}
