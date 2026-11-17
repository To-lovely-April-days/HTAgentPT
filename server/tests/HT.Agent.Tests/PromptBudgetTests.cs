using System.Text;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Services;

namespace HT.Agent.Tests;

/// <summary>提示词预算：技术协议按设备展开后有几百项，本地模型上下文 8K，每轮只给模型看挑出来的一部分；
/// 以及「仍然生成」这类说法认得出来。</summary>
public class PromptBudgetTests
{
    /// <summary>仿技术协议展开后的槽位：封面 10 项 + 4 台设备 × 47 项 + 部件行若干。</summary>
    private static List<TemplateSlot> Slots()
    {
        var list = new List<TemplateSlot>();
        var order = 0;
        TemplateSlot S(string tag, string name, string section) => new()
        {
            Tag = tag, Name = name, Section = section, DataType = SlotDataType.Text, Required = true, SortOrder = order++,
            Choices = null
        };
        for (var i = 1; i <= 10; i++) list.Add(S($"cover.{i:00}", $"封面项{i}", "封面"));
        var devices = new[] { ("t3_1", "a1", "R-101 聚合反应釜", "表3-1"), ("t3_1", "a2", "R-102 预聚反应釜", "表3-2"),
            ("t3_2", "a3", "V-201 收集罐", "表3-3"), ("t3_3", "a4", "E-101 冷凝器", "表3-4") };
        foreach (var (block, id, who, no) in devices)
            for (var k = 1; k <= 47; k++)
                list.Add(S($"{block}#{id}.{k:00}", k == 7 ? "内筒·设计压力（MPa）" : $"参数{k}", $"{no} 设备 · {who}"));
        for (var p = 1; p <= 20; p++) list.Add(S($"part15#p{p}.01", "测量范围", $"主要部件清单 · PG-{p:000} 压力表"));
        return list;
    }

    [Fact]
    public void 字段少的模板全给()
    {
        var few = Slots().Take(40).ToList();
        Assert.Equal(40, GenerationChatService.PromptSlots(few, [], [], "随便说一句").Count);
    }

    [Fact]
    public void 字段多时按轻重挑_不超上限_正在问的那章和点名的设备在里面()
    {
        var all = Slots();
        var asked = new[] { "t3_1#a1.05", "t3_1#a1.06" };
        var picked = GenerationChatService.PromptSlots(all, asked, [], "R-102 的设计压力是 5MPa");
        Assert.True(picked.Count <= GenerationChatService.PromptSlotLimit);
        Assert.Contains(picked, s => s.Tag == "t3_1#a1.05");                         // 正在问的
        Assert.Contains(picked, s => s.Tag == "t3_1#a1.47");                         // 它那一整章
        Assert.Contains(picked, s => s.Tag == "t3_1#a2.07");                         // 点了名的 R-102 的设计压力
        Assert.DoesNotContain(picked, s => s.Section.Contains("V-201"));             // 没提到的设备不给
        // 原顺序
        Assert.Equal(picked.OrderBy(s => s.SortOrder).Select(s => s.Tag), picked.Select(s => s.Tag));
    }

    [Fact]
    public void 槽位表按章节分组_体积受控_写明还有没列出的()
    {
        var all = Slots();
        var picked = GenerationChatService.PromptSlots(all, ["t3_1#a1.05"], [], "R-102 设计压力 5MPa");
        var sb = new StringBuilder();
        GenerationChatService.AppendSlotTable(sb, "槽位清单", all, picked, new Dictionary<string, SlotState>(), withType: true);
        var text = sb.ToString();
        Assert.Equal(1, CountOf(text, "【表3-1 设备 · R-101 聚合反应釜】"));   // 章节名只写一次
        Assert.Contains($"另有 {all.Count - picked.Count} 项本轮没列出", text);
        Assert.True(text.Length < 4000, $"槽位表 {text.Length} 字，超出预算");
    }

    private static int CountOf(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Theory]
    [InlineData("仍然生成", true)]
    [InlineData("直接生成文档", true)]
    [InlineData("先出一版", true)]
    [InlineData("带空项生成", true)]
    [InlineData("生成文档", false)]
    [InlineData("仍然生成不了怎么办，必填项太多了", false)]
    public void 仍然生成的说法(string text, bool force)
    {
        Assert.Equal(force, GenChatLogic.IsForceRender(text));
        if (force) Assert.Equal(GenChatLogic.ChatCommand.Render, GenChatLogic.DetectCommand(text));
    }
}
