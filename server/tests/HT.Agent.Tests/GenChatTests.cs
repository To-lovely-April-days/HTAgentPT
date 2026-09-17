using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

/// <summary>对话式填槽的纯逻辑（GenChatLogic）：指令、日期、选项、模型输出解析、模板推荐打分。</summary>
public class GenChatTests
{
    [Theory]
    [InlineData("跳过", GenChatLogic.ChatCommand.Skip)]
    [InlineData("先跳过。", GenChatLogic.ChatCommand.Skip)]
    [InlineData("下一个", GenChatLogic.ChatCommand.Skip)]
    [InlineData("生成文档", GenChatLogic.ChatCommand.Render)]
    [InlineData("生成", GenChatLogic.ChatCommand.Render)]
    [InlineData("可以生成了", GenChatLogic.ChatCommand.Render)]
    [InlineData("汇总", GenChatLogic.ChatCommand.Summary)]
    [InlineData("材质是316L", GenChatLogic.ChatCommand.None)]
    // 长句里带「生成」不算指令——那是内容
    [InlineData("加热形式选电加热就能生成合格品", GenChatLogic.ChatCommand.None)]
    public void 指令识别_只认独立短句(string text, GenChatLogic.ChatCommand expected)
        => Assert.Equal(expected, GenChatLogic.DetectCommand(text));

    [Theory]
    [InlineData("2026-9-3", "2026-09-03")]
    [InlineData("2026/09/03", "2026-09-03")]
    [InlineData("2026年9月3日", "2026-09-03")]
    [InlineData("9月3日", "2025-09-03")]
    [InlineData("2026-13-01", null)]
    [InlineData("下周三", null)]
    public void 日期规整(string raw, string? expected)
        => Assert.Equal(expected, GenChatLogic.NormalizeDate(raw, 2025));

    [Fact]
    public void 选项匹配_精确_包含_同义_歧义()
    {
        string[] heat = ["电加热", "油浴控夹套", "油浴控物料"];
        Assert.Equal("电加热", GenChatLogic.MatchChoice("电加热", heat));
        Assert.Equal("电加热", GenChatLogic.MatchChoice("用电加热的", heat));
        Assert.Null(GenChatLogic.MatchChoice("油浴", heat));           // 两个油浴选项都沾边 → 不猜
        Assert.Null(GenChatLogic.MatchChoice("蒸汽加热", heat));
        string[] yn = ["有", "无"];
        Assert.Equal("无", GenChatLogic.MatchChoice("不需要", yn));
        Assert.Equal("有", GenChatLogic.MatchChoice("需要", yn));
        string[] volt = ["220V", "380V"];
        Assert.Equal("380V", GenChatLogic.MatchChoice("380v", volt));
    }

    [Fact]
    public void 模型输出解析_容忍围栏与废话_解析失败给空表()
    {
        var fills = GenChatLogic.ParseModelFills(
            "好的，以下是抽取结果：\n```json\n{\"fills\":[{\"tag\":\"material\",\"value\":\"316L\"},{\"tag\":\"device_qty\",\"value\":2}]}\n```");
        Assert.Equal(2, fills.Count);
        Assert.Equal(("material", "316L"), fills[0]);
        Assert.Equal(("device_qty", "2"), fills[1]);

        Assert.Empty(GenChatLogic.ParseModelFills("完全不是 JSON 的回答"));
        Assert.Empty(GenChatLogic.ParseModelFills("{\"fills\":\"不是数组\"}"));
        Assert.Empty(GenChatLogic.ParseModelFills("{\"fills\":[{\"tag\":\"a\"}]}")); // 缺 value 丢弃
    }

    [Theory]
    [InlineData("有没有历史做过的你查询一下", "ledger")]
    [InlineData("华东理工之前做过哪些项目", "ledger")]
    [InlineData("查一下台账", "ledger")]
    [InlineData("参考类似项目推荐一下技术参数", "ledger")]   // 「参考…项目」先归台账，再由用户挑
    [InlineData("这几项给个建议", "suggest")]
    [InlineData("材质一般用什么", "suggest")]
    [InlineData("都采纳", "adopt")]
    [InlineData("用第二个做基准", "pick_base")]
    [InlineData("P-2025-0186", "pick_base")]
    [InlineData("设计压力和使用压力有什么区别？", "ask")]
    [InlineData("防爆工况要不要调设计压力", "ask")]
    [InlineData("316L", "fill")]
    [InlineData("生成文档", "render")]
    public void 规则规划_按意图派工(string text, string expectedType)
    {
        var plan = GenChatLogic.PlanByRules(text);
        Assert.Single(plan);
        Assert.Equal(expectedType, plan[0].Type);
    }

    [Fact]
    public void 规则规划_序号与名称能带出参数()
    {
        Assert.Equal(2, GenChatLogic.PlanByRules("用第二个做基准")[0].Index);
        Assert.Equal("P-2025-0186", GenChatLogic.PlanByRules("就用 P-2025-0186 做基准")[0].ProjectNo);
        Assert.Equal("材质", GenChatLogic.PlanByRules("采纳材质")[0].Name);
        Assert.Empty(GenChatLogic.PlanByRules("全部采纳")[0].Tags!);
    }

    [Fact]
    public void 派工单解析_多动作_未知类型丢弃_兼容纯fills格式()
    {
        var plan = GenChatLogic.ParsePlan(
            "```json\n{\"actions\":[{\"type\":\"fill\",\"tag\":\"customer_name\",\"value\":\"华东理工\"}," +
            "{\"type\":\"ledger\",\"customer\":\"华东理工\",\"device\":\"\"}," +
            "{\"type\":\"pick_base\",\"index\":2},{\"type\":\"dance\"}," +
            "{\"type\":\"fill\",\"tag\":\"x\"}]}\n```");
        Assert.Equal(3, plan.Count);                       // dance 丢弃、缺 value 的 fill 丢弃
        Assert.Equal("华东理工", plan[0].Value);
        Assert.Equal("华东理工", plan[1].Customer);
        Assert.Null(plan[1].Device);                      // 空串归 null
        Assert.Equal(2, plan[2].Index);

        var legacy = GenChatLogic.ParsePlan("{\"fills\":[{\"tag\":\"material\",\"value\":\"316L\"}]}");
        Assert.Single(legacy);
        Assert.Equal(("fill", "material"), (legacy[0].Type, legacy[0].Tag));
        Assert.Empty(GenChatLogic.ParsePlan("不是 JSON"));
    }

    [Fact]
    public void 模板推荐打分_名称命中优先_类别兜底()
    {
        var q = "帮我出一份C类生产任务单，客户是华东理工";
        var hit = GenChatLogic.TemplateScore(q, "C类生产任务单（非标）", "方案");
        var miss = GenChatLogic.TemplateScore(q, "售后维修报告", "报告");
        Assert.True(hit > miss);
        Assert.True(GenChatLogic.TemplateScore("起草一个方案", "投标书模板", "方案") > 0); // 类别命中
        Assert.Equal(0, GenChatLogic.TemplateScore("水泵怎么保养", "投标书模板", "投标"));
    }

    [Theory]
    [InlineData("我要做硝化反应呢")]
    [InlineData("介质有强腐蚀性")]
    [InlineData("这个要过夜连续运行")]
    [InlineData("用来做加氢的")]
    [InlineData("物料是淤浆，容易结垢")]
    public void 工况说明_派给工况顾问(string text)
    {
        var plan = GenChatLogic.PlanByRules(text);
        Assert.Equal("advise", plan[0].Type);
        Assert.Equal(text, plan[0].Question);
    }

    [Fact]
    public void 工况顾问输出解析_缺项丢弃_失败给空()
    {
        var (notes, advices) = GenChatLogic.ParseAdvice(
            "好的：\n```json\n{\"notes\":\"硝化强放热且强腐蚀\",\"advices\":[" +
            "{\"tag\":\"material\",\"value\":\"哈氏合金 C276\",\"level\":\"high\",\"reason\":\"硝酸体系对 316L 腐蚀严重\",\"risk\":\"点蚀穿孔\"}," +
            "{\"tag\":\"inner_cooling\",\"value\":\"盘管\",\"reason\":\"需快速移热\"}," +
            "{\"tag\":\"ctrl\",\"value\":\"触摸屏\",\"level\":\"极高\"}," +
            "{\"value\":\"缺 tag 丢弃\"},{\"tag\":\"x\"}]}\n```");
        Assert.Equal("硝化强放热且强腐蚀", notes);
        Assert.Equal(3, advices.Count);
        Assert.Equal(("material", "哈氏合金 C276", "点蚀穿孔"), (advices[0].Tag, advices[0].Value, advices[0].Risk));
        Assert.Equal("high", advices[0].Level);
        Assert.Null(advices[1].Risk);                       // 没给风险就是 null，不编
        Assert.Equal("normal", advices[1].Level);           // 没给要紧程度按普通
        Assert.Equal("normal", advices[2].Level);           // 模型自创的等级不认
        Assert.Empty(GenChatLogic.ParseAdvice("不是 JSON").Advices);
        Assert.Empty(GenChatLogic.ParseAdvice("{\"advices\":\"不是数组\"}").Advices);
    }

    [Theory]
    [InlineData("材质换其他的", "材质")]
    [InlineData("材质不合适", "材质")]
    [InlineData("加热形式换一个", "加热形式")]
    [InlineData("换个别的", null)]
    [InlineData("这个不行", null)]
    [InlineData("再给一个", null)]
    public void 对建议不买账_认出是哪一项(string text, string? name)
    {
        var (revise, got) = GenChatLogic.ParseRevise(text);
        Assert.True(revise);
        Assert.Equal(name, got);
        var plan = GenChatLogic.PlanByRules(text);
        Assert.Equal("advise", plan[0].Type);
        Assert.Equal(name, plan[0].Name);
    }

    [Theory]
    [InlineData("材质换成316L")]      // 给了具体取值，是填值不是要新方案
    [InlineData("改成电加热")]
    [InlineData("转速范围改到 30~300")]
    public void 换成具体取值仍算填值(string text)
    {
        Assert.False(GenChatLogic.ParseRevise(text).Revise);
        Assert.Equal("fill", GenChatLogic.PlanByRules(text)[0].Type);
    }

    [Fact]
    public void 模型一轮_正文在前动作块在后()
    {
        var t = GenChatLogic.ParseTurn(
            "能换。\n\n现在的 1.6MPa 是按使用压力 1.0 取 1.5 倍裕度来的——\n" +
            "「飞温」工况下裕度小了泄压来不及。\n\n- 压到 0.8 以下：可以降到 1.2\n- 保持 1.0：不建议低于 1.6\n\n" +
            "```json\n{\"actions\":[{\"type\":\"advise\",\"question\":\"设计压力能换吗\",\"name\":\"设计压力\"}]}\n```");
        Assert.NotNull(t.Reply);
        Assert.Contains("1.5 倍", t.Reply!);
        Assert.Contains("- 保持 1.0", t.Reply!);          // 正文里的换行与要点原样留着
        Assert.DoesNotContain("actions", t.Reply!);       // 代码块不混进正文
        Assert.Single(t.Actions);
        Assert.Equal(("advise", "设计压力"), (t.Actions[0].Type, t.Actions[0].Name));
    }

    [Fact]
    public void 纯答疑_整段都是正文()
    {
        var t = GenChatLogic.ParseTurn("这项按 GB150 取，通常留 1.5 倍裕度。\n换低了爆破片动作会不可靠。");
        Assert.NotNull(t.Reply);
        Assert.Contains("GB150", t.Reply!);
        Assert.Empty(t.Actions);
    }

    [Fact]
    public void 回答里有引号和换行也不影响解析()
    {
        // 这正是把回答塞进 JSON 字符串时会崩的情形
        var t = GenChatLogic.ParseTurn(
            "按「最坏工况」算：\n温度 200℃ 时压力会到 1.4MPa，\n所以 1.6 是下限，不是「保守」。\n" +
            "```json\n{\"actions\":[]}\n```");
        Assert.NotNull(t.Reply);
        Assert.Contains("最坏工况", t.Reply!);
        Assert.Empty(t.Actions);
    }

    [Fact]
    public void 兼容老格式_整体JSON时取reply字段()
    {
        var t = GenChatLogic.ParseTurn("{\"reply\":\"能换，看你使用压力定多少。\",\"actions\":[{\"type\":\"summary\"}]}");
        Assert.Equal("能换，看你使用压力定多少。", t.Reply);
        Assert.Single(t.Actions);
    }

    [Theory]
    [InlineData("给我一份英文版", "zh2en")]
    [InlineData("把这份翻译成英文", "zh2en")]
    [InlineData("能出个 English version 吗", "zh2en")]
    [InlineData("英文的文档也要一份", "zh2en")]
    [InlineData("翻译成中文", "en2zh")]
    public void 要整篇译文_派翻译不当成取值(string text, string direction)
    {
        var plan = GenChatLogic.PlanByRules(text);
        var a = Assert.Single(plan);
        Assert.Equal("translate", a.Type);
        Assert.Equal(direction, a.Value);
    }

    [Theory]
    [InlineData("材质是316L")]
    [InlineData("设计压力1.6MPa")]
    public void 普通取值不会被当成要译文(string text)
        => Assert.DoesNotContain(GenChatLogic.PlanByRules(text), a => a.Type == "translate");

    [Fact]
    public void 模型派翻译单_方向按value解析()
    {
        var t = GenChatLogic.ParseTurn(
            "这就出英文版。\n```json\n{\"actions\":[{\"type\":\"translate\",\"value\":\"zh2en\"}]}\n```");
        var a = Assert.Single(t.Actions);
        Assert.Equal("translate", a.Type);
        Assert.Equal("zh2en", a.Value);
    }

    [Fact]
    public void 模型没写tag的填值不丢弃_留给服务端认是哪一项()
    {
        // 丢了就等于用户白给了这个值，界面还会把同一项再问一遍——「我给了合同编号，它还在问」
        var t = GenChatLogic.ParseTurn(
            "合同编号 HT-2025-C0012 已落。\n```json\n{\"actions\":[{\"type\":\"fill\",\"value\":\"HT-2025-C0012\"}]}\n```");
        var a = Assert.Single(t.Actions);
        Assert.Equal("fill", a.Type);
        Assert.Null(a.Tag);
        Assert.Equal("HT-2025-C0012", a.Value);
    }

    [Fact]
    public void 模型把项名写进tag_原样带出交服务端解析()
    {
        var plan = GenChatLogic.ParsePlan("{\"actions\":[{\"type\":\"fill\",\"tag\":\"合同编号\",\"value\":\"HT-2025-C0012\"}]}");
        var a = Assert.Single(plan);
        Assert.Equal("合同编号", a.Tag);
    }

    [Fact]
    public void 没有值的填值单照旧丢弃()
        => Assert.Empty(GenChatLogic.ParsePlan("{\"actions\":[{\"type\":\"fill\",\"tag\":\"contract_no\"}]}"));

    [Fact]
    public void 只有动作没有话_动作照办话由专员补()
    {
        var onlyActions = GenChatLogic.ParseTurn("{\"actions\":[{\"type\":\"summary\"}]}");
        Assert.Null(onlyActions.Reply);
        Assert.Single(onlyActions.Actions);

        // 什么都没给才是真的没接住
        Assert.Null(GenChatLogic.ParseTurn("   ").Reply);
        Assert.Empty(GenChatLogic.ParseTurn("   ").Actions);
    }
}
