using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

/// <summary>按字段名取值：继承预填与参数建议都靠它把历史资料用起来。
/// 夹具用的是真实任务单解析后的形态（Word 表格行「| 标签 | 值 | 标签 | 值 |」）。</summary>
public class FormFieldExtractorTests
{
    private const string TaskOrder = """
        | 非标产品生产任务单 |
        | 合同编号 | HT-2023-C091 | 下单日期 | 2023-04-11 | 售前人员 | 张伟 |
        | 客户名称 | 华东理工大学化学工程学院 | 项目紧急程度 | 一般 |
        | 全 容 积 | 5000ml | 材 质 | 釜体及釜盖 316L，搅拌轴 316L |
        | 结构形式 | 法兰 | 升 降 | 无 |
        | 使用压力 | 10MPa | 爆破压力 | 15MPa | 设计压力 | 12MPa |
        """;

    [Theory]
    [InlineData("合同编号", "HT-2023-C091")]
    [InlineData("下单日期", "2023-04-11")]
    [InlineData("售前人员", "张伟")]                      // 行尾字段也要取到
    [InlineData("客户名称", "华东理工大学化学工程学院")]
    [InlineData("结构形式", "法兰")]
    [InlineData("升降", "无")]                            // 标签里有空格（「升 降」）
    [InlineData("爆破压力", "15MPa")]                     // 同行第二个字段，不能串到第一个
    public void 按字段名取值_不是整块也不是首行(string field, string expected)
        => Assert.Equal(expected, FormFieldExtractor.Extract(TaskOrder, field));

    [Fact]
    public void 模板自带单位从取值里去掉()
    {
        // 模板里「全容积 ____ ml」的单位是版面固定文字，回填只填数值，否则会出现「5000ml ml」
        Assert.Equal("5000", FormFieldExtractor.Extract(TaskOrder, "全容积", "ml"));
        Assert.Equal("10", FormFieldExtractor.Extract(TaskOrder, "使用压力", "MPa"));
        Assert.Equal("10MPa", FormFieldExtractor.Extract(TaskOrder, "使用压力"));   // 不给单位就原样
    }

    [Fact]
    public void 取不到就返回空_绝不硬凑()
    {
        Assert.Null(FormFieldExtractor.Extract(TaskOrder, "防爆等级要求"));
        Assert.Null(FormFieldExtractor.Extract(TaskOrder, "交货日期"));      // 文中没有这一项
        Assert.Null(FormFieldExtractor.Extract("", "合同编号"));
        Assert.Null(FormFieldExtractor.Extract(TaskOrder, ""));
        // 标签在但取值空：不能把下一个标签当值
        Assert.Null(FormFieldExtractor.Extract("| 交货方式 |  |", "交货方式"));
    }

    [Fact]
    public void 冒号式写法也认()
    {
        const string text = "设备型号：CJF-20L\n防爆等级要求：ExdⅡCT4\n随机资料：见附件";
        Assert.Equal("CJF-20L", FormFieldExtractor.Extract(text, "设备型号"));
        Assert.Equal("ExdⅡCT4", FormFieldExtractor.Extract(text, "防爆等级要求"));
    }

    [Fact]
    public void 精确同名优先于包含匹配()
    {
        const string text = "| 设计压力 | 12MPa |\n| 压力 | 不该取这个 |";
        Assert.Equal("12MPa", FormFieldExtractor.Extract(text, "设计压力"));
        // 名字差太远的不算命中（长度差一倍以上）
        Assert.Null(FormFieldExtractor.Extract("| 压力 | 10MPa |", "釜盖开口及接口尺寸要求"));
    }

    [Fact]
    public void 多份资料里按顺序取第一处()
    {
        var texts = new[] { "| 材质 | |", "| 材质 | 316L |", "| 材质 | 哈氏合金 |" };
        Assert.Equal("316L", FormFieldExtractor.ExtractFirst(texts, "材质"));
        Assert.Null(FormFieldExtractor.ExtractFirst(texts, "桨型"));
    }

    /// <summary>任务单里的两张明细表：表头行的邻格是列名不是取值，得按列对齐到数据行去取；
    /// 两张表列名相同（名称/数量），靠章节认出该取哪一张。</summary>
    private const string TablesInOrder = """
        | 设备信息（销售填） |
        | 序号 | 名称 | 数量 | 单价（元） | 型号 |
        | 1 | 磁力搅拌高压反应釜 | 1 | 58000 | CJF-5L |
        | 配件及耗材清单（未装在主设备上的均需填写） |
        | 序号 | 名称 | 数量 | 规格 |
        | 1 | 聚四氟乙烯内衬杯 | 2 | φ90×110 mm |
        """;

    private static readonly string[] Labels =
        ["设备名称", "数量", "单价", "设备型号", "配件名称", "配件数量", "配件规格", "合同编号", "客户名称"];

    [Theory]
    [InlineData("设备名称", "设备信息", "磁力搅拌高压反应釜")]
    [InlineData("数量", "设备信息", "1")]
    [InlineData("单价", "设备信息", "58000")]
    [InlineData("设备型号", "设备信息", "CJF-5L")]
    [InlineData("配件名称", "配件信息", "聚四氟乙烯内衬杯")]
    [InlineData("配件规格", "配件信息", "φ90×110 mm")]
    public void 表头行按列对齐取数据行_同名列靠章节区分(string field, string section, string expected)
        => Assert.Equal(expected, FormFieldExtractor.Extract(TablesInOrder, field, null, Labels, section));

    [Fact]
    public void 右邻是另一个字段名时不当取值()
    {
        // 「| 序号 | 名称 | 数量 | 单价（元） |」：取「数量」不能得到「单价（元）」
        const string header = "| 序号 | 名称 | 数量 | 单价（元） | 型号 |";
        Assert.Null(FormFieldExtractor.Extract(header, "数量", null, Labels, "设备信息"));
        Assert.Null(FormFieldExtractor.Extract(header, "设备名称", null, Labels, "设备信息"));
    }
}
