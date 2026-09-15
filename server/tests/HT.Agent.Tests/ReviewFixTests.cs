using HT.Agent.Api.Controllers;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Dtos;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;

namespace HT.Agent.Tests;

/// <summary>对抗评审确认项的回归锁。每个测试名对应一条被确认的缺陷。</summary>
public class ReviewFixTests
{
    // ── #15 全角归一化 ──
    [Fact]
    public void 全角型号串可检索_NFKC归一化()
    {
        var indexed = ChineseTokenizer.Tokenize("ＣＪＦ－５Ｌ 报警").Split(' ');
        Assert.Contains("cjf-5l", indexed);
        // 索引侧全角、查询侧半角，同样命中
        var q = ChineseTokenizer.Tokenize("CJF-5L").Split(' ');
        Assert.Contains(q[0], indexed);
    }

    // ── #17 ByWindow 中部窗口不丢 ──
    [Fact]
    public void 窗口切分_中部短窗口保留_文末尾巴才可丢()
    {
        // 构造中部有大量空白导致 Trim 后偏短的窗口
        var text = new string('甲', 190) + new string(' ', 150) + new string('乙', 400);
        var opt = new ChunkingOptions(TargetLength: 200, Overlap: 20, MinLength: 100);
        var blocks = new List<ParsedBlock> { new("paragraph", text, null, 1, null, null) };
        var chunks = Chunker.Chunk(ChunkStrategy.BySemantic, blocks, opt);
        var joined = string.Concat(chunks.Select(c => c.Text));
        // 乙段的结尾必须存在于某个块里——旧实现会在中部 break 把后半篇全部丢掉
        Assert.Contains("乙乙乙", joined);
        Assert.EndsWith("乙", chunks[^1].Text);
    }

    // ── #16/#18 二次切分与按条款的相邻重叠 ──
    [Fact]
    public void 章节内二次切分保留重叠()
    {
        var paras = Enumerable.Range(0, 6).Select(i =>
            new ParsedBlock("paragraph", $"第{i}段" + new string((char)('a' + i), 120), null, 1, null, null));
        var blocks = new List<ParsedBlock> { new("heading", "长章节", 1, 1, null, null) };
        blocks.AddRange(paras);
        var opt = new ChunkingOptions(TargetLength: 150, Overlap: 30, MinLength: 40);
        var chunks = Chunker.Chunk(ChunkStrategy.ByHeading, blocks, opt);
        Assert.True(chunks.Count >= 2);
        // 第二块（去掉标题前缀行）应以第一块的尾部内容开头
        var first = chunks[0].Text;
        var secondBody = chunks[1].Text.Split('\n', 2)[1];
        Assert.Contains(first[^10..], secondBody[..50]);
    }

    [Fact]
    public void 按条款切分保留上一条尾部作语境()
    {
        var text = "第一条 " + new string('壹', 100) + "\n第二条 " + new string('贰', 100);
        var blocks = new List<ParsedBlock> { new("paragraph", text, null, 1, null, null) };
        var opt = new ChunkingOptions(TargetLength: 200, Overlap: 20, MinLength: 40);
        var chunks = Chunker.Chunk(ChunkStrategy.ByClause, blocks, opt);
        Assert.Equal(2, chunks.Count);
        Assert.Contains("壹壹", chunks[1].Text[..30]); // 上一条尾部前缀
        Assert.Contains("第二条", chunks[1].Text);
    }

    // ── #30 项目编号规则来自配置 ──
    [Fact]
    public void 项目编号正则由配置注入()
    {
        string[] none = [];
        // 默认无 pattern：陌生编号格式不触发台账
        Assert.Equal(IntentRouter.Knowledge, IntentRouter.Classify("X-2025/077 进展如何", none, none));
        // 部署配置了自家编号规则后触发
        Assert.Equal(IntentRouter.Ledger, IntentRouter.Classify("X-2025/077 进展如何", none, none, @"X-\d{4}/\d+"));
        // 非法正则不炸、不匹配
        Assert.Equal(IntentRouter.Knowledge, IntentRouter.Classify("随便问问", none, none, "(("));
    }

    // ── #2 CSV 公式注入中和 ──
    [Theory]
    [InlineData("=cmd|' /C calc'!A0", "\"'=cmd")]
    [InlineData("+1+1", "\"'+1+1\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("-2+3", "\"'-2+3\"")]
    [InlineData("正常文本", "\"正常文本\"")]
    public void 审计导出_公式前缀被中和(string input, string expectedPrefix)
    {
        var rows = new List<AuditRow>
        {
            new(1, DateTimeOffset.UnixEpoch, input, "act", null, null, null, AuditResult.Success, null, input)
        };
        var csv = System.Text.Encoding.UTF8.GetString(AuditCsv.Build(rows));
        Assert.Contains(expectedPrefix, csv);
    }

    // ── #（衍生）拆分逻辑不受影响 ──
    [Fact]
    public void 窗口重叠仍然成立()
    {
        var text = string.Concat(Enumerable.Repeat("固定窗口重叠验证文字。", 60));
        var opt = new ChunkingOptions(TargetLength: 200, Overlap: 20, MinLength: 40);
        var blocks = new List<ParsedBlock> { new("paragraph", text, null, 1, null, null) };
        var chunks = Chunker.Chunk(ChunkStrategy.BySemantic, blocks, opt);
        Assert.Contains(chunks[1].Text[..10], chunks[0].Text);
    }
}
