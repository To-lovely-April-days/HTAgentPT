using System.Text;
using System.Text.RegularExpressions;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Dtos;
using HT.Agent.Domain;

namespace HT.Agent.Application.Logic;

/// <summary>切分（表 4-2）：输入解析引擎的结构块，输出待入库分块。
/// 做法是业界常用的组合：按文档结构切（标题、条款、表格各按各的单位），结构内再按整句递归切；
/// 切点和重叠都落在整句、整行上，不在句子中间截断；表格一行不拆开、每块都带表头；太碎的块并到相邻块。
/// 文件名、客户、项目、章节这些「身份」不写进分块正文，由入库时拼在检索和向量用的文字前面（见 ChunkContext）。</summary>
public static class Chunker
{
    public static IReadOnlyList<ChunkDraft> Chunk(ChunkStrategy strategy, IReadOnlyList<ParsedBlock> blocks, ChunkingOptions opt)
        => strategy switch
        {
            ChunkStrategy.ByRow => ByRow(blocks, opt),
            ChunkStrategy.ByClause => Structured(blocks, opt, clauses: true),
            ChunkStrategy.BySemantic => Structured(blocks.Where(b => b.Kind != "heading").ToList(), opt, clauses: false),
            _ => Structured(blocks, opt, clauses: false),
        };

    /// <summary>按文档的样子自动选切法：表格为主（Excel、参数表）按行；条款编号多而标题少（合同、协议）按条款；其余按章节结构。</summary>
    public static ChunkStrategy Pick(IReadOnlyList<ParsedBlock> blocks)
    {
        var rows = blocks.Count(b => b.Kind == "table_row");
        var paras = blocks.Where(b => b.Kind == "paragraph").ToList();
        var headings = blocks.Count(b => b.Kind == "heading");
        if (rows > 0 && rows >= 3 * Math.Max(1, paras.Count)) return ChunkStrategy.ByRow;
        var clauseLines = paras.Count(p => IsClauseStart(p.Text));
        if (headings < 3 && clauseLines >= 8 && clauseLines * 10 >= paras.Count * 3) return ChunkStrategy.ByClause;
        return headings >= 3 ? ChunkStrategy.ByHeading : ChunkStrategy.General;
    }

    // ── 条款号识别 ───────────────────────────────────
    private static readonly Regex ClauseNo = new(
        @"^\s*(第\s*[一二三四五六七八九十百零\d]+\s*[条章节款]|[一二三四五六七八九十]+\s*[、.．]|[（(]\s*[一二三四五六七八九十\d]+\s*[)）]|\d+(?:\.\d+){0,3}(?:[、.．]|\s+))",
        RegexOptions.Compiled);
    private static readonly Regex UnitAfter = new(
        @"^\s*(倍|%|‰|℃|°|mpa|kpa|pa\b|bar|psi|mm|cm|m\b|m²|m³|kg|g\b|t\b|l\b|ml|kw|w\b|rpm|v\b|a\b|hz|个|台|套|件|天|小时|分钟|h\b|min|s\b|年|月|日|元|万)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>这一行是不是一条新条款的开头。「3.2 设计参数」「第五条」「（一）」算；「1.25 倍」「16.0 MPa」「2024.5.6」不算。</summary>
    public static bool IsClauseStart(string line)
    {
        var m = ClauseNo.Match(line ?? "");
        if (!m.Success) return false;
        var rest = line![(m.Index + m.Length)..];
        if (rest.Trim().Length < 2 || UnitAfter.IsMatch(rest)) return false;
        var first = rest.TrimStart()[0];
        return first > 0x2E80 || char.IsLetter(first) || first is '“' or '"' or '《' or '【';
    }

    // ── 整句 ─────────────────────────────────────────
    private static readonly Regex SentenceEnd = new(@"(?<=[。！？；;!?])|(?<=\n)", RegexOptions.Compiled);

    /// <summary>切成整句；句子还太长就在逗号处切；再长才硬切（这种情况极少）。</summary>
    public static List<string> Sentences(string text, int max)
    {
        var list = new List<string>();
        foreach (var raw in SentenceEnd.Split(text ?? ""))
        {
            var s = raw.Trim();
            if (s.Length == 0) continue;
            if (s.Length <= max) { list.Add(s); continue; }
            var buf = new StringBuilder();
            foreach (var part in Regex.Split(s, @"(?<=[，,、])"))
            {
                if (buf.Length > 0 && buf.Length + part.Length > max) { list.Add(buf.ToString()); buf.Clear(); }
                if (part.Length > max)
                {
                    for (var i = 0; i < part.Length; i += max) list.Add(part.Substring(i, Math.Min(max, part.Length - i)));
                    continue;
                }
                buf.Append(part);
            }
            if (buf.Length > 0) list.Add(buf.ToString());
        }
        return list;
    }

    // ── 结构化切分（章节 / 通用 / 条款）─────────────────────
    private sealed record Unit(string Text, int? PageNo, string? Bbox);
    private sealed record Piece(string Text, int? PageNo, string? Bbox, bool Table);

    private static List<ChunkDraft> Structured(IReadOnlyList<ParsedBlock> blocks, ChunkingOptions opt, bool clauses)
    {
        var drafts = new List<ChunkDraft>();
        var headings = new List<(int Level, string Text)>();
        var section = new List<ParsedBlock>();

        void Flush()
        {
            if (section.Count == 0) return;
            var path = headings.Count == 0 ? null : string.Join(" > ", headings.Select(h => h.Text));
            var pieces = SectionPieces(section, opt, clauses);
            foreach (var p in clauses ? pieces : MergeTiny(pieces, opt))
                drafts.Add(new ChunkDraft(drafts.Count, path, p.PageNo, p.Bbox, p.Text));
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
            else if (b.Text.Trim().Length > 0) section.Add(b);
        }
        Flush();
        return drafts;
    }

    /// <summary>一个小节：连续的表格行成一张表、按行打包；正文按条款或整句打包。</summary>
    private static List<Piece> SectionPieces(List<ParsedBlock> section, ChunkingOptions opt, bool clauses)
    {
        var pieces = new List<Piece>();
        var prose = new List<Unit>();
        var i = 0;
        while (i < section.Count)
        {
            var b = section[i];
            if (b.Kind == "table_row")
            {
                pieces.AddRange(PackProse(prose, opt, clauses));
                prose.Clear();
                var rows = new List<ParsedBlock>();
                while (i < section.Count && section[i].Kind == "table_row" && section[i].TableHeader == b.TableHeader) rows.Add(section[i++]);
                pieces.AddRange(PackTable(rows, b.TableHeader, opt));
                continue;
            }
            prose.Add(new Unit(b.Text.Trim(), b.PageNo, b.Bbox));
            i++;
        }
        pieces.AddRange(PackProse(prose, opt, clauses));
        return pieces;
    }

    /// <summary>正文。条款模式：按行找条款号，一条一块（超长的条款内部再按整句切）；
    /// 普通模式：整节不长就整节一块，长了按整句攒到目标长度，相邻块用上一块末尾的整句做重叠。</summary>
    private static List<Piece> PackProse(List<Unit> units, ChunkingOptions opt, bool clauses)
    {
        if (units.Count == 0) return [];
        if (clauses)
        {
            var lines = units.SelectMany(u => u.Text.Split('\n').Select(l => new Unit(l.Trim(), u.PageNo, u.Bbox))).Where(u => u.Text.Length > 0).ToList();
            var groups = new List<List<Unit>>();
            foreach (var l in lines)
                if (groups.Count == 0 || IsClauseStart(l.Text)) groups.Add([l]); else groups[^1].Add(l);
            var pieces = new List<Piece>();
            foreach (var g in groups)
            {
                var text = string.Join("\n", g.Select(u => u.Text));
                if (text.Length <= opt.TargetLength * 3 / 2) pieces.Add(new Piece(text, g[0].PageNo, g[0].Bbox, false));
                else pieces.AddRange(PackWithOverlap(g.SelectMany(u => SentencesOf(u)).ToList(), opt));
            }
            return pieces;
        }
        var whole = string.Join("\n", units.Select(u => u.Text));
        if (whole.Length <= opt.TargetLength * 3 / 2) return [new Piece(whole, units[0].PageNo, units[0].Bbox, false)];
        return PackWithOverlap(units.SelectMany(u => SentencesOf(u)).ToList(), opt);
    }

    /// <summary>一段切成整句；段落最后一句后面带换行，拼回去时段落还在。</summary>
    private static IEnumerable<Unit> SentencesOf(Unit u)
    {
        var ss = Sentences(u.Text, 400);
        for (var i = 0; i < ss.Count; i++) yield return new Unit(i == ss.Count - 1 ? ss[i] + "\n" : ss[i], u.PageNo, u.Bbox);
    }

    private static List<Piece> PackWithOverlap(List<Unit> sentences, ChunkingOptions opt)
    {
        var result = new List<Piece>();
        var buf = new List<Unit>();
        var fresh = 0; // 本块新内容的长度（不含重叠）
        foreach (var s in sentences)
        {
            var len = buf.Sum(x => x.Text.Length);
            if (fresh > 0 && len + s.Text.Length > opt.TargetLength)
            {
                result.Add(new Piece(string.Concat(buf.Select(x => x.Text)).Trim(), buf[0].PageNo, buf[0].Bbox, false));
                // 重叠：上一块末尾的整句，总长不超过重叠长度
                var tail = new List<Unit>();
                var sum = 0;
                for (var k = buf.Count - 1; k >= 0 && sum + buf[k].Text.Length <= opt.Overlap; k--) { tail.Insert(0, buf[k]); sum += buf[k].Text.Length; }
                buf = tail;
                fresh = 0;
            }
            buf.Add(s);
            fresh += s.Text.Length;
        }
        if (fresh > 0) result.Add(new Piece(string.Concat(buf.Select(x => x.Text)).Trim(), buf[0].PageNo, buf[0].Bbox, false));
        return result;
    }

    /// <summary>表格：每块以表头开头，一行不拆开；单行超长时按整句切，续块带上表头和这一行的「序号 / 位号 / 名称」。</summary>
    private static List<Piece> PackTable(List<ParsedBlock> rows, string? header, ChunkingOptions opt)
    {
        var result = new List<Piece>();
        var head = string.IsNullOrWhiteSpace(header) ? "" : header!.Trim() + "\n";
        var buf = new StringBuilder();
        int? page = null; string? bbox = null;
        void Emit()
        {
            if (buf.Length == 0) return;
            result.Add(new Piece((head + buf).Trim(), page, bbox, true));
            buf.Clear(); page = null; bbox = null;
        }
        foreach (var r in rows)
        {
            var t = r.Text.Trim();
            if (head.Length + t.Length > opt.TargetLength * 3 / 2)
            {
                Emit();
                var key = RowKey(t);
                var first = true;
                foreach (var part in PackSentences(t, opt.TargetLength - head.Length - key.Length))
                {
                    result.Add(new Piece((head + (first ? "" : key + "（续）") + part).Trim(), r.PageNo, r.Bbox, true));
                    first = false;
                }
                continue;
            }
            if (buf.Length > 0 && head.Length + buf.Length + t.Length > opt.TargetLength) Emit();
            if (buf.Length == 0) { page = r.PageNo; bbox = r.Bbox; }
            buf.AppendLine(t);
        }
        Emit();
        return result;
    }

    /// <summary>一行表格的「身份」：前几个短格子（序号、位号、名称）。</summary>
    private static string RowKey(string row)
    {
        var cells = row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
        var key = cells.Take(4).Where(c => c.Length is > 0 and <= 20).Take(3).ToList();
        return key.Count == 0 ? "" : "| " + string.Join(" | ", key) + " | ";
    }

    private static IEnumerable<string> PackSentences(string text, int max)
    {
        max = Math.Max(200, max);
        var buf = new StringBuilder();
        foreach (var s in Sentences(text, max))
        {
            if (buf.Length > 0 && buf.Length + s.Length > max) { yield return buf.ToString(); buf.Clear(); }
            buf.Append(s);
        }
        if (buf.Length > 0) yield return buf.ToString();
    }

    /// <summary>太碎的块（不到最小长度的一半）并到后一块；是最后一块就并到前一块。表格块不和正文混并。</summary>
    private static List<Piece> MergeTiny(List<Piece> pieces, ChunkingOptions opt)
    {
        var min = Math.Max(40, opt.MinLength / 2);
        var list = new List<Piece>(pieces);
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Text.Length >= min || list.Count == 1) continue;
            if (i + 1 < list.Count && list[i + 1].Table == list[i].Table && list[i].Text.Length + list[i + 1].Text.Length <= opt.TargetLength * 2)
            {
                list[i + 1] = list[i + 1] with { Text = list[i].Text + "\n" + list[i + 1].Text, PageNo = list[i].PageNo ?? list[i + 1].PageNo, Bbox = list[i].Bbox ?? list[i + 1].Bbox };
                list.RemoveAt(i--);
            }
            else if (i > 0 && list[i - 1].Table == list[i].Table && list[i - 1].Text.Length + list[i].Text.Length <= opt.TargetLength * 2)
            {
                list[i - 1] = list[i - 1] with { Text = list[i - 1].Text + "\n" + list[i].Text };
                list.RemoveAt(i--);
            }
        }
        return list;
    }

    // ── 按行：参数表、物料清单、价格表（Excel 为主）─────────────
    // 一行一条完整记录，每块带表头；行间不重叠（同一条记录复制进相邻块会在检索结果里出现两份）。
    private static List<ChunkDraft> ByRow(IReadOnlyList<ParsedBlock> blocks, ChunkingOptions opt)
    {
        var chunks = new List<ChunkDraft>();
        string? sheet = null;
        foreach (var b in blocks)
        {
            if (b.Kind == "heading") { sheet = b.Text.Trim(); continue; }
            var text = b.Text.Trim();
            if (text.Length == 0) continue;
            if (b.Kind == "table_row")
            {
                var head = string.IsNullOrWhiteSpace(b.TableHeader) ? "" : b.TableHeader!.Trim() + "\n";
                if (head.Length + text.Length <= opt.TargetLength * 3 / 2)
                    chunks.Add(new ChunkDraft(chunks.Count, sheet, b.PageNo, b.Bbox, head + text));
                else
                {
                    var key = RowKey(text);
                    var first = true;
                    foreach (var part in PackSentences(text, opt.TargetLength - head.Length - key.Length))
                    {
                        chunks.Add(new ChunkDraft(chunks.Count, sheet, b.PageNo, b.Bbox, head + (first ? "" : key + "（续）") + part));
                        first = false;
                    }
                }
            }
            else
                foreach (var part in PackSentences(text, opt.TargetLength))
                    chunks.Add(new ChunkDraft(chunks.Count, sheet, b.PageNo, b.Bbox, part));
        }
        return chunks;
    }
}
