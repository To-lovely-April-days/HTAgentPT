using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Dtos;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Clients;

namespace HT.Agent.Tests;

public class ChunkingV2Tests
{
    private static readonly ChunkingOptions Opt = new(TargetLength: 200, Overlap: 40, MinLength: 80);

    [Theory]
    [InlineData("3.2 设计参数", true)]
    [InlineData("第五条 付款方式", true)]
    [InlineData("（一）甲方工作范围", true)]
    [InlineData("一、项目背景", true)]
    [InlineData("1.25 倍设计压力", false)]
    [InlineData("16.0 MPa", false)]
    [InlineData("2024.5.6", false)]
    [InlineData("60 天内交货", false)]
    public void Clause_numbers_are_not_confused_with_values(string line, bool clause) => Assert.Equal(clause, Chunker.IsClauseStart(line));

    [Theory]
    [InlineData(3, "decimal", "3")]
    [InlineData(12, "chineseCounting", "十二")]
    [InlineData(21, "chineseCountingThousand", "二十一")]
    [InlineData(105, "chineseCounting", "一百零五")]
    [InlineData(2, "ideographTraditional", "乙")]
    [InlineData(3, "decimalEnclosedCircle", "③")]
    [InlineData(28, "upperLetter", "AB")]
    [InlineData(4, "lowerRoman", "iv")]
    public void Word_number_formats(int n, string fmt, string expected) => Assert.Equal(expected, WordNumbering.Format(n, fmt));

    [Fact]
    public void Table_chunks_always_start_with_the_header_and_rows_are_not_split()
    {
        const string header = "| 序号 | 位号 | 名称 | 规格参数 |";
        var rows = Enumerable.Range(1, 8).Select(i => new ParsedBlock("table_row", $"| {i} | R-{i}01 | 反应釜 | 容积{i}0L，设计压力{i} MPa，材质316L。 |", null, 1, null, header)).ToList();
        var chunks = Chunker.Chunk(ChunkStrategy.ByHeading, rows, Opt);
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.StartsWith(header, c.Text));
        // 每一行完整地落在某一块里
        foreach (var r in rows) Assert.Contains(chunks, c => c.Text.Contains(r.Text));
    }

    [Fact]
    public void Very_long_row_is_split_by_sentences_and_continuations_keep_the_row_key()
    {
        const string header = "| 序号 | 位号 | 名称 | 规格参数 |";
        var spec = string.Concat(Enumerable.Range(1, 30).Select(i => $"第{i}项参数为{i}0。"));
        var row = new ParsedBlock("table_row", $"| 1 | R-101 | 反应釜 | {spec} |", null, 1, null, header);
        var chunks = Chunker.Chunk(ChunkStrategy.ByHeading, [row], Opt);
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.StartsWith(header, c.Text));
        Assert.All(chunks.Skip(1), c => Assert.Contains("R-101", c.Text));
        // 切点落在整句上：每块最后一行以句号或表格竖线结尾
        Assert.All(chunks, c => Assert.Matches("[。|]$", c.Text.TrimEnd()));
    }

    [Fact]
    public void Tiny_pieces_are_merged_but_sections_stay_separate()
    {
        var blocks = new List<ParsedBlock>
        {
            new("heading", "一、概述", 1, 1, null, null),
            new("paragraph", "很短。", null, 1, null, null),
            new("table_row", "| 1 | 甲 |", null, 1, null, "| 序号 | 名称 |"),
            new("heading", "二、范围", 1, 1, null, null),
            new("paragraph", "第二节的内容。", null, 1, null, null),
        };
        var chunks = Chunker.Chunk(ChunkStrategy.ByHeading, blocks, Opt);
        Assert.Equal(["一、概述", "一、概述", "二、范围"], chunks.Select(c => c.SectionPath));
    }

    [Fact]
    public void Strategy_is_picked_from_the_shape_of_the_document()
    {
        var excel = Enumerable.Range(0, 20).Select(i => new ParsedBlock("table_row", $"| {i} | x |", null, null, null, "| a | b |")).ToList();
        Assert.Equal(ChunkStrategy.ByRow, Chunker.Pick(excel));
        var contract = Enumerable.Range(1, 12).Select(i => new ParsedBlock("paragraph", $"第{WordNumbering.Chinese(i)}条 双方约定事项{i}。", null, null, null, null)).ToList();
        Assert.Equal(ChunkStrategy.ByClause, Chunker.Pick(contract));
        var manual = new List<ParsedBlock>
        {
            new("heading", "第一章", 1, null, null, null), new("paragraph", "内容", null, null, null, null),
            new("heading", "第二章", 1, null, null, null), new("paragraph", "内容", null, null, null, null),
            new("heading", "第三章", 1, null, null, null), new("paragraph", "内容", null, null, null, null),
        };
        Assert.Equal(ChunkStrategy.ByHeading, Chunker.Pick(manual));
    }

    [Fact]
    public void Context_header_carries_document_identity()
    {
        var h = ChunkContext.Header("尼龙66合成装置技术协议", "杭州本松新材料", "HT-2026-031", "反应釜", "技术协议", "第三章 > 设备清单", "R-101 反应釜的设计参数");
        Assert.Contains("客户：杭州本松新材料", h);
        Assert.Contains("项目：HT-2026-031", h);
        Assert.Contains("章节：第三章 > 设备清单", h);
        Assert.EndsWith("R-101 反应釜的设计参数", h);
        Assert.Equal("正文", ChunkContext.ForIndex(null, "正文"));
        Assert.Null(ChunkContext.CleanSummary("  \n"));
        Assert.Equal("讲 R-101 的设计压力", ChunkContext.CleanSummary("「讲 R-101 的设计压力」\n多余的话"));
    }

    [Fact]
    public async Task Word_numbering_merged_cells_toc_and_noise()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var np = main.AddNewPart<NumberingDefinitionsPart>();
            np.Numbering = new Numbering(
                new AbstractNum(
                    new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.ChineseCounting }, new LevelText { Val = "%1、" }) { LevelIndex = 0 },
                    new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Decimal }, new IsLegalNumberingStyle(), new LevelText { Val = "%1.%2" }) { LevelIndex = 1 })
                { AbstractNumberId = 1 },
                new AbstractNum(new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Decimal }, new LevelText { Val = "%1" }) { LevelIndex = 0 }) { AbstractNumberId = 2 },
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
                new NumberingInstance(new AbstractNumId { Val = 2 }) { NumberID = 2 });
            Paragraph Num(string text, int numId, int lvl) => new(new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = lvl }, new NumberingId { Val = numId })), new Run(new Text(text)));
            TableCell Cell(string text, int? numId = null, bool vmergeContinue = false, bool vmergeStart = false)
            {
                var props = new TableCellProperties();
                if (vmergeStart) props.Append(new VerticalMerge { Val = MergedCellValues.Restart });
                if (vmergeContinue) props.Append(new VerticalMerge());
                var p = numId is null ? new Paragraph(new Run(new Text(text))) : Num(text, numId.Value, 0);
                return new TableCell(props, p);
            }
            var toc = new Paragraph(new Run(new FieldCode(" TOC \\o \"1-1\" \\h ")), new Run(new Text("第一章 概述 3")));
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("甲方签字："))),
                new Paragraph(new Run(new Text("5326380387985注意保密注意保密"))),
                toc,
                Num("项目背景", 1, 0),
                Num("设计要求", 1, 1),
                Num("安全要求", 1, 1),
                Num("工作范围", 1, 0),
                new Table(
                    new TableRow(Cell("序号"), Cell("分组"), Cell("名称")),
                    new TableRow(Cell("", 2), Cell("主体", vmergeStart: true), Cell("反应釜")),
                    new TableRow(Cell("", 2), Cell("", vmergeContinue: true), Cell("冷凝器")))));
        }
        ms.Position = 0;
        var parsed = await new OfficeParser().ParseAsync(ms, "t.docx", "", default);
        var texts = parsed.Blocks.Select(b => b.Text).ToList();
        Assert.Contains("一、项目背景", texts);
        Assert.Contains("1.1 设计要求", texts);
        Assert.Contains("1.2 安全要求", texts);
        Assert.Contains("二、工作范围", texts);
        Assert.Contains("| 1 | 主体 | 反应釜 |", texts);
        Assert.Contains("| 2 | 主体 | 冷凝器 |", texts);  // 纵向合并的格补上了
        Assert.DoesNotContain(texts, t => t.Contains("签字") || t.Contains("注意保密") || t.Contains("TOC") || t.Contains("概述 3"));
    }
}
