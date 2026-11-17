using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

/// <summary>历史技术协议参数正文的按设备取值。文本形态照历史协议解析后的样子：
/// 设备标题行 + 「标签：值」分句，以及部件清单表格行的规格参数格。</summary>
public class SpecTextExtractorTests
{
    private const string Params = """
        【第三章 工艺流程 > 一、主要设备参数】
        （1）R-3101 预聚釜
        容积：0.5L；
        接液材质：316L，夹套材质：304；
        工作温度：10℃，工作压力：3.5MPaG；
        设计温度：100℃，设计压力：5.0MPaG；
        搅拌：双端面机械，变频，电机功率：1.1KW，调速范围0-500rpm；
        防爆等级：ExdIICT4。
        （2）V-4101 PP闪蒸罐
        容积：200L；
        工作温度：65℃，工作压力：1.5MPaG；
        预聚冷凝器 E-101
        管程材质/壳程材质：316L/304
        工作压力 管程/壳程：-0.1~3/0.3MPaG
        换热面积：0.9㎡
        """;

    private const string PartsRows = """
        | 序号 | 位号 | 名称 | 规格参数 | 数量 | 单位 | 品牌 |
        |  | PT | 压力变送器带现场显示 | 压力:0~10MPaG，材质316L，防爆等级：ExdIIBT4Gb | 2 | 件 | 安徽天康或同等 |
        |  | PG-101 | 压力表 | 测量范围：0~4MPaG；精确度等级：1.6；表盘直径：100mm | 1 | 件 | 安徽天康 |
        """;

    private static string? Get(string text, string name, string section, string? unit = null)
        => SpecTextExtractor.Extract(text, SpecTextExtractor.FromSlot(name, unit, section)!)?.Value;

    [Fact]
    public void 槽位名与章节拆成标签_分组_设备线索()
    {
        var t = SpecTextExtractor.FromSlot("夹套/盘管·材质", null, "表3-1 带搅拌反应釜 · R-101 聚合反应釜")!;
        Assert.Equal(("材质", "夹套/盘管", "R-101"), (t.Label, t.Group, t.TagNo));
        Assert.Contains("聚合反应釜", t.EquipmentWords);

        var main = SpecTextExtractor.FromSlot("内筒·工作压力（MPa）", null, null)!;
        Assert.Null(main.Group);              // 内筒是主体
        Assert.Equal("MPa", main.Unit);

        var alt = SpecTextExtractor.FromSlot("内筒全容积·容积及尺寸", null, null)!;
        Assert.Null(alt.Group);               // 「内筒全容积」不是部位，是另一个标签
        Assert.Equal("内筒全容积", alt.AltLabel);
    }

    [Fact]
    public void 按设备取值_位号对上的那台优先()
    {
        Assert.Equal("3.5MPaG", Get(Params, "内筒·工作压力（MPa）", "表3-1 带搅拌反应釜 · R-3101 预聚釜"));
        Assert.Equal("1.5MPaG", Get(Params, "内筒·工作压力（MPa）", "表3-2 储罐 · V-4101 闪蒸罐"));
        Assert.Equal("200L", Get(Params, "内筒全容积·容积及尺寸", "表3-2 储罐 · V-4101 闪蒸罐"));
    }

    [Fact]
    public void 同义标签与分组_接液材质算材质_夹套只认夹套()
    {
        const string section = "表3-1 带搅拌反应釜 · R-3101 预聚釜";
        Assert.Equal("316L", Get(Params, "内筒·材质", section));
        Assert.Equal("304", Get(Params, "夹套/盘管·材质", section));
        Assert.Equal("1.1KW", Get(Params, "电机功率", section));
        Assert.Equal("1.1", Get(Params, "电机功率", section, "KW"));   // 模板自带单位的去掉单位
        Assert.Equal("ExdIICT4", Get(Params, "防爆等级", section));
    }

    [Fact]
    public void 管程壳程成对写法拆开()
    {
        const string section = "表3-4 换热器 · E-101 冷凝器";
        Assert.Equal("304", Get(Params, "壳程·材质", section));
        Assert.Equal("316L", Get(Params, "管程·材质", section));
        Assert.Equal("0.3MPaG", Get(Params, "壳程·工作压力（MPa）", section));
        Assert.Equal("0.9㎡", Get(Params, "管程换热面积", section));
    }

    [Fact]
    public void 清单行_规格参数格按设备取_别的类别不拿()
    {
        Assert.Equal("0~4", Get(PartsRows, "测量范围", "主要部件清单 · PG-101 压力表", "MPaG"));
        // 只有变送器行写了「压力:」，位号前缀 PT≠PG，不能当压力表的量程
        Assert.Null(Get(PartsRows.Replace("测量范围：0~4MPaG；", ""), "测量范围", "主要部件清单 · PG-101 压力表"));
    }

    [Fact]
    public void 值不拖带下一个标签_没定的占位不算值()
    {
        Assert.Equal("316L", Get("材质：316L 使用温度：180℃", "材质", "表3-1 反应釜 · R-1 釜"));
        Assert.Null(Get("（1）R-1 釜\n电机功率：--KW", "电机功率", "表3-1 反应釜 · R-1 釜"));
    }
}
