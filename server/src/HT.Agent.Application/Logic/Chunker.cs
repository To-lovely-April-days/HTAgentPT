using System.Text;
using System.Text.RegularExpressions;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Dtos;
using HT.Agent.Domain;

namespace HT.Agent.Application.Logic;

/// <summary>切分策略实现（表 4-2）。输入解析引擎的结构块，输出待入库分块。
/// 所有策略统一保留相邻块重叠区域（表 4-2 末段），避免答案恰好落在切分边界被截断。</summary>
public static class Chunker
{
    public static IReadOnlyList<ChunkDraft> Chunk(
        ChunkStrategy strategy, IReadOnlyList<ParsedBlock> blocks, ChunkingOptions opt)
        => strategy switch
        {
            ChunkStrategy.ByHeading => ByHeading(blocks, opt),
            ChunkStrategy.ByClause => ByClause(blocks, opt),
            ChunkStrategy.ByRow => ByRow(blocks),
            ChunkStrategy.BySemantic => ByWindow(blocks, opt, headingPrefix: false),
            _ => General(blocks, opt)
        };

    // ── 按章节层级：手册、规程、方案、投标书 ──────────────────────────
    // 以标题层级为边界，同一小节尽量不拆分；小节过长时按段落二次切分并保留标题作为前缀。
    private static List<ChunkDraft> ByHeading(IReadOnlyList<ParsedBlock> blocks, ChunkingOptions opt)
    {
        var chunks = new List<ChunkDraft>();
        var headings = new List<(int Level, string Text)>();
        var section = new List<ParsedBlock>();

        void Flush()
        {
            if (section.Count == 0) return;
            var path = HeadingPath(headings);
            var text = string.Join("\n", TextsWithTableHeaders(section)).Trim();
            if (text.Length == 0) { section.Clear(); return; }
            var first = section[0];
            if (text.Length <= opt.TargetLength * 3 / 2)
            {
                chunks.Add(new ChunkDraft(chunks.Count, path, first.PageNo, first.Bbox, Prefixed(path, text)));
            }
            else
            {
                // 小节过长：按段落二次切分，每块都带标题前缀
                foreach (var piece in SplitParagraphs(section, opt))
                    chunks.Add(new ChunkDraft(chunks.Count, path, piece.PageNo, piece.Bbox, Prefixed(path, piece.Text)));
            }
            section.Clear();
        }

        foreach (var b in blocks)
        {
            if (b.Kind == "heading")
            {
                Flush();
                var level = b.Level ?? 1;
                headings.RemoveAll(h => h.Level >= level);
                headings.Add((level, b.Text.Trim()));
            }
            else
            {
                section.Add(b);
            }
        }
        Flush();
        return chunks;
    }

    // ── 按条款：合同、报价单。每条独立成块，便于拼装时按条引用 ────────────
    private static readonly Regex ClauseStart = new(
        @"^\s*(第\s*[一二三四五六七八九十百零\d]+\s*条|\d+(\.\d+)+|\d+\s*[、.．]|[（(]\s*\d+\s*[)）])",
        RegexOptions.Compiled);

    private static List<ChunkDraft> ByClause(IReadOnlyList<ParsedBlock> blocks, ChunkingOptions opt)
    {
        var chunks = new List<ChunkDraft>();
        var current = new StringBuilder();
        int? pageNo = null; string? bbox = null; string? path = null;
        var headings = new List<(int, string)>();

        void Flush()
        {
            var text = current.ToString().Trim();
            if (text.Length > 0)
                chunks.Add(new ChunkDraft(chunks.Count, path, pageNo, bbox, text));
            current.Clear(); pageNo = null; bbox = null;
        }

        foreach (var b in blocks)
        {
            if (b.Kind == "heading")
            {
                Flush();
                var level = b.Level ?? 1;
                headings.RemoveAll(h => h.Item1 >= level);
                headings.Add((level, b.Text.Trim()));
                path = HeadingPath(headings);
                continue;
            }
            var lines = b.Text.Split('\n');
            foreach (var line in lines)
            {
                if (ClauseStart.IsMatch(line) && current.Length > 0) Flush();
                if (current.Length == 0) { pageNo = b.PageNo; bbox = b.Bbox; }
                current.AppendLine(line.TrimEnd());
            }
        }
        Flush();
        return chunks;
    }

    // ── 按行：参数表、物料清单。表头作为每块前缀，避免脱离表头后无法理解 ─────
    private static List<ChunkDraft> ByRow(IReadOnlyList<ParsedBlock> blocks)
    {
        var chunks = new List<ChunkDraft>();
        foreach (var b in blocks)
        {
            if (b.Kind is not ("table_row" or "paragraph" or "table")) continue;
            var text = b.Text.Trim();
            if (text.Length == 0) continue;
            if (b.Kind == "table_row" && !string.IsNullOrWhiteSpace(b.TableHeader))
                text = b.TableHeader!.Trim() + "\n" + text;
            chunks.Add(new ChunkDraft(chunks.Count, null, b.PageNo, b.Bbox, text));
        }
        return chunks;
    }

    // ── 语义段落：固定窗口配合重叠 ─────────────────────────────────
    private static List<ChunkDraft> ByWindow(IReadOnlyList<ParsedBlock> blocks, ChunkingOptions opt, bool headingPrefix)
    {
        var text = string.Join("\n", TextsWithTableHeaders(blocks.Where(b => b.Kind != "heading"))).Trim();
        var chunks = new List<ChunkDraft>();
        if (text.Length == 0) return chunks;
        var firstPage = blocks.FirstOrDefault(b => b.PageNo != null)?.PageNo;
        var step = Math.Max(1, opt.TargetLength - opt.Overlap);
        for (var pos = 0; pos < text.Length; pos += step)
        {
            var len = Math.Min(opt.TargetLength, text.Length - pos);
            var piece = text.Substring(pos, len).Trim();
            if (piece.Length < opt.MinLength && chunks.Count > 0) break; // 尾部碎片并入语义上一块的重叠区
            chunks.Add(new ChunkDraft(chunks.Count, null, firstPage, null, piece));
            if (pos + len >= text.Length) break;
        }
        return chunks;
    }

    // ── 通用：语义段落优先累积，超长时按固定窗口切分 ─────────────────────
    private static List<ChunkDraft> General(IReadOnlyList<ParsedBlock> blocks, ChunkingOptions opt)
    {
        var chunks = new List<ChunkDraft>();
        var headings = new List<(int, string)>();
        var buf = new StringBuilder();
        int? pageNo = null; string? bbox = null; string? path = null;
        string? lastTableHeader = null;
        string tail = string.Empty; // 上一块尾部，作为重叠前缀

        void Flush()
        {
            var text = buf.ToString().Trim();
            if (text.Length >= 1)
            {
                var withOverlap = tail.Length > 0 ? tail + "\n" + text : text;
                chunks.Add(new ChunkDraft(chunks.Count, path, pageNo, bbox, withOverlap));
                tail = text.Length > opt.Overlap ? text[^opt.Overlap..] : text;
            }
            buf.Clear(); pageNo = null; bbox = null;
        }

        foreach (var b in blocks)
        {
            if (b.Kind == "heading")
            {
                Flush();
                var level = b.Level ?? 1;
                headings.RemoveAll(h => h.Item1 >= level);
                headings.Add((level, b.Text.Trim()));
                path = HeadingPath(headings);
                tail = string.Empty; // 跨章节不重叠
                continue;
            }
            var t = WithTableHeader(b, ref lastTableHeader);
            if (t.Length == 0) continue;
            if (t.Length > opt.TargetLength * 2)
            {
                Flush();
                foreach (var piece in Window(t, opt))
                    chunks.Add(new ChunkDraft(chunks.Count, path, b.PageNo, b.Bbox, piece));
                tail = string.Empty;
                continue;
            }
            if (buf.Length > 0 && buf.Length + t.Length > opt.TargetLength) Flush();
            if (buf.Length == 0) { pageNo = b.PageNo; bbox = b.Bbox; }
            buf.AppendLine(t);
        }
        Flush();
        return chunks;
    }

    private static IEnumerable<string> Window(string text, ChunkingOptions opt)
    {
        var step = Math.Max(1, opt.TargetLength - opt.Overlap);
        for (var pos = 0; pos < text.Length; pos += step)
        {
            var len = Math.Min(opt.TargetLength, text.Length - pos);
            yield return text.Substring(pos, len);
            if (pos + len >= text.Length) yield break;
        }
    }

    private static IEnumerable<(string Text, int? PageNo, string? Bbox)> SplitParagraphs(
        List<ParsedBlock> section, ChunkingOptions opt)
    {
        var buf = new StringBuilder();
        int? pageNo = null; string? bbox = null;
        string? lastHeader = null;
        foreach (var b in section)
        {
            var t = WithTableHeader(b, ref lastHeader);
            if (t.Length == 0) continue;
            if (buf.Length > 0 && buf.Length + t.Length > opt.TargetLength)
            {
                yield return (buf.ToString().Trim(), pageNo, bbox);
                buf.Clear(); pageNo = null; bbox = null;
            }
            if (buf.Length == 0) { pageNo = b.PageNo; bbox = b.Bbox; }
            buf.AppendLine(t);
        }
        if (buf.Length > 0) yield return (buf.ToString().Trim(), pageNo, bbox);
    }

    private static string HeadingPath(IReadOnlyList<(int Level, string Text)> headings) =>
        string.Join(" > ", headings.Select(h => h.Text));

    private static string Prefixed(string? path, string text) =>
        string.IsNullOrEmpty(path) ? text : $"【{path}】\n{text}";

    // 表格行不脱离表头（表 4-2 的意图对所有策略成立，不只 ByRow）：
    // 表头变化时把表头行注入一次，行内容紧随其后。
    private static IEnumerable<string> TextsWithTableHeaders(IEnumerable<ParsedBlock> blocks)
    {
        string? lastHeader = null;
        foreach (var b in blocks)
        {
            var t = WithTableHeader(b, ref lastHeader);
            if (t.Length > 0) yield return t;
        }
    }

    private static string WithTableHeader(ParsedBlock b, ref string? lastHeader)
    {
        var t = b.Text.Trim();
        if (b.Kind == "table_row" && !string.IsNullOrWhiteSpace(b.TableHeader))
        {
            if (b.TableHeader != lastHeader)
            {
                lastHeader = b.TableHeader;
                return b.TableHeader.Trim() + "\n" + t;
            }
            return t;
        }
        if (b.Kind != "table_row") lastHeader = null;
        return t;
    }
}
