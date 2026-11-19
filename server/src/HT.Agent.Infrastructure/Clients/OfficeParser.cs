using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;
using HT.Agent.Application.Abstractions;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using V = DocumentFormat.OpenXml.Vml;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>Office 文件（Word / Excel / PowerPoint 的 OOXML 格式）本地解析：这些文件本身就是
/// 结构化 XML，标题层级、表格单元格、工作表、幻灯片都是现成的，送外部版面引擎要先渲染再识别，
/// 慢、要出网、还可能识错合并单元格；Excel 多数引擎根本不收。所以与 .txt/.md 同列，一律本地解析。
/// 文中图片一并抽出（新式 DrawingML 与老式 VML 都认——从 .doc 转存或 WPS 产出的文档常是后者）。
/// 页码不给：OOXML 的分页由渲染时决定，文件里没有页号——宁可不标，也不标错页。</summary>
public class OfficeParser : IDocumentParserClient
{
    // Word 全家族：文档、启用宏的文档、模板都是同一套 WordprocessingML
    private static readonly string[] WordExt = [".docx", ".docm", ".dotx", ".dotm"];
    private static readonly string[] ExcelExt = [".xlsx", ".xlsm", ".xltx", ".xltm"];
    private static readonly string[] PptExt = [".pptx", ".pptm", ".potx", ".potm"];

    /// <summary>老格式与非 OOXML 的 Office 文件：本地读不了（不是 XML 包），
    /// 给出可执行的提示而不是让它撞在引擎上出一句看不懂的错。</summary>
    private static readonly string[] LegacyExt = [".doc", ".xls", ".ppt", ".rtf", ".wps", ".et", ".dps", ".odt"];

    public static bool Handles(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return WordExt.Contains(ext) || ExcelExt.Contains(ext) || PptExt.Contains(ext);
    }

    public static bool IsLegacyOffice(string fileName)
        => LegacyExt.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    public async Task<ParsedDocument> ParseAsync(Stream file, string fileName, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        if (ms.Length == 0) throw new ParseContentException("文件为空");
        ms.Position = 0;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var blocks = new List<ParsedBlock>();
        var images = new ImageBag();
        try
        {
            if (WordExt.Contains(ext)) ParseWord(ms, blocks, images, ct);
            else if (ExcelExt.Contains(ext)) ParseExcel(ms, blocks, images, ct);
            else if (PptExt.Contains(ext)) ParsePresentation(ms, blocks, images, ct);
            else throw new ParseContentException($"本地解析不支持的格式（{fileName}）");
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or InvalidDataException or ArgumentOutOfRangeException)
        {
            throw new ParseContentException($"文件无法解析或已损坏（{fileName}）：{ex.Message}");
        }

        if (blocks.All(b => b.Kind == "image") && images.Items.Count > 0)
        {
            // 只有图没有字（扫描件转存、整页贴图）：给一条说明块，图片照常入库可检索；
            // 图片位置记号接在后面，分块视图照样能配图
            var marks = string.Concat(blocks.Select(b => b.Text));
            blocks.Clear();
            blocks.Add(new ParsedBlock("paragraph",
                $"本文档没有可提取的文字，含 {images.Items.Count} 张图片" +
                (images.Captions().Length == 0 ? "。" : $"：{images.Captions()}") + marks, null, null, null, null));
        }
        if (blocks.Count == 0)
            throw new ParseContentException($"未解析出任何内容（{fileName} 可能是空文档）");
        return new ParsedDocument(blocks, images.Items.Count == 0 ? null : images.Items);
    }

    // ── Word ─────────────────────────────────────────────

    private static void ParseWord(Stream ms, List<ParsedBlock> blocks, ImageBag images, CancellationToken ct)
    {
        using var doc = WordprocessingDocument.Open(ms, false);
        var main = doc.MainDocumentPart ?? throw new ParseContentException("Word 文件缺少主文档部件");
        var body = main.Document?.Body ?? throw new ParseContentException("Word 文件没有正文内容");
        var styles = HeadingStyles(main);
        var numbering = new WordNumbering(main);

        // 内容控件（模板里常见）里面的段落和表格也要读，不能只看正文第一层
        void Walk(IEnumerable<OpenXmlElement> elements)
        {
            foreach (var element in elements)
            {
                ct.ThrowIfCancellationRequested();
                switch (element)
                {
                    case Paragraph p:
                        AddParagraph(blocks, p, styles, numbering);
                        CollectWordImages(images, main, p, blocks);
                        break;
                    case W.Table t:
                        AddTable(blocks, t, main, images, numbering);
                        break;
                    case SdtBlock sdt when sdt.SdtContentBlock is { } content:
                        Walk(content.ChildElements);
                        break;
                }
            }
        }
        Walk(body.ChildElements);
    }

    /// <summary>段落文字，前面补上自动编号（「3.2」「一、」）。</summary>
    /// <summary>段落里真正显示的文字：只取正文字符，不要域代码（目录、页码引用的 TOC / PAGEREF 指令）和修订删除的字。</summary>
    private static string VisibleText(Paragraph p)
        => Clean(string.Concat(p.Descendants().Select(e => e switch
        {
            Text t => t.Text,
            TabChar or Break or CarriageReturn => " ",
            _ => "",
        })));

    /// <summary>目录里的条目（指向 _Toc 书签、带页码）：和正文标题重复，只是噪声。</summary>
    private static bool IsTocEntry(Paragraph p)
        => p.Descendants<FieldCode>().Any(f => f.Text.Contains("_Toc") || f.Text.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase))
           || (p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "").StartsWith("TOC", StringComparison.OrdinalIgnoreCase);

    private static string ParagraphText(Paragraph p, WordNumbering numbering, bool inCell = false)
    {
        var label = numbering.Next(p);
        var text = VisibleText(p);
        if (label is null) return text;
        // 表格里只有编号的格子（序号列）编号本身就是内容；正文里只有编号的空段落不要
        if (text.Length == 0) return inCell ? label : "";
        return label[^1] is '、' or '.' or '．' or '）' or ')' or '：' ? label + text : label + " " + text;
    }

    /// <summary>封面上没填的表单项（「甲方签字：」「签订日期：」）、重复的水印字样（「注意保密注意保密」），对检索只是噪声。</summary>
    public static bool IsNoise(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return true;
        if (t.Length <= 10 && (t.EndsWith('：') || t.EndsWith(':'))) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^目\s*录$")) return true;
        var body = System.Text.RegularExpressions.Regex.Replace(t, @"^\d{6,}", "");
        var m = System.Text.RegularExpressions.Regex.Match(body, @"^(.{2,8}?)\1+$");
        return m.Success;
    }

    private static void AddParagraph(List<ParsedBlock> blocks, Paragraph p, Dictionary<string, int> styles, WordNumbering numbering)
    {
        if (IsTocEntry(p)) { numbering.Next(p); return; }
        var text = ParagraphText(p, numbering);
        if (text.Length == 0) return;
        var level = HeadingLevel(p, styles);
        if (level is null && IsNoise(text)) return;
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
        return outline is >= 0 and <= 8 ? outline.Value + 1 : null;  // 0-8 → 标题 1-9，9 是正文
    }

    private static Dictionary<string, int> HeadingStyles(MainDocumentPart main)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var styles = main.StyleDefinitionsPart?.Styles;
        if (styles is null) return map;
        foreach (var s in styles.Elements<Style>())
        {
            var id = s.StyleId?.Value;
            if (id is null) continue;
            var level = LevelFrom(id) ?? LevelFrom(s.StyleName?.Val?.Value ?? "")
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

    /// <summary>表格按行出块，单元格以 | 分隔（与引擎侧口径一致，便于按行切分时带表头上下文）。
    /// 表头只在首行确实像表头时才认：多列、都不为空、都是不含数字的短标签——编号、日期、金额
    /// 都带数字，据此把任务单那种「标签|值|标签|值」的表单行认出来；表单首行不是表头，
    /// 硬当表头会给每行都挂上错误前缀，还会把首行数据吞掉。拿不准就不认表头。</summary>
    private static void AddTable(List<ParsedBlock> blocks, W.Table table, MainDocumentPart main, ImageBag images, WordNumbering numbering)
    {
        var rows = table.Elements<W.TableRow>().ToList();
        if (rows.Count == 0) return;
        // 按网格展开：横向合并的格占几列就占几列（后面几列留空，列和表头对得上）；
        // 纵向合并的格，下面几行补上合并区第一行的值（否则「分组」「名称」一列只有第一行有字）
        var cellRows = new List<List<string>>();
        var above = new Dictionary<int, string>();
        foreach (var r in rows)
        {
            var cells = new List<string>();
            foreach (var c in r.Elements<W.TableCell>())
            {
                var span = c.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
                var vm = c.TableCellProperties?.VerticalMerge;
                var col = cells.Count;
                string text;
                if (vm is not null && (vm.Val is null || vm.Val.InnerText == "continue"))
                    text = above.TryGetValue(col, out var up) ? up : "";
                else
                    text = string.Join(" ", c.Descendants<Paragraph>().Select(p => ParagraphText(p, numbering, inCell: true)).Where(x => x.Length > 0));
                above[col] = text;
                cells.Add(text);
                for (var k = 1; k < span; k++) { above[col + k] = ""; cells.Add(""); }
            }
            cellRows.Add(cells);
        }
        var header = HeaderOf(cellRows);

        for (var i = 0; i < cellRows.Count; i++)
        {
            if (header is not null && i == 0) continue;            // 表头行本身不重复出块
            var cells = cellRows[i];
            if (cells.All(c => c.Length == 0)) continue;
            blocks.Add(new ParsedBlock("table_row", Join(cells), null, null, null, header));
        }

        foreach (var p in rows.SelectMany(r => r.Elements<W.TableCell>()).SelectMany(c => c.Descendants<Paragraph>()))
            CollectWordImages(images, main, p, blocks);
    }

    /// <summary>Word 段落里的图片：新式 DrawingML（a:blip）与老式 VML（v:imagedata）都取。
    /// 后者在从 .doc 另存、WPS 产出的文档里很常见，只认前者会整篇图都丢。</summary>
    private static void CollectWordImages(ImageBag images, MainDocumentPart main, Paragraph p, List<ParsedBlock> blocks)
    {
        var ids = p.Descendants<A.Blip>().Select(b => b.Embed?.Value)
            .Concat(p.Descendants<V.ImageData>().Select(d => d.RelationshipId?.Value))
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!);
        var marks = new List<int>();
        foreach (var id in ids)
        {
            var caption = Clean(p.InnerText);
            if (caption.Length == 0)
                caption = p.Descendants<DW.DocProperties>()
                    .Select(d => d.Description?.Value ?? d.Name?.Value)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
            if (caption.Length == 0)
                caption = blocks.LastOrDefault(b => b.Kind is "paragraph" or "heading")?.Text ?? "";
            if (images.TryAdd(main, id, caption) is { } n) marks.Add(n);
        }
        AddImageMarks(blocks, marks);
    }

    /// <summary>图片位置记号，如「〔图3〕」：数字是图片序号（从 1 起）。</summary>
    public static string ImageMark(int n) => $"〔图{n}〕";

    private static void AddImageMarks(List<ParsedBlock> blocks, List<int> marks)
    {
        if (marks.Count == 0) return;
        blocks.Add(new ParsedBlock("image", string.Concat(marks.Distinct().Select(ImageMark)), null, null, null, null));
    }

    // ── Excel ────────────────────────────────────────────

    /// <summary>工作表逐张出块：表名作标题，行按 | 分隔出 table_row（列位置按单元格引用对齐，
    /// 空列补空，否则稀疏行会让表头与取值错位）。</summary>
    private static void ParseExcel(Stream ms, List<ParsedBlock> blocks, ImageBag images, CancellationToken ct)
    {
        using var doc = SpreadsheetDocument.Open(ms, false);
        var wb = doc.WorkbookPart ?? throw new ParseContentException("Excel 文件缺少工作簿部件");
        var sst = wb.SharedStringTablePart?.SharedStringTable;
        var sheets = wb.Workbook?.Sheets?.Elements<S.Sheet>().ToList() ?? [];

        foreach (var sheet in sheets)
        {
            ct.ThrowIfCancellationRequested();
            if (sheet.Id?.Value is not { } relId || wb.GetPartById(relId) is not WorksheetPart wsPart) continue;
            var name = Clean(sheet.Name?.Value ?? "工作表");
            blocks.Add(new ParsedBlock("heading", name, 1, null, null, null));

            var cellRows = new List<List<string>>();
            foreach (var row in wsPart.Worksheet.Descendants<S.Row>().Take(5000))
            {
                ct.ThrowIfCancellationRequested();
                var cells = new List<string>();
                foreach (var c in row.Elements<S.Cell>())
                {
                    var col = ColumnIndex(c.CellReference?.Value);
                    while (col > 0 && cells.Count < col - 1) cells.Add("");   // 稀疏行补空列
                    cells.Add(Truncate(Clean(CellText(c, sst)), 200));
                }
                if (cells.Any(v => v.Length > 0)) cellRows.Add(cells);
            }
            if (cellRows.Count == 0) continue;

            var header = HeaderOf(cellRows);
            for (var i = 0; i < cellRows.Count; i++)
            {
                if (header is not null && i == 0) continue;
                blocks.Add(new ParsedBlock("table_row", Join(cellRows[i]), null, null, null, header));
            }
            foreach (var img in wsPart.DrawingsPart?.ImageParts ?? [])
                images.TryAdd(img, name);
        }
    }

    private static string CellText(S.Cell c, S.SharedStringTable? sst)
    {
        var raw = c.CellValue?.InnerText ?? c.InlineString?.InnerText ?? "";
        if (c.DataType?.Value == S.CellValues.SharedString && sst is not null &&
            int.TryParse(raw, out var idx) && idx >= 0 && idx < sst.ChildElements.Count)
            return sst.ElementAt(idx).InnerText;
        if (c.DataType?.Value == S.CellValues.Boolean) return raw == "1" ? "TRUE" : "FALSE";
        return raw;
    }

    /// <summary>单元格引用（A1/BC12）→ 列序号（1 起）；取不出来返回 0 表示按顺序排。</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 0;
        var n = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n;
    }

    // ── PowerPoint ───────────────────────────────────────

    /// <summary>每页幻灯片：标题作标题块，其余文字逐段出块，页内图片一并取出。</summary>
    private static void ParsePresentation(Stream ms, List<ParsedBlock> blocks, ImageBag images, CancellationToken ct)
    {
        using var doc = PresentationDocument.Open(ms, false);
        var pres = doc.PresentationPart ?? throw new ParseContentException("演示文稿缺少主部件");
        var ids = pres.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList() ?? [];
        var no = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            no++;
            if (id.RelationshipId?.Value is not { } relId || pres.GetPartById(relId) is not SlidePart slide) continue;

            var texts = slide.Slide?.Descendants<A.Paragraph>()
                .Select(p => Clean(p.InnerText)).Where(t => t.Length > 0).ToList() ?? [];
            blocks.Add(new ParsedBlock("heading", texts.Count > 0 ? texts[0] : $"第 {no} 页", 1, null, null, null));
            foreach (var t in texts.Skip(1))
                blocks.Add(new ParsedBlock("paragraph", t, null, null, null, null));
            var slideMarks = new List<int>();
            foreach (var img in slide.ImageParts)
                if (images.TryAdd(img, texts.Count > 0 ? texts[0] : $"第 {no} 页") is { } n) slideMarks.Add(n);
            AddImageMarks(blocks, slideMarks);
        }
    }

    // ── 图片收集（去重、过滤、上限）─────────────────────────

    /// <summary>图片抽取的共同规矩：同一图片部件只取一次（页眉 logo、反复插入的同一张图不重复入库）；
    /// 只留浏览器能直接显示的格式（EMF/WMF 这类矢量剪贴板格式存下来也只会是破图）；
    /// 过滤项目符号、分隔线之类的小图标；整篇设上限，防止一份图册把库撑爆。</summary>
    private sealed class ImageBag
    {
        private const int MinPixels = 64;       // 边长小于 64 px 的是图标/项目符号/分隔线
        private const int MinBytesUnknown = 2048; // 读不出尺寸时退回体积判断
        private const int MaxImages = 200;
        private static readonly string[] Renderable =
            ["image/png", "image/jpeg", "image/gif", "image/bmp", "image/webp", "image/svg+xml"];

        private readonly Dictionary<string, int?> _seen = [];
        public readonly List<ParsedImage> Items = [];

        /// <summary>收下一张图，返回它的序号（从 1 起，和入库后的图片顺序一致）；没收（图标、格式不支持、读不出）返回 null。</summary>
        public int? TryAdd(MainDocumentPart main, string relationshipId, string? caption)
        {
            try
            {
                return main.GetPartById(relationshipId) is ImagePart part ? TryAdd(part, caption) : null;
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException or KeyNotFoundException)
            {
                // 关系 ID 指不到部件（文档被改坏）：跳过这一张，不影响整篇
                return null;
            }
        }

        public int? TryAdd(ImagePart part, string? caption)
        {
            var key = part.Uri.ToString();
            if (_seen.TryGetValue(key, out var known)) return known;
            var index = Add(part, caption);
            _seen[key] = index;
            return index;
        }

        private int? Add(ImagePart part, string? caption)
        {
            if (Items.Count >= MaxImages) return null;
            var contentType = part.ContentType;
            // EMF/WMF：Visio、CAD 粘进 Word 的 PID 图、装配图多是这种矢量格式。
            // 服务器装了转换工具（Inkscape 或 LibreOffice）就转成 PNG 留下，没装照旧跳过
            var vector = VectorImageConverter.IsVector(contentType);
            if (!vector && !Renderable.Contains(contentType, StringComparer.OrdinalIgnoreCase)) return null;
            try
            {
                using var s = part.GetStream();
                using var buf = new MemoryStream();
                s.CopyTo(buf);
                var bytes = buf.ToArray();
                if (vector)
                {
                    var png = VectorImageConverter.ToPng(bytes, contentType);
                    if (png is null) return null;
                    bytes = png;
                    contentType = "image/png";
                }
                // 按像素尺寸筛掉图标：线条图、纯色示意图压缩后可能只有几百字节，
                // 单看体积会把正经插图当图标误杀；读不出尺寸的才退回体积判断
                var size = TryReadSize(bytes);
                if (size is { } wh)
                {
                    if (wh.W < MinPixels || wh.H < MinPixels) return null;
                }
                else if (bytes.Length < MinBytesUnknown) return null;
                var ext = contentType.ToLowerInvariant() switch
                {
                    "image/png" => ".png",
                    "image/jpeg" => ".jpg",
                    "image/gif" => ".gif",
                    "image/bmp" => ".bmp",
                    "image/webp" => ".webp",
                    "image/svg+xml" => ".svg",
                    _ => ".bin"
                };
                var text = Clean(caption ?? "");
                Items.Add(new ParsedImage(bytes, $"image-{Items.Count + 1:D3}{ext}", contentType,
                    text.Length == 0 ? null : Truncate(text, 120), null, null));
                return Items.Count;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // 单张图读不出来就跳过——不因为一张图让整篇解析失败
                return null;
            }
        }

        public string Captions()
            => string.Join("、", Items.Select(i => i.Caption).Where(c => !string.IsNullOrWhiteSpace(c)).Take(5));

        /// <summary>从图片字节里读宽高（PNG/GIF/BMP 的文件头，JPEG 扫 SOF 段）。
        /// 读不出来返回 null，由调用方退回体积判断——不解码像素，只看头部。</summary>
        private static (int W, int H)? TryReadSize(byte[] b)
        {
            try
            {
                // PNG：signature + IHDR(width,height 大端)
                if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 &&
                    b[12] == (byte)'I' && b[13] == (byte)'H' && b[14] == (byte)'D' && b[15] == (byte)'R')
                    return (Be32(b, 16), Be32(b, 20));
                // GIF：header + 宽高小端
                if (b.Length >= 10 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
                    return (b[6] | (b[7] << 8), b[8] | (b[9] << 8));
                // BMP：DIB 头里的宽高小端
                if (b.Length >= 26 && b[0] == (byte)'B' && b[1] == (byte)'M')
                    return (Le32(b, 18), Math.Abs(Le32(b, 22)));
                // JPEG：逐段找 SOF0/1/2/3 等帧头，段里是高、宽（大端）
                if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
                {
                    var i = 2;
                    while (i + 9 < b.Length)
                    {
                        if (b[i] != 0xFF) { i++; continue; }
                        var marker = b[i + 1];
                        if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                            return (b[i + 7] << 8 | b[i + 8], b[i + 5] << 8 | b[i + 6]);
                        var len = b[i + 2] << 8 | b[i + 3];
                        if (len <= 0) break;
                        i += 2 + len;
                    }
                }
            }
            catch (IndexOutOfRangeException) { /* 头部被截断：当作读不出尺寸 */ }
            return null;

            static int Be32(byte[] b, int i) => b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3];
            static int Le32(byte[] b, int i) => b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24;
        }
    }

    // ── 公共小工具 ────────────────────────────────────────

    private static string? HeaderOf(List<List<string>> rows)
    {
        if (rows.Count <= 1) return null;
        var first = rows[0];
        var filled = first.Where(c => c.Length > 0).ToList();
        // 合并单元格展开后表头里会有空格子：有字的格子至少两个、占一半以上，并且都像列名
        return filled.Count >= 2 && filled.Count * 2 >= first.Count && filled.All(c => c.Length <= 12 && !c.Any(char.IsDigit))
            ? Join(first) : null;
    }

    private static string Join(IEnumerable<string> cells) => "| " + string.Join(" | ", cells) + " |";

    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c is '\t' or '\r' or '\n' or ' ' or '　' or '\v' ? ' ' : c);
        var text = sb.ToString().Trim();
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        return text;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
