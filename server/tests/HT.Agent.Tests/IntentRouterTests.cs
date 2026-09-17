using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class IntentRouterTests
{
    private static readonly string[] Customers = ["华东理工", "南方药业"];
    private static readonly string[] Devices = ["反应釜", "旋转蒸发仪"];

    [Theory]
    [InlineData("把这段翻译成英文", IntentRouter.Translate)]
    [InlineData("帮我写一份 10L 反应釜的技术方案", IntentRouter.Generate)]
    [InlineData("给南方药业出一份报价", IntentRouter.Generate)]
    [InlineData("2023 年给华东理工做过哪些项目", IntentRouter.Ledger)]
    [InlineData("P-2025-0186 的交付状态", IntentRouter.Ledger)]
    [InlineData("CJF-5L 的最大工作压力是多少", IntentRouter.Knowledge)]
    [InlineData("搅拌电机过载怎么排查", IntentRouter.Knowledge)]
    public void 意图分类(string q, string expected)
        => Assert.Equal(expected, IntentRouter.Classify(q, Customers, Devices));

    [Fact]
    public void 拿不准时保守走知识问答()
    {
        // 只提客户名但不是盘点式问题 → 不抢分流（判定可见可纠正，宁可保守）
        Assert.Equal(IntentRouter.Knowledge,
            IntentRouter.Classify("华东理工的釜盖密封结构是什么", Customers, Devices));
    }

    [Fact]
    public void 台账筛选抽取_客户年份设备()
    {
        var f = IntentRouter.ExtractFilters("2022 到 2024 年给华东理工做的反应釜项目", Customers, Devices);
        Assert.Equal("华东理工", f.CustomerName);
        Assert.Equal("反应釜", f.DeviceType);
        Assert.Equal(2022, f.YearFrom);
        Assert.Equal(2024, f.YearTo);
    }

    [Fact]
    public void 台账筛选抽取_近三年()
    {
        var f = IntentRouter.ExtractFilters("近三年南方药业的项目", Customers, Devices);
        Assert.Equal("南方药业", f.CustomerName);
        Assert.Equal(3, f.RecentYears);
        Assert.Null(f.YearFrom);
    }
}

public class ChunkSplitterTests
{
    [Fact]
    public void 单点拆分为两块()
    {
        var parts = ChunkSplitter.Split("甲甲甲甲甲乙乙乙乙乙", [5]);
        Assert.Equal(["甲甲甲甲甲", "乙乙乙乙乙"], parts);
    }

    [Fact]
    public void 多点拆分且自动排序去重()
    {
        var parts = ChunkSplitter.Split("112233", [4, 2, 2]);
        Assert.Equal(["11", "22", "33"], parts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void 越界拆分点拒绝(int off)
        => Assert.Throws<ArgumentException>(() => ChunkSplitter.Split("112233", [off]));

    [Fact]
    public void 拆出空块拒绝()
        => Assert.Throws<ArgumentException>(() => ChunkSplitter.Split("11 22", [2, 3]));

    [Theory]
    [InlineData("CJF-5L 显示 E12 报警怎么处理")]
    [InlineData("磁力搅拌不转了")]
    [InlineData("之前有没有类似的维修记录")]
    [InlineData("釜盖漏液，查一下案例")]
    public void 故障与报警归案例(string q)
        => Assert.Equal(IntentRouter.Case, IntentRouter.Classify(q, [], []));

    [Fact]
    public void 参数类提问不归案例()
    {
        Assert.Equal(IntentRouter.Knowledge, IntentRouter.Classify("CJF-5L 的复装力矩是多少", [], []));
        Assert.Equal(IntentRouter.Knowledge, IntentRouter.Classify("设计压力怎么取", [], []));
    }

    [Theory]
    [InlineData("请给我英文的文档", "zh2en")]
    [InlineData("把这段翻译成英文", "zh2en")]
    [InlineData("translate to Chinese", "en2zh")]
    [InlineData("译成中文", "en2zh")]
    public void 翻译方向识别(string q, string expected)
        => Assert.Equal(expected, IntentRouter.ParseTranslateAsk(q).Direction);

    [Fact]
    public void 翻译请求_纯指令不带正文时不拿指令去翻()
    {
        Assert.Null(IntentRouter.ParseTranslateAsk("请给我英文的文档").Text);
        Assert.Null(IntentRouter.ParseTranslateAsk("翻译成英文").Text);
        Assert.True(IntentRouter.ParseTranslateAsk("请给我英文的文档").WantsFile);

        var withBody = IntentRouter.ParseTranslateAsk("把这段翻译成英文：本设备采用磁力耦合密封，最高工作压力 10MPa");
        Assert.Equal("zh2en", withBody.Direction);
        Assert.NotNull(withBody.Text);
        Assert.Contains("磁力耦合密封", withBody.Text!);
        Assert.DoesNotContain("翻译", withBody.Text!);
    }

    [Fact]
    public void 案例关键词_去掉问法留下型号与代码()
    {
        var k = IntentRouter.CaseKeywords("有没有 CJF-5L 显示 E12 报警的案例，怎么处理？");
        Assert.Contains("CJF-5L", k);
        Assert.Contains("E12", k);
        Assert.DoesNotContain("有没有", k);
        Assert.DoesNotContain("怎么处理", k);
    }

    // 实际词表（customer_name / device_type）下的判定
    private static readonly string[] Customers = ["华东理工", "南方药业"];
    private static readonly string[] Devices =
        ["光化学反应仪", "冻干机", "反应釜", "平行合成仪", "旋转蒸发仪", "真空干燥箱", "离心机"];

    [Theory]
    [InlineData("华东理工近三年做过哪些反应釜")]
    [InlineData("华东理工大学近三年做过哪些反应釜")]   // 用户说全称，词表里是简称
    [InlineData("近三年做过哪些反应釜")]                // 没提客户，只说设备
    [InlineData("华东理工都买过什么设备")]
    public void 盘点类提问归台账(string q)
        => Assert.Equal(IntentRouter.Ledger, IntentRouter.Classify(q, Customers, Devices));

    [Theory]
    [InlineData("待处理的工单有哪些", IntentRouter.Ticket)]
    [InlineData("HT-2025-0031 这单报修到哪了", IntentRouter.Ticket)]
    [InlineData("我的工单", IntentRouter.Ticket)]
    [InlineData("报修进度怎么样了", IntentRouter.Ticket)]
    // 「这毛病怎么修」仍归案例，不被工单抢走
    [InlineData("磁力搅拌不转了怎么处理", IntentRouter.Case)]
    [InlineData("E12 报警的维修记录", IntentRouter.Case)]
    public void 工单与案例的分界(string q, string expected)
        => Assert.Equal(expected, IntentRouter.Classify(q, Customers, Devices));

    [Fact]
    public void 客户简称全称互认()
    {
        Assert.True(IntentRouter.Mentions("华东理工做过哪些釜", "华东理工大学"));
        Assert.True(IntentRouter.Mentions("华东理工大学做过哪些釜", "华东理工"));
        Assert.False(IntentRouter.Mentions("南方药业做过哪些釜", "华东理工大学"));
        // 主干太短不做简称匹配，免得「厂」「公司」这类残渣乱命中
        Assert.False(IntentRouter.Mentions("我们厂的设备", "厂"));
    }
}
