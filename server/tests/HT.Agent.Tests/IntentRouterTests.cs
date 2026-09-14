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
}
