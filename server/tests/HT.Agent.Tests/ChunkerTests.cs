using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Dtos;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;

namespace HT.Agent.Tests;

public class ChunkerTests
{
    private static readonly ChunkingOptions Opt = new(TargetLength: 200, Overlap: 20, MinLength: 40);

    [Fact]
    public void 按章节_同一小节不拆分且带标题前缀()
    {
        var blocks = new List<ParsedBlock>
        {
            new("heading", "操作手册", 1, 1, null, null),
            new("heading", "安全须知", 2, 1, null, null),
            new("paragraph", "操作前须泄压。", null, 1, null, null),
            new("paragraph", "严禁超温运行。", null, 1, null, null),
            new("heading", "维护保养", 2, 2, null, null),
            new("paragraph", "每月检查密封件。", null, 2, null, null)
        };
        var chunks = Chunker.Chunk(ChunkStrategy.ByHeading, blocks, Opt);
        Assert.Equal(2, chunks.Count);
        Assert.StartsWith("【操作手册 > 安全须知】", chunks[0].Text);
        Assert.Contains("操作前须泄压。", chunks[0].Text);
        Assert.Contains("严禁超温运行。", chunks[0].Text);
        Assert.Equal("操作手册 > 维护保养", chunks[1].SectionPath);
        Assert.Equal(2, chunks[1].PageNo);
    }

    [Fact]
    public void 按章节_表格行不脱离表头()
    {
        // 表 4-2 的意图：脱离表头的行无法理解。曾在冒烟中暴露为真实缺陷，此测试为回归锁
        var blocks = new List<ParsedBlock>
        {
            new("heading", "技术参数", 1, 1, null, null),
            new("table_row", "| CJF-5L | 5 L | 10 MPa |", null, 1, null, "| 型号 | 容积 | 最大工作压力 |"),
            new("table_row", "| CJF-10L | 10 L | 8 MPa |", null, 1, null, "| 型号 | 容积 | 最大工作压力 |")
        };
        var chunks = Chunker.Chunk(ChunkStrategy.ByHeading, blocks, Opt);
        var text = Assert.Single(chunks).Text;
        Assert.Contains("最大工作压力", text);
        // 表头只注入一次
        Assert.Equal(1, CountOf(text, "| 型号 |"));
    }

    [Fact]
    public void 按条款_每条独立成块()
    {
        var blocks = new List<ParsedBlock>
        {
            new("paragraph", "第一条 设备总价为人民币肆拾捌万元。\n第二条 交货期为合同生效后 60 日。\n第三条 质保期 18 个月。", null, 1, null, null)
        };
        var chunks = Chunker.Chunk(ChunkStrategy.ByClause, blocks, Opt);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Matches("^第[一二三]条", c.Text));
    }

    [Fact]
    public void 按行_表头作为每块前缀()
    {
        var header = "| 名称 | 数量 |";
        var blocks = new List<ParsedBlock>
        {
            new("table_row", "| 密封圈 | 2 |", null, 3, null, header),
            new("table_row", "| 轴承 | 1 |", null, 3, null, header)
        };
        var chunks = Chunker.Chunk(ChunkStrategy.ByRow, blocks, Opt);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.StartsWith(header, c.Text));
    }

    [Fact]
    public void 通用_超长段按窗口切分且相邻块有重叠()
    {
        var longText = string.Concat(Enumerable.Repeat("这是用于验证固定窗口切分逻辑的一段测试文字。", 60));
        var blocks = new List<ParsedBlock> { new("paragraph", longText, null, 1, null, null) };
        var chunks = Chunker.Chunk(ChunkStrategy.BySemantic, blocks, Opt);
        Assert.True(chunks.Count > 2);
        // 相邻块重叠（表 4-2 末段）：后块开头应出现在前块结尾
        var overlapHead = chunks[1].Text[..10];
        Assert.Contains(overlapHead, chunks[0].Text);
    }

    [Fact]
    public void 通用_跨章节不重叠()
    {
        var blocks = new List<ParsedBlock>
        {
            new("heading", "甲章", 1, 1, null, null),
            new("paragraph", "甲章内容甲章内容。", null, 1, null, null),
            new("heading", "乙章", 1, 1, null, null),
            new("paragraph", "乙章内容乙章内容。", null, 1, null, null)
        };
        var chunks = Chunker.Chunk(ChunkStrategy.General, blocks, Opt);
        Assert.Equal(2, chunks.Count);
        Assert.DoesNotContain("甲章内容", chunks[1].Text);
    }

    private static int CountOf(string text, string sub)
    {
        var count = 0;
        for (var i = text.IndexOf(sub, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
