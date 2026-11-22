using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Clients;

namespace HT.Agent.Infrastructure.Services;

/// <summary>跑一次流程模拟：按 ProcessSim.Plan 建流程、排好位置、求解、取物流和设备结果、存 DWSIM 文件、拿 DWSIM 自己画的流程图。
/// 每次都是新建、算完就关（无状态）；「换条件再算」就是再跑一次。</summary>
public class ProcessSimRunner(DwsimClient dwsim, IRuntimeConfig config)
{
    public record Outcome(bool Ok, string? Problem, bool Unavailable, JsonObject? Card, JsonObject? Pfd, string Text, List<double> Numbers,
        byte[]? File, byte[]? Image);

    static string F(double v, int d) => Math.Round(v, d).ToString(CultureInfo.InvariantCulture);

    public async Task<Outcome> RunAsync(ProcessSim.Spec s, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string fid;
        try { fid = (await dwsim.CallAsync("dwsim_flowsheet_create", new() { ["name"] = s.Title }, ct))?["flowsheet_id"]?.ToString() ?? throw new DwsimClient.DwsimException("没拿到流程编号", true); }
        catch (DwsimClient.DwsimException ex) { return Fail(ex.Message, ex.Unavailable); }
        try
        {
            var plan = ProcessSim.Plan(s);
            for (var i = 0; i < plan.Count; i++)
            {
                var c = plan[i];
                var args = (JsonObject)c.Args.DeepClone();
                args["flowsheet_id"] = fid;
                try { await dwsim.CallAsync(c.Tool, args, ct); }
                catch (DwsimClient.DwsimException ex) { return Fail($"第 {i + 1} 步（{c.What}）DWSIM 报错：{ex.Message}", ex.Unavailable); }
            }
            // 排版：下载的文件打开就是排好的，截图也好看。挪不动不影响计算
            foreach (var (tag, (x, y)) in ProcessSim.Layout(s))
                try { await dwsim.CallAsync("dwsim_graphic_edit", new() { ["flowsheet_id"] = fid, ["name"] = tag, ["x"] = x, ["y"] = y }, ct); }
                catch (DwsimClient.DwsimException ex) when (!ex.Unavailable) { }

            JsonNode? check;
            try { check = await dwsim.CallAsync("dwsim_flowsheet_check", new() { ["flowsheet_id"] = fid }, ct); }
            catch (DwsimClient.DwsimException ex) { return Fail($"检查流程时出错：{ex.Message}", ex.Unavailable); }
            if ((check?["blockers"]?.GetValue<int>() ?? 0) > 0)
                return Fail("流程还没法算：" + Findings(check, "blocker"), false);

            JsonNode? solve;
            try { solve = await dwsim.CallAsync("dwsim_solve_run", new() { ["flowsheet_id"] = fid, ["timeout_s"] = 120 }, ct); }
            catch (DwsimClient.DwsimException ex) { return Fail($"求解出错：{ex.Message}", ex.Unavailable); }
            if (solve?["ok"]?.GetValue<bool>() != true)
            {
                var errs = (solve?["errors"] as JsonArray ?? []).Select(e => e?.ToString()).Where(e => !string.IsNullOrEmpty(e)).Take(5);
                var objErrs = (solve?["objects"] as JsonArray ?? []).OfType<JsonObject>().Where(o => (o["error"]?.ToString() ?? "").Length > 0)
                    .Select(o => $"{o["name"]}：{o["error"]}").Take(5);
                return Fail("没算收敛：" + string.Join("；", errs.Concat(objErrs).DefaultIfEmpty("DWSIM 没给原因")) + Findings(solve, null), false);
            }

            // 物流结果
            var streamIds = s.Feeds.Select(f => f.Id).Concat(s.Units.SelectMany(u => u.Out)).Distinct().ToList();
            var from = s.Units.SelectMany(u => u.Out.Select(o => (o, u.Id))).ToDictionary(x => x.o, x => x.Id);
            var to = s.Units.SelectMany(u => u.In.Select(i => (i, u.Id))).ToDictionary(x => x.i, x => x.Id);
            var streams = new JsonArray();
            var numbers = new List<double>();
            var text = new StringBuilder($"流程模拟「{s.Title}」已收敛（DWSIM，物性方法 {s.Package}）。\n物流（温度 ℃、压力 MPa 表压、质量流量 kg/h、摩尔流量 kmol/h、汽相分率、摩尔组成）：\n");
            var pfdStreams = new JsonArray();
            foreach (var id in streamIds)
            {
                JsonNode? r;
                try { r = await dwsim.CallAsync("dwsim_stream_get_results", new() { ["flowsheet_id"] = fid, ["name"] = id }, ct); }
                catch (DwsimClient.DwsimException) { continue; }
                if (r is null) continue;
                var t = r["temperature_K"]!.GetValue<double>() - 273.15;
                var p = (r["pressure_Pa"]!.GetValue<double>() - ProcessSim.Atm) / 1e6;
                var m = r["mass_flow_kg_s"]!.GetValue<double>() * 3600;
                var n = r["molar_flow_mol_s"]!.GetValue<double>() * 3.6;
                var phases = (r["phases"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                var mix = phases.FirstOrDefault(x => x["name"]?.ToString() == "Mixture");
                var vf = phases.FirstOrDefault(x => x["name"]?.ToString() == "Vapor")?["fraction"]?.GetValue<double>() ?? 0;
                var mole = new JsonObject(); var mass = new JsonObject();
                foreach (var kv in mix?["compounds"] as JsonObject ?? [])
                {
                    var mf = kv.Value?["mole_fraction"]?.GetValue<double>() ?? 0;
                    var wf = kv.Value?["mass_fraction"]?.GetValue<double>() ?? 0;
                    if (mf > 1e-6) mole[kv.Key] = Math.Round(mf, 4);
                    if (wf > 1e-6) mass[kv.Key] = Math.Round(wf * 100, 2);
                }
                var label = s.Feeds.FirstOrDefault(f => f.Id == id)?.Label ?? "";
                streams.Add(new JsonObject
                {
                    ["id"] = id, ["label"] = label, ["from"] = from.GetValueOrDefault(id), ["to"] = to.GetValueOrDefault(id),
                    ["T"] = Math.Round(t, 1), ["P"] = Math.Round(p, 3), ["mass"] = Math.Round(m, 3), ["mole"] = Math.Round(n, 4), ["vf"] = Math.Round(vf, 3), ["comp"] = mole,
                });
                pfdStreams.Add(new JsonObject
                {
                    ["id"] = id, ["from"] = from.GetValueOrDefault(id), ["to"] = to.GetValueOrDefault(id), ["label"] = label,
                    ["T"] = $"{F(t, 1)} ℃", ["P"] = $"{F(p, 3)} MPa", ["flow"] = Math.Round(m, 3), ["unit"] = "kg/h",
                    ["phase"] = vf >= 0.999 ? "气" : vf <= 0.001 ? "液" : $"气液（汽相 {F(vf * 100, 0)}%）", ["comp"] = mass,
                });
                numbers.AddRange([Math.Round(t, 1), Math.Round(t, 0), Math.Round(p, 3), Math.Round(p, 2), Math.Round(m, 3), Math.Round(m, 2), Math.Round(m, 1), Math.Round(n, 4), Math.Round(n, 3)]);
                text.AppendLine($"- {id}{(label.Length > 0 ? $"（{label}）" : "")}：{F(t, 1)} ℃，{F(p, 3)} MPa，{F(m, 3)} kg/h，{F(n, 4)} kmol/h，汽相 {F(vf, 3)}，"
                    + string.Join("、", mole.Select(kv => $"{kv.Key} {kv.Value}")));
            }

            // 存文件（DWSIM 写到共用目录，后端从同一个路径读），顺便从文件里取能量流（热负荷、功率，kW）
            byte[]? file = null;
            var duties = new Dictionary<string, double>();
            var dir = await config.GetStringAsync(ConfigKeys.DwsimFilesDir, "/dwsim-files", ct);
            var path = Path.Combine(dir, $"{Guid.NewGuid():N}.dwxmz");
            try
            {
                await dwsim.CallAsync("dwsim_flowsheet_save", new() { ["flowsheet_id"] = fid, ["filepath"] = path, ["compressed"] = true }, ct);
                if (File.Exists(path))
                {
                    file = await File.ReadAllBytesAsync(path, ct);
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    duties = EnergyFlows(file);
                }
            }
            catch (DwsimClient.DwsimException) { }

            var units = new JsonArray();
            text.AppendLine("设备：");
            foreach (var u in s.Units)
            {
                JsonNode? r = null;
                try { r = await dwsim.CallAsync("dwsim_unitop_get_results", new() { ["flowsheet_id"] = fid, ["name"] = u.Id }, ct); }
                catch (DwsimClient.DwsimException) { }
                var props = new JsonArray();
                foreach (var kv in r?["properties"] as JsonObject ?? [])
                {
                    var v = kv.Value?["value"]?.ToString() ?? "";
                    if (v.Length == 0 || kv.Key is "Calculation Mode") continue;
                    var shown = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) ? F(dv, Math.Abs(dv) >= 100 ? 1 : 3) : v;
                    props.Add(new JsonObject { ["name"] = kv.Key, ["value"] = shown, ["units"] = kv.Value?["units"]?.ToString() ?? "" });
                }
                double? duty = duties.TryGetValue(ProcessSim.EnergyId(u), out var q) ? Math.Round(q, 3) : null;
                if (duty is { } dq) numbers.AddRange([dq, Math.Round(dq, 2), Math.Round(dq, 1), Math.Round(-dq, 2), Math.Round(-dq, 1)]);
                units.Add(new JsonObject
                {
                    ["id"] = u.Id, ["type"] = u.Type, ["typeName"] = ProcessSim.Types[u.Type].Name, ["label"] = u.Label, ["duty"] = duty,
                    ["dutyKind"] = DutyKind(u.Type), ["props"] = props,
                });
                text.AppendLine($"- {u.Id}（{ProcessSim.Types[u.Type].Name}{(u.Label.Length > 0 ? "，" + u.Label : "")}）"
                    + (duty is { } d ? $"：{DutyKind(u.Type)} {F(d, 3)} kW" : "")
                    + (props.Count > 0 ? "；" + string.Join("，", props.Select(x => $"{x!["name"]} {x["value"]} {x["units"]}")) : ""));
            }
            var notes = Findings(solve, null);
            if (notes.Length > 0) text.AppendLine("DWSIM 的提示：" + notes);

            byte[]? image = null;
            try
            {
                var shot = await dwsim.CallAsync("dwsim_graphic_screenshot", new() { ["flowsheet_id"] = fid }, ct);
                if (shot?["base64_png"]?.ToString() is { Length: > 0 } b64) image = Convert.FromBase64String(b64);
            }
            catch (Exception ex) when (ex is DwsimClient.DwsimException or FormatException) { }

            text.AppendLine("流程图、物流表、设备表已经显示给用户（还能下载 DWSIM 文件）。回答里写清物性方法和为什么选它、关键结果、哪些是给定条件哪些是算出来的；不要把整张物流表再写一遍。");
            var card = new JsonObject
            {
                ["kind"] = "sim", ["title"] = s.Title, ["package"] = s.Package, ["compounds"] = new JsonArray(s.Compounds.Select(c => (JsonNode?)c).ToArray()),
                ["streams"] = streams, ["units"] = units, ["notes"] = notes, ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            };
            var pfd = new JsonObject
            {
                ["units"] = new JsonArray(s.Units.Select(u => (JsonNode?)new JsonObject { ["id"] = u.Id, ["type"] = ProcessSim.PfdType(u.Type), ["label"] = u.Label.Length > 0 ? u.Label : ProcessSim.Types[u.Type].Name }).ToArray()),
                ["streams"] = pfdStreams,
            };
            return new Outcome(true, null, false, card, pfd, text.ToString(), numbers, file, image);
        }
        finally
        {
            try { await dwsim.CallAsync("dwsim_flowsheet_close", new() { ["flowsheet_id"] = fid }, CancellationToken.None); }
            catch (DwsimClient.DwsimException) { }
        }

        static Outcome Fail(string why, bool unavailable) => new(false, why, unavailable, null, null, why, [], null, null);
    }

    /// <summary>能量流的意思：接在出口一侧的是撤走的热（冷却器、反应器），进口一侧的是加进去的热或功。</summary>
    static string DutyKind(string type) => type switch
    {
        "Pump" or "Compressor" => "功率",
        _ when ProcessSim.Types[type].Energy == "product" => "撤热",
        _ => "加热",
    };

    /// <summary>DWSIM 的诊断提示（blocker / warning / info），一条一句。</summary>
    static string Findings(JsonNode? r, string? severity)
        => string.Join("；", (r?["findings"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(f => severity is null || f["severity"]?.ToString() == severity)
            .Select(f => $"{f["object"]}：{f["message"]}{(f["fix"] is { } fx ? $"（{fx}）" : "")}").Take(6));

    /// <summary>从存下来的 .dwxmz（zip 里一个 XML）取能量流：位号 → kW。</summary>
    internal static Dictionary<string, double> EnergyFlows(byte[] dwxmz)
    {
        var map = new Dictionary<string, double>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(dwxmz), ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return map;
            using var st = entry.Open();
            var root = XDocument.Load(st).Root!;
            var tags = (root.Element("GraphicObjects")?.Elements() ?? [])
                .Select(g => (Name: g.Element("Name")?.Value, Tag: g.Element("Tag")?.Value))
                .Where(x => x.Name is not null && x.Tag is not null).GroupBy(x => x.Name!).ToDictionary(g => g.Key, g => g.First().Tag!);
            foreach (var o in root.Element("SimulationObjects")?.Elements() ?? [])
            {
                if (!(o.Element("Type")?.Value ?? "").Contains("EnergyStream")) continue;
                var name = o.Element("Name")?.Value;
                if (name is null || !tags.TryGetValue(name, out var tag)) continue;
                if (double.TryParse(o.Element("EnergyFlow")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kw)) map[tag] = kw;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { }
        return map;
    }
}
