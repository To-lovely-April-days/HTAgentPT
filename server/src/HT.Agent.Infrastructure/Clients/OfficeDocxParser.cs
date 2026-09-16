using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HT.Agent.Application.Abstractions;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>Word（.docx）本地解析：Word 文件本身就是结构化 XML，段落层级与表格单元格都是现成的，
/// 送外部版面引擎反而要先渲染再识别，慢、要出网、还可能识错合并单元格。所以 .docx 与 .txt/.md 同列，
/// 一律本地解析，不经外部服务（可在「系统设置」关掉改走引擎）。
/// 页码不给：docx 的分页由渲染时决定，文件里没有页号——宁可不标，也不标错页。</summary>
public class OfficeDocxParser : IDocumentParserClient
{
    public static bool Handles(string fileName)
        => Path.GetExtension(fileName).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedDocument> ParseAsync(Stream file, string fileName, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        if (ms.Length == 0) throw new ParseContentException("文件为空");
        ms.Position = 0;

        WordprocessingDocument doc;
        try
        {
            doc = WordprocessingDocument.Open(ms, false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or InvalidDataException)
        {
            throw new ParseContentException($"不是有效的 Word 文件或文件已损坏（{fileName}）：{ex.Message}");
        }
        using (doc)
        {
            var main = doc.MainDocumentPart ?? throw new ParseContentException($"Word 文件缺少主文档部件（{fileName}）");
            var body = main.Document.Body ?? throw new ParseContentException($"Word 文件没有正文内容（{fileName}）");

            var blocks = new List<ParsedBlock>();
            var images = new List<ParsedImage>();
            var styles = HeadingStyles(main);

            foreach (var element in body.ChildElements)
            {
                ct.ThrowIfCancellationRequested();
                switch (element)
                {
                    case Paragraph p:
                        AddParagraph(blocks, p, styles);
                        CollectImages(images, main, p, blocks);
                        break;
                    case Table t:
                        AddTable(blocks, t, main, images);
                        break;
                }
            }

            if (blocks.Count == 0) throw new ParseContentException($"未解析出任何内容块（{fileName} 可能只有图片或为空文档）");
            return new ParsedDocument(blocks, images.Count == 0 ? null : images);
        }
    }

    // ── 段落 ─────────────────────────────────────────────

    private static void AddParagraph(List<ParsedBlock> blocks, Paragraph p, Dictionary<string, int> styles)
    {
        var text = Clean(p.InnerText);
        if (text.Length == 0) return;
        var level = HeadingLevel(p, styles);
        blocks.Add(level is null
            ? new ParsedBlock("paragraph", text, null, null, null, null)
            : new ParsedBlock("heading", text, level, null, null, null));
    }

    /// <summary>标题层级：优先段落样式（Heading1/标题 1/自定义中文样式名），其次大纲级别。</summary>
    private static int? HeadingLevel(Paragraph p, Dictionary<string, int> styles)
    {
        var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (styleId is not null && styles.TryGetValue(styleId, out var byStyle)) return byStyle;
        var outline = p.ParagraphProperties?.OutlineLevel?.Val?.Value;
        // 大纲级别 0-8 对应标题 1-9；9 表示正文
        if (outline is >= 0 and <= 8) return outline.Value + 1;
        return null;
    }

    /// <summary>样式表里认标题的样式 → 层级。样式 ID 与显示名都看，中英文都认。</summary>
    private static Dictionary<string, int> HeadingStyles(MainDocumentPart main)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var styles = main.StyleDefinitionsPart?.Styles;
        if (styles is null) return map;
        foreach (var s in styles.Elements<Style>())
        {
            var id = s.StyleId?.Value;
            if (id is null) continue;
            var name = s.StyleName?.Val?.Value ?? "";
            var level = LevelFrom(id) ?? LevelFrom(name)
                        ?? (s.StyleParagraphProperties?.OutlineLevel?.Val?.Value is >= 0 and <= 8 and var o ? o + 1 : null);
            if (level is not null) map[id] = level.Value;
        }
        return map;

        static int? LevelFrom(string s)
        {
            var t = s.Replace(" ", "");
            foreach (var prefix in new[] { "heading", "标题" })
            {
                if (!t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var digits = new string(t[prefix.Length..].TakeWhile(char.IsDigit).ToArray());
                if (digits.Length > 0 && int.TryParse(digits, out var n) && n is >= 1 and <= 9) return n;
            }
            return null;
        }
    }

    // ── 表格 ─────────────────────────────────────────────

    /// <summary>表格按行出块，单元格以 | 分隔（与引擎侧口径一致，便于按行切分时带表头上下文）。
    /// 表头只在首行确实像表头时才认：多列、都不为空、都是不含数字的短标签——编号、日期、金额
    /// 都带数字，据此把任务单那种「标签|值|标签|值」的表单行认出来；表单首行不是表头，
    /// 硬当表头会给每行都挂上错误前缀，还会把首行数据吞掉。拿不准就不认表头。</summary>
    private static void AddTable(List<ParsedBlock> blocks, Table table, MainDocumentPart main, List<ParsedImage> images)
    {
        var rows = table.Elements<TableRow>().ToList();
        if (rows.Count == 0) return;

        var cellRows = rows.Select(r => r.Elements<TableCell>()
            .Select(c => Clean(c.InnerText)).ToList()).ToList();

        string? header = null;
        var first = cellRows[0];
        if (cellRows.Count > 1 && first.Count >= 2 &&
            first.All(c => c.Length is > 0 and <= 8 && !c.Any(char.IsDigit)))
            header = "| " + string.Join(" | ", first) + " |";

        for (var i = 0; i < cellRows.Count; i++)
        {
            var cells = cellRows[i];
            if (cells.All(c => c.Length == 0)) continue;
            if (header is not null && i == 0) continue;               // 表头行本身不重复出块
            blocks.Add(new ParsedBlock("table_row", "| " + string.Join(" | ", cells) + " |",
                null, null, null, header));
        }

        foreach (var cell in rows.SelectMany(r => r.Elements<TableCell>()))
            foreach (var p in cell.Descendants<Paragraph>())
                CollectImages(images, main, p, blocks);
    }

    // ── 图片 ─────────────────────────────────────────────

    /// <summary>嵌入图片按文档顺序取出，题注取图片所在段落文字或紧邻的「图 x …」说明。
    /// 单张图片取不出来不影响整篇解析。</summary>
    private static void CollectImages(List<ParsedImage> images, MainDocumentPart main, Paragraph p, List<ParsedBlock> blocks)
    {
        foreach (var blip in p.Descendants<A.Blip>())
        {
            var id = blip.Embed?.Value;
            if (id is null) continue;
            try
            {
                if (main.GetPartById(id) is not ImagePart part) continue;
                using var s = part.GetStream();
                using var buf = new MemoryStream();
                s.CopyTo(buf);
                if (buf.Length == 0) continue;
                var ext = part.ContentType switch
                {
                    "image/png" => ".png",
                    "image/jpeg" => ".jpg",
                    "image/gif" => ".gif",
                    "image/bmp" => ".bmp",
                    "image/tiff" => ".tif",
                    _ => ".bin"
                };
                var caption = Clean(p.InnerText);
                if (caption.Length == 0)
                    caption = p.Descendants<DW.DocProperties>().Select(d => d.Description?.Value ?? d.Name?.Value)
                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
                if (caption.Length == 0)
                    caption = blocks.LastOrDefault(b => b.Kind is "paragraph" or "heading")?.Text ?? "";
                images.Add(new ParsedImage(buf.ToArray(),
                    $"image-{images.Count + 1:D3}{ext}", part.ContentType,
                    caption.Length == 0 ? null : Truncate(caption, 120), null, null));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentOutOfRangeException)
            {
                // 单张图取不出来就跳过——不因为一张图让整篇解析失败
            }
        }
    }

    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c is '\t' or '\r' or '\n' or ' ' or '　' ? ' ' : c);
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        var text = sb.ToString().Trim();
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        return text;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
