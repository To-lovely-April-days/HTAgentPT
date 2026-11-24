using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace HT.Agent.Infrastructure.Services;

/// <summary>把助手的回答（Markdown）存成 Word：标题、段落、加粗、列表、表格、引用块。够发给客户或领导看，不追求花哨排版。</summary>
public static class MarkdownDocx
{
    /// <summary>title 为空就不加标题（导出回答时只要正文，不把提问那句话当标题）。</summary>
    public static byte[] Build(string? title, string markdown, IReadOnlyList<string>? sources = null)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body());
            var body = main.Document.Body!;
            if (!string.IsNullOrWhiteSpace(title)) body.Append(Para(title, bold: true, size: 32));

            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith('|') && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], @"^\s*\|?\s*:?-{2,}"))
                {
                    var rows = new List<string> { line };
                    i += 2;
                    while (i < lines.Length && lines[i].TrimStart().StartsWith('|')) rows.Add(lines[i++]);
                    i--;
                    body.Append(Table(rows));
                    continue;
                }
                var h = Regex.Match(line, @"^(#{1,4})\s+(.*)$");
                if (h.Success) { body.Append(Para(h.Groups[2].Value, bold: true, size: h.Groups[1].Length switch { 1 => 30, 2 => 26, _ => 24 })); continue; }
                var li = Regex.Match(line, @"^\s*([-*+]|\d+[.)])\s+(.*)$");
                if (li.Success) { body.Append(Para((li.Groups[1].Value.Length > 1 || char.IsDigit(li.Groups[1].Value[0]) ? li.Groups[1].Value + " " : "• ") + li.Groups[2].Value, indent: true)); continue; }
                var q = Regex.Match(line, @"^\s*>\s?(.*)$");
                if (q.Success) { body.Append(Para(q.Groups[1].Value, color: "4A4A4A", indent: true, shade: true)); continue; }
                if (line.Trim().Length == 0) continue;
                body.Append(Para(line));
            }

            if (sources is { Count: > 0 })
            {
                body.Append(Para("参考资料", bold: true, size: 26));
                foreach (var s in sources) body.Append(Para(s, size: 20, color: "4A4A4A"));
            }
            main.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>一段文字：**加粗** 拆成加粗片段。</summary>
    private static Paragraph Para(string text, bool bold = false, int size = 22, string? color = null, bool indent = false, bool shade = false)
    {
        var p = new Paragraph();
        var pp = new ParagraphProperties(new SpacingBetweenLines { After = "120", Line = "320", LineRule = LineSpacingRuleValues.Auto });
        if (indent) pp.Append(new Indentation { Left = "420" });
        if (shade) pp.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "F3F3F1" });
        p.Append(pp);
        foreach (Match m in Regex.Matches(text, @"\*\*(.+?)\*\*|[^*]+|\*"))
        {
            var isBold = m.Value.StartsWith("**") && m.Value.Length > 4;
            var t = isBold ? m.Groups[1].Value : m.Value;
            var rp = new RunProperties(new RunFonts { EastAsia = "微软雅黑", Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = size.ToString() });
            if (bold || isBold) rp.Append(new Bold());
            if (color is not null) rp.Append(new Color { Val = color });
            p.Append(new Run(rp, new Text(t) { Space = SpaceProcessingModeValues.Preserve }));
        }
        return p;
    }

    private static Table Table(IReadOnlyList<string> rows)
    {
        var table = new Table(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" }, new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" }, new RightBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" }),
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }));
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
            var tr = new TableRow();
            foreach (var c in cells)
                tr.Append(new TableCell(new TableCellProperties(r == 0 ? new Shading { Val = ShadingPatternValues.Clear, Fill = "F3F3F1" } : new Shading()), Para(c, bold: r == 0, size: 20)));
            table.Append(tr);
        }
        return table;
    }
}
