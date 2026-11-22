using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Clients;
using HT.Agent.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace HT.Agent.Tests;

/// <summary>流程模拟（DWSIM）：流程描述的校验、换算成 DWSIM 调用、排版、结果解析；另有一个连真 DWSIM 的测试（设了 DWSIM_LIVE_URL 才跑）。</summary>
public class ProcessSimTests
{
    const string Benzene = """
    {"title":"苯加氢中试","compounds":["Benzene","Hydrogen","Cyclohexane"],"package":"Peng-Robinson (PR)",
     "reactions":[{"name":"加氢","stoich":{"Benzene":-1,"Hydrogen":-3,"Cyclohexane":1},"base":"Benzene","conversion":99}],
     "feeds":[{"id":"1","label":"苯","T":25,"P":3.0,"mass":10,"comp":{"Benzene":1}},
              {"id":"2","label":"氢气","T":25,"P":3.0,"mole":0.448,"comp":{"Hydrogen":1}}],
     "units":[{"id":"M-101","type":"Mixer","in":["1","2"],"out":["3"]},
              {"id":"R-101","type":"ConversionReactor","label":"加氢反应器","in":["3"],"out":["4","5"],"T_out":180},
              {"id":"E-101","type":"Cooler","in":["4"],"out":["6"],"T_out":40,"dP":0.05},
              {"id":"V-101","type":"Vessel","in":["6"],"out":["7","8"]}]}
    """;

    static JsonObject J(string s) => JsonNode.Parse(s)!.AsObject();

    [Fact]
    public void Valid_spec_parses_and_converts_to_si()
    {
        var (spec, errors) = ProcessSim.Parse(J(Benzene));
        Assert.Empty(errors);
        var plan = ProcessSim.Plan(spec!);
        var feed = plan.First(c => c.Tool == "dwsim_stream_add_material" && c.Args["name"]!.ToString() == "1").Args;
        Assert.Equal(298.15, feed["temperature_K"]!.GetValue<double>(), 6);
        Assert.Equal(3.101325e6, feed["pressure_Pa"]!.GetValue<double>(), 1);
        Assert.Equal(10.0 / 3600, feed["mass_flow_kg_s"]!.GetValue<double>(), 9);
        Assert.Equal(0.448 * 1000 / 3600, plan.First(c => c.Args["name"]?.ToString() == "2").Args["molar_flow_mol_s"]!.GetValue<double>(), 9);
        var cooler = plan.Single(c => c.Tool == "dwsim_unitop_set" && c.Args["name"]!.ToString() == "E-101").Args["properties"]!;
        Assert.Equal("OutletTemperature", cooler["CalcMode"]!.ToString());
        Assert.Equal(313.15, cooler["OutletTemperature"]!.GetValue<double>(), 6);
        Assert.Equal(50000, cooler["DeltaP"]!.GetValue<double>(), 3);
        var reactor = plan.Single(c => c.Tool == "dwsim_unitop_set" && c.Args["name"]!.ToString() == "R-101").Args["properties"]!;
        Assert.Equal("OutletTemperature", reactor["ReactorOperationMode"]!.ToString());
        Assert.Equal(453.15, reactor["OutletTemperature"]!.GetValue<double>(), 6);
        Assert.Contains(plan, c => c.Tool == "dwsim_reaction_add_conversion" && c.Args["phase"]!.ToString() == "Mixture");
        // 能量流：反应器、冷却器各一股，接在出的一侧
        Assert.Contains(plan, c => c.Tool == "dwsim_unitop_connect" && c.Args["energy_product"]?.ToString() == "Q-R-101");
        Assert.Contains(plan, c => c.Tool == "dwsim_unitop_connect" && c.Args["energy_product"]?.ToString() == "Q-E-101");
        // 分离罐两个出口按顺序接 0（气）、1（液）
        Assert.Contains(plan, c => c.Tool == "dwsim_unitop_connect" && c.Args["product_stream"]?.ToString() == "8" && c.Args["product_port"]!.GetValue<int>() == 1);
    }

    [Fact]
    public void Problems_are_all_listed()
    {
        var bad = J("""
        {"compounds":["Water"],"package":"NRTL",
         "feeds":[{"id":"1","T":25,"P":0.1,"comp":{"Water":1,"Salt":0}}],
         "units":[{"id":"H-1","type":"Heater","in":["1"],"out":["2"]},
                  {"id":"X-1","type":"DistillationColumn","in":["2"],"out":["3","4"]},
                  {"id":"P-1","type":"Pump","in":["9"],"out":["5"],"P_out":0.5}]}
        """);
        var (spec, errors) = ProcessSim.Parse(bad);
        Assert.Null(spec);
        Assert.Contains(errors, e => e.Contains("mass") && e.Contains("mole"));
        Assert.Contains(errors, e => e.Contains("Salt"));
        Assert.Contains(errors, e => e.Contains("H-1") && e.Contains("T_out"));
        Assert.Contains(errors, e => e.Contains("DistillationColumn") && e.Contains("不支持"));
        Assert.Contains(errors, e => e.Contains("9") && e.Contains("没有来处"));
    }

    [Fact]
    public void Loops_and_shared_streams_are_refused()
    {
        var loop = J("""
        {"compounds":["Water"],"package":"NRTL","feeds":[{"id":"1","T":25,"P":0.1,"mass":1,"comp":{"Water":1}}],
         "units":[{"id":"M-1","type":"Mixer","in":["1","4"],"out":["2"]},{"id":"S-1","type":"Splitter","in":["2"],"out":["3","4"],"ratio":[0.5,0.5]}]}
        """);
        Assert.Contains(ProcessSim.Parse(loop).Errors, e => e.Contains("循环"));
        var shared = J("""
        {"compounds":["Water"],"package":"NRTL","feeds":[{"id":"1","T":25,"P":0.1,"mass":1,"comp":{"Water":1}}],
         "units":[{"id":"H-1","type":"Heater","in":["1"],"out":["2"],"T_out":50},{"id":"H-2","type":"Heater","in":["1"],"out":["3"],"T_out":60}]}
        """);
        Assert.Contains(ProcessSim.Parse(shared).Errors, e => e.Contains("同时进了"));
    }

    [Fact]
    public void Layout_places_every_object_left_to_right_without_overlap()
    {
        var (spec, _) = ProcessSim.Parse(J(Benzene));
        var pos = ProcessSim.Layout(spec!);
        foreach (var id in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "M-101", "R-101", "E-101", "V-101", "Q-R-101", "Q-E-101" }) Assert.True(pos.ContainsKey(id), id);
        Assert.True(pos["M-101"].X < pos["R-101"].X && pos["R-101"].X < pos["E-101"].X && pos["E-101"].X < pos["V-101"].X);
        Assert.Equal(pos.Count, pos.Values.Distinct().Count());
    }

    [Fact]
    public void Rpc_errors_and_tool_errors_carry_dwsim_words()
    {
        Assert.Equal("x1", DwsimClient.Unwrap("""{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"{\"flowsheet_id\":\"x1\"}"}]}}""")!["flowsheet_id"]!.ToString());
        var e1 = Assert.Throws<DwsimClient.DwsimException>(() => DwsimClient.Unwrap("""{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"Missing required parameter: name"}}"""));
        Assert.Contains("Missing required parameter", e1.Message);
        Assert.False(e1.Unavailable);
        var e2 = Assert.Throws<DwsimClient.DwsimException>(() => DwsimClient.Unwrap("""{"jsonrpc":"2.0","id":1,"result":{"isError":true,"content":[{"type":"text","text":"{\"error\":\"There isn't any stream with tag '9'.\"}"}]}}"""));
        Assert.Contains("tag '9'", e2.Message);
    }

    [Fact]
    public void Energy_flows_come_from_the_saved_file()
    {
        var xml = """
        <DWSIM_Simulation_Data><SimulationObjects>
          <SimulationObject><Type>DWSIM.UnitOperations.Streams.EnergyStream</Type><Name>EN-1</Name><EnergyFlow>5.894</EnergyFlow></SimulationObject>
          <SimulationObject><Type>DWSIM.Thermodynamics.Streams.MaterialStream</Type><Name>MAT-1</Name></SimulationObject>
        </SimulationObjects><GraphicObjects>
          <GraphicObject><Name>EN-1</Name><Tag>Q-R-101</Tag></GraphicObject><GraphicObject><Name>MAT-1</Name><Tag>1</Tag></GraphicObject>
        </GraphicObjects></DWSIM_Simulation_Data>
        """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        using (var w = new StreamWriter(zip.CreateEntry("abc.xml").Open())) w.Write(xml);
        var map = ProcessSimRunner.EnergyFlows(ms.ToArray());
        Assert.Equal(5.894, map["Q-R-101"], 6);
        Assert.Single(map);
    }

    // ── 连真的 DWSIM（本机起了 dwsim-mcp 时手动跑：DWSIM_LIVE_URL、DWSIM_TOKEN、DWSIM_FILES_DIR）──────
    sealed class Cfg(Dictionary<string, string> v) : IRuntimeConfig
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(v.GetValueOrDefault(key));
        public Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<double> GetDoubleAsync(string key, double fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<string> GetStringAsync(string key, string fallback, CancellationToken ct = default) => Task.FromResult(v.GetValueOrDefault(key) ?? fallback);
        public Task SetAsync(string key, string value, Guid? updatedBy, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(v);
    }

    sealed class Http : IHttpClientFactory { public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(200) }; }

    [Fact]
    public async Task Live_dwsim_benzene_hydrogenation()
    {
        var url = Environment.GetEnvironmentVariable("DWSIM_LIVE_URL");
        if (string.IsNullOrEmpty(url)) return;   // 没起 DWSIM 就不跑
        var dir = Environment.GetEnvironmentVariable("DWSIM_FILES_DIR") ?? Path.GetTempPath();
        var cfg = new Cfg(new() { [ConfigKeys.DwsimUrl] = url, [ConfigKeys.DwsimFilesDir] = dir });
        var app = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DWSIM_TOKEN"] = Environment.GetEnvironmentVariable("DWSIM_TOKEN") }).Build();
        var runner = new ProcessSimRunner(new DwsimClient(new Http(), cfg, app), cfg);
        var (spec, errors) = ProcessSim.Parse(J(Benzene));
        Assert.Empty(errors);
        var r = await runner.RunAsync(spec!, default);
        Assert.True(r.Ok, r.Problem);
        var s7 = r.Card!["streams"]!.AsArray().First(x => x!["id"]!.ToString() == "8")!;
        Assert.True(s7["comp"]!["Cyclohexane"]!.GetValue<double>() > 0.9);
        var reactor = r.Card["units"]!.AsArray().First(x => x!["id"]!.ToString() == "R-101")!;
        Assert.InRange(Math.Abs(reactor["duty"]!.GetValue<double>()), 5, 7);
        Assert.NotNull(r.File);
        Assert.NotNull(r.Image);
        File.WriteAllText(Path.Combine(dir, "live-result.txt"), r.Text + "\n" + r.Card.ToJsonString());
        File.WriteAllBytes(Path.Combine(dir, "live-shot.png"), r.Image!);
    }
}
