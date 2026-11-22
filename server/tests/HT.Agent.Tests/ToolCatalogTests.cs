using System.Reflection;
using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using Xunit;
using Xunit.Abstractions;

namespace HT.Agent.Tests;

public class ToolCatalogTests(ITestOutputHelper output)
{
    /// <summary>全部工具都开着时的工具清单（和线上组装的是同一个函数）。</summary>
    private static List<ToolSpec> AllTools()
    {
        var t = typeof(HT.Agent.Infrastructure.Services.AssistantService);
        var build = t.GetMethod("BuildTools", BindingFlags.NonPublic | BindingFlags.Static)!;
        var optional = (string[])t.GetField("OptionalTools", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var enabled = new HashSet<string>(optional) { "remember", "search_standards", "use_skill", "delegate", "browser_look" };
        var list = (List<ToolSpec>)build.Invoke(null, [true, true, true, enabled, new HashSet<string> { "docx", "xlsx", "pptx", "pdf" }, 4])!;
        list.Add((ToolSpec)t.GetMethod("MakePptTool", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!);
        return list;
    }

    private static int Chars(IEnumerable<ToolSpec> tools) => tools.Sum(x => JsonSerializer.Serialize(
        new { type = "function", function = new { name = x.Name, description = x.Description, parameters = x.Parameters } },
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Length);

    [Fact]
    public void Every_group_tool_exists_and_core_tools_stay()
    {
        var all = AllTools();
        var names = all.Select(x => x.Name).ToHashSet();
        foreach (var g in ToolCatalog.Groups)
            Assert.All(g.Tools, t => Assert.Contains(t, names));
        var live = ToolCatalog.Live(all, new HashSet<string>());
        foreach (var core in new[] { "search_documents", "read_document", "query_projects", "list_catalog", "web_search", "fetch_url", "calculate", "convert_unit", "use_skill", "plan", "ask_user" })
            Assert.Contains(live, x => x.Name == core);
        Assert.DoesNotContain(live, x => x.Name is "show_block" or "process_sim" or "make_ppt" or "browser_act");
        Assert.Contains(live, x => x.Name == ToolCatalog.FindTools);
    }

    [Fact]
    public void On_demand_sends_well_under_half_the_tool_text()
    {
        var all = AllTools();
        var live = ToolCatalog.Live(all, new HashSet<string>());
        var catalog = ToolCatalog.CatalogPrompt(ToolCatalog.Available(all)).Length;
        int before = Chars(all), after = Chars(live) + catalog;
        output.WriteLine($"全部带：{all.Count} 个 {before} 字；按需：{live.Count} 个 {Chars(live)} 字 + 目录 {catalog} 字 = {after} 字");
        Assert.True(after < before * 0.6, $"{after} / {before}");
    }

    [Fact]
    public void Loading_a_group_adds_all_its_tools_and_drops_find_tools_when_nothing_left()
    {
        var all = AllTools();
        var live = ToolCatalog.Live(all, new HashSet<string> { "browser" });
        Assert.Contains(live, x => x.Name == "browser_act");
        Assert.Contains(live, x => x.Name == "browser_look");
        var every = ToolCatalog.Live(all, ToolCatalog.Groups.Select(g => g.Code).ToHashSet());
        Assert.Equal(all.Count, every.Count);
        Assert.DoesNotContain(every, x => x.Name == ToolCatalog.FindTools);
    }

    [Fact]
    public void Preload_by_words_similarity_history_and_ui()
    {
        var avail = ToolCatalog.Available(AllTools());
        Assert.Contains("make_ppt", ToolCatalog.Preload(avail, "按 30L 加氢釜方案做个 ppt 给客户", null, 0.6, 2, [], [], []));
        Assert.Empty(ToolCatalog.Preload(avail, "你好", null, 0.6, 2, [], [], []));
        var sim = new Dictionary<string, double> { ["process_sim"] = 0.71, ["chemical_info"] = 0.65, ["hazard_lookup"] = 0.62, ["make_ppt"] = 0.4 };
        var s = ToolCatalog.Preload(avail, "苯加氢中试算一下各物流", sim, 0.6, 2, [], [], []);
        Assert.Equal(new[] { "chemical_info", "process_sim" }, s.OrderBy(x => x));
        // 之前用过的写工具名也认；界面上选的、后台常驻的
        var h = ToolCatalog.Preload(avail, "那再改一下", null, 0.6, 2, ["browser_act", "search_documents"], ["image"], ["show_block"]);
        Assert.Equal(new[] { "browser", "image", "show_block" }, h.OrderBy(x => x));
    }

    [Fact]
    public void Match_by_name_or_need()
    {
        var avail = ToolCatalog.Available(AllTools());
        Assert.Equal(["process_sim"], ToolCatalog.Match(avail, ["process_sim"], null));
        Assert.Equal(["browser"], ToolCatalog.Match(avail, ["browser_open"], null));
        Assert.Contains("process_sim", ToolCatalog.Match(avail, [], "算物料衡算和热量衡算"));
        Assert.Contains("translate_file", ToolCatalog.Match(avail, [], "把附件翻译成英文"));
        Assert.Contains("make_ppt", ToolCatalog.Match(avail, ["做 PPT"], null));   // names 里写成了要做的事
        Assert.Empty(ToolCatalog.Match(avail, ["no_such_tool"], null));
        Assert.Empty(ToolCatalog.Match(avail, [], "今天天气"));
    }

    [Fact]
    public void Skill_body_mentions_load_tools()
    {
        var avail = ToolCatalog.Available(AllTools());
        Assert.Equal(["process_sim"], ToolCatalog.Mentioned("2. 用 process_sim 算物料衡算，不要用 run_python", avail));
        Assert.Empty(ToolCatalog.Mentioned("show_blocks 和 my_process_sim 都不是", avail));
    }

    [Fact]
    public void Bad_arguments_get_a_clear_message()
    {
        var spec = AllTools().First(x => x.Name == "web_search");
        Assert.Null(ToolCatalog.CheckArgs(spec, "{\"query\":\"哈氏合金\"}", out var ok));
        Assert.Equal("哈氏合金", ok["query"]!.ToString());
        Assert.Null(ToolCatalog.CheckArgs(spec, "\"{\\\"query\\\":\\\"C276\\\"}\"", out var wrapped));   // 包了一层字符串也认
        Assert.Equal("C276", wrapped["query"]!.ToString());
        var missing = ToolCatalog.CheckArgs(spec, "{\"q\":\"x\"}", out _);
        Assert.Contains("缺必填参数：query", missing);
        Assert.Contains("- query（string，必填）", missing);
        Assert.Contains("不是合法的 JSON", ToolCatalog.CheckArgs(spec, "{query: 哈氏", out _));
        Assert.Contains("缺必填参数", ToolCatalog.CheckArgs(spec, "{\"query\":\"  \"}", out _));
        // 没有必填参数的工具：空参数没问题
        Assert.Null(ToolCatalog.CheckArgs(ToolCatalog.FindToolsSpec, "", out _));
    }
}
