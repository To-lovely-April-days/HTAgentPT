using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using W14 = DocumentFormat.OpenXml.Office2010.Word;

namespace HT.Agent.Infrastructure.Templates;

/// <summary>模板规整：给没有 Tag 的内容控件按上下文自动补 Tag 与「章节/名称」标题，
/// 让业务人员直接在 Word 里做的模板（只放控件、不设 Tag，如技术协议模板的 600 多处控件）上传即可用。
///
/// 命名依据控件所在位置：
/// - 章节：最近的「第X章」标题；封面在第一章之前。
/// - 设备参数表：表题「表3-1 …」连同下面那张表算一个块，表前「此表适用于XX类」给出表的用途。
/// - 部件清单类的表（首行是 ≥5 列的纯文字表头）：每一数据行是一个块，块名取该行的位号与名称。
/// - 名称取「填写XX」占位文字、同段落里「标签：控件」的标签、同一行左侧的行标题、表头列名，依次兜底。
///
/// Tag 形如「t3_1.07」「part12.03」「ch1.02」：前缀是块，后面的序号在块内唯一。
/// 设备展开时按块整体复制（同一块的控件同前缀），后续步骤依赖这一约定。
/// 首页公司名称、装置名称这类贯穿全文的字段用固定 Tag（customer_name / device_name），
/// 全文各处共用一个槽位，填一次处处生效。
/// 已经设了 Tag 的控件原样不动；全部控件都有 Tag 的模板整份原样返回。</summary>
public static class TemplateNormalizer
{
    public record Result(byte[] Output, int Tagged, IReadOnlyList<string> Notes);

    /// <summary>与文档属性绑定的控件 → 全局槽位（Word 里「首页填一次、下面自动更新」靠的就是这个绑定）。</summary>
    private static readonly Dictionary<string, (string Tag, string Name)> BoundProps = new()
    {
        ["keywords"] = ("customer_name", "甲方单位名称"),
        ["title"] = ("device_name", "装置名称"),
        ["description"] = ("device_kind", "装置类型描述"),
        ["subject"] = ("doc_subject", "主题"),
        ["category"] = ("doc_category", "类别"),
    };

    /// <summary>没有绑定、但占位文字明说是全局字段的控件，与绑定控件共用槽位。</summary>
    private static readonly Dictionary<string, string> GlobalPlaceholders = new()
    {
        ["填写公司名称"] = "customer_name",
        ["填写甲方名称"] = "customer_name",
        ["填写客户名称"] = "customer_name",
        ["填写装置名称"] = "device_name",
        ["填写项目名称"] = "device_name",
    };

    /// <summary>不带信息的通用占位文字：从中取不出字段名。</summary>
    private static readonly Regex GenericPlaceholder = new(
        @"^(单击|点击|选择|请|在此|此处|输入|填写)?(此处|这里)?(输入|填写|选择)?(文字|内容|一项|或填写|/填写|填写)?[。.]?$",
        RegexOptions.Compiled);

    private static readonly Regex ChapterPattern = new(@"^第([一二三四五六七八九十]+)章\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex TableCaptionPattern = new(@"^表\s*(\d+)\s*[-－—]\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex TableKindPattern = new(@"此表适用于(.+?)类", RegexOptions.Compiled);

    /// <summary>标签尾巴上的单位（「工作压力Mpa：」）。长的写在前面，免得 MPaG 被 MPa 截断。</summary>
    private static readonly Regex TrailingUnit = new(
        @"\s*[（(]?(MPaG|MPa|Mpa|mpa|kPa|Pa|℃|°C|kg/m3|kg/m³|kg/h|m3/h|Nm3/h|r/min|rpm|N·m|N\.m|μm|um|mm|kW|kw|cP|cp|mPa·s|ml|mL|L|kg|%|m)[）)]?\s*$",
        RegexOptions.Compiled);

    private static readonly char[] SegmentBreaks = ['；', ';', '，', ',', '。', '、', '“', '”', '"', '（', '）', '(', ')', '\t'];

    public static Result Normalize(Stream docx)
    {
        using var ms = new MemoryStream();
        docx.CopyTo(ms);
        var original = ms.ToArray();
        var notes = new List<string>();
        int tagged;
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var body = doc.MainDocumentPart?.Document.Body
                ?? throw new InvalidDataException("不是有效的 Word 模板");
            if (!body.Descendants<SdtElement>().Any(NeedsTag))
                return new Result(original, 0, notes);

            var walker = new Walker();
            walker.Walk(body);
            tagged = walker.Tagged;
            doc.MainDocumentPart!.Document.Save();
        }
        notes.Insert(0, $"模板中有 {tagged} 处内容控件未设 Tag，已按所在章节与上下文自动命名，请在槽位列表里核对名称");
        return new Result(ms.ToArray(), tagged, notes);
    }

    /// <summary>需要补 Tag 的控件：没有 Tag、不是目录、不是只装着别的控件的外壳、不在重复节里。</summary>
    internal static bool NeedsTag(SdtElement sdt)
    {
        var props = sdt.SdtProperties;
        if (props is null) return false;
        var existing = props.GetFirstChild<Tag>()?.Val?.Value;
        // 绑定文档属性的控件归到全局槽位；模板作者在 Tag 里写的「自行修改」这类中文批注不是槽位标识
        if (BoundProperty(sdt) is not null && (string.IsNullOrWhiteSpace(existing) || existing.Any(c => c > 127)))
            return true;
        if (!string.IsNullOrWhiteSpace(existing)) return false;
        if (props.GetFirstChild<SdtContentDocPartObject>() is not null) return false;
        if (TemplateSlotExtractor.IsRepeatingSection(sdt)) return false;
        if (sdt.Ancestors<SdtElement>().Any(TemplateSlotExtractor.IsRepeatingSection)) return false;
        if (sdt.Descendants<SdtElement>().Any()) return false;
        return true;
    }

    private sealed class Block(string id, string section)
    {
        public string Id { get; } = id;
        public string Section { get; } = section;
        public int Next { get; set; } = 1;
        /// <summary>名称 → (Tag, 占位文字)：同名同占位的控件合并为一个槽位。</summary>
        public Dictionary<string, (string Tag, string Placeholder)> Names { get; } = new();
        /// <summary>同一段落里的一组勾选框共用一个槽位。</summary>
        public Dictionary<OpenXmlElement, string> CheckboxGroups { get; } = new();
    }

    private sealed class Walker
    {
        public int Tagged { get; private set; }
        private Block _chapter = new("cover", "封面");
        private readonly Dictionary<string, int> _blockUse = new();
        private string? _pendingKind;
        private Block? _caption;
        private int _partNo;

        public void Walk(OpenXmlElement container)
        {
            foreach (var el in container.Elements())
            {
                switch (el)
                {
                    case Paragraph p: OnParagraph(p); break;
                    case Table t: OnTable(t); break;
                    case SdtBlock sb:
                        if (sb.SdtProperties?.GetFirstChild<SdtContentDocPartObject>() is not null) break; // 目录
                        if (NeedsTag(sb)) TagOne(sb, _caption ?? _chapter);
                        else if (sb.SdtContentBlock is not null) Walk(sb.SdtContentBlock);
                        break;
                }
            }
        }

        private void OnParagraph(Paragraph p)
        {
            var text = p.InnerText.Trim();
            var ch = ChapterPattern.Match(text);
            if (ch.Success && !p.Descendants<SdtElement>().Any())
            {
                var n = ChineseNumber(ch.Groups[1].Value);
                _chapter = NewBlock($"ch{n}", $"第{ch.Groups[1].Value}章 {ch.Groups[2].Value.Trim()}".Trim());
                _caption = null;
                _pendingKind = null;
                return;
            }
            var kind = TableKindPattern.Match(text);
            if (kind.Success) _pendingKind = kind.Groups[1].Value.Trim();
            var cap = TableCaptionPattern.Match(text);
            if (cap.Success)
            {
                var a = cap.Groups[1].Value;
                var b = cap.Groups[2].Value;
                _caption = NewBlock($"t{a}_{b}", $"表{a}-{b} {_pendingKind ?? "设备参数表"}".Trim());
                _pendingKind = null;
            }
            foreach (var sdt in p.Descendants<SdtElement>().Where(NeedsTag).ToList())
                TagOne(sdt, _caption ?? _chapter);
        }

        private void OnTable(Table t)
        {
            var rows = t.Elements<TableRow>().ToList();
            if (rows.Count == 0) return;
            if (_caption is not null)
            {
                TagAll(t, _caption);
                _caption = null; // 表题只管紧跟着的这一张表
                return;
            }
            var header = rows[0].Elements<TableCell>().ToList();
            var isList = header.Count >= 5
                         && header.All(c => !c.Descendants<SdtElement>().Any() && c.InnerText.Trim().Length > 0);
            if (!isList)
            {
                TagAll(t, _chapter);
                return;
            }
            // 清单表：每一行是一个块（一种部件），块名取位号与名称
            var colPos = ColumnIndex(header, "位号");
            var colName = ColumnIndex(header, "名称");
            foreach (var row in rows.Skip(1))
            {
                if (!row.Descendants<SdtElement>().Any(NeedsTag)) continue;
                _partNo++;
                var cells = row.Elements<TableCell>().ToList();
                var pos = colPos >= 0 && colPos < cells.Count ? PlainText(cells[colPos]).Trim() : "";
                var name = colName >= 0 && colName < cells.Count ? PlainText(cells[colName]).Trim() : "";
                if (name.Length == 0) name = $"部件{_partNo}";
                var section = $"{Clean(_chapter.Section)} {_partNo:00} {name}{(pos.Length > 0 ? $"（{pos}）" : "")}";
                TagAll(row, NewBlock($"part{_partNo:00}", section));
            }
        }

        private void TagAll(OpenXmlElement scope, Block block)
        {
            foreach (var sdt in scope.Descendants<SdtElement>().Where(NeedsTag).ToList())
                TagOne(sdt, block);
        }

        private Block NewBlock(string id, string section)
        {
            // 同名块（模板里出现两个「表3-1」之类）加序号区分，保证 Tag 不撞
            var n = _blockUse.TryGetValue(id, out var used) ? used + 1 : 1;
            _blockUse[id] = n;
            return new Block(n == 1 ? id : $"{id}_{n}", Clean(section));
        }

        private void TagOne(SdtElement sdt, Block block)
        {
            var placeholder = Squash(SdtText(sdt));

            // 1) 全局字段：文档属性绑定，或占位文字点名的公司名称 / 装置名称
            var prop = BoundProperty(sdt);
            if (prop is not null && BoundProps.TryGetValue(prop, out var bound))
            {
                Apply(sdt, bound.Tag, "封面", bound.Name);
                return;
            }
            if (GlobalPlaceholders.TryGetValue(placeholder, out var globalTag))
            {
                var g = BoundProps.Values.First(v => v.Tag == globalTag);
                Apply(sdt, g.Tag, "封面", g.Name);
                return;
            }

            // 2) 勾选框：同一段落的一组框是一个单选槽位，选项就是各框后面的文字
            if (IsCheckbox(sdt))
            {
                var para = sdt.Ancestors<Paragraph>().FirstOrDefault() ?? (OpenXmlElement)sdt.Parent!;
                if (block.CheckboxGroups.TryGetValue(para, out var groupTag))
                {
                    Apply(sdt, groupTag, block.Section, null);
                    return;
                }
                var options = para.Descendants<SdtElement>().Where(IsCheckbox)
                    .Select(CheckboxLabel).Where(o => o.Length > 0).ToList();
                var ctx = TableContext.Of(sdt);
                // 「绝热材料：| ☐保温☐隔热…」有带冒号的行标题就用它；表头里的「☐夹套/☐盘管」用选项本身命名
                var label = ctx is { RowKeyHasColon: true } && ctx.RowKey.Length > 0 ? ctx.RowKey
                    : options.Count > 0 ? string.Join("或", options) : "勾选项";
                var tagNew = NewTag(block, label, "☐", out var nameNew);
                block.CheckboxGroups[para] = tagNew;
                Apply(sdt, tagNew, block.Section, nameNew);
                return;
            }

            // 3) 一般控件：按上下文取名
            var nameRaw = DeriveName(sdt, placeholder);
            var tag = NewTag(block, nameRaw, placeholder, out var name);
            Apply(sdt, tag, block.Section, name);
        }

        /// <summary>块内取 Tag：同名且占位文字相同、并且占位文字是「填写XX」这种有指向的，视为同一字段
        /// （表题里的「填写位号」与表头的「填写位号」是一回事）；否则同名加序号另起一个槽位。</summary>
        private static string NewTag(Block block, string name, string placeholder, out string finalName)
        {
            var specific = placeholder.StartsWith("填写") && !GenericPlaceholder.IsMatch(placeholder);
            if (block.Names.TryGetValue(name, out var hit) && specific && hit.Placeholder == placeholder)
            {
                finalName = name;
                return hit.Tag;
            }
            finalName = name;
            for (var i = 2; block.Names.ContainsKey(finalName); i++) finalName = $"{name}（{i}）";
            var tag = $"{block.Id}.{block.Next++:00}";
            block.Names[finalName] = (tag, placeholder);
            return tag;
        }

        private void Apply(SdtElement sdt, string tag, string section, string? name)
        {
            var props = sdt.SdtProperties!;
            props.RemoveAllChildren<Tag>();
            props.RemoveAllChildren<SdtAlias>();
            // 标题按第一个斜杠切「章节/名称」，所以章节里不能再有斜杠；名称里的斜杠（夹套/盘管）不受影响
            var safeSection = section.Replace('/', '、').Replace('／', '、');
            var alias = name is null ? null : new SdtAlias { Val = $"{safeSection}/{name}" };
            var tagEl = new Tag { Val = tag };
            var anchor = props.GetFirstChild<RunProperties>();
            if (anchor is not null)
            {
                if (alias is not null) anchor.InsertAfterSelf(alias);
                (alias ?? (OpenXmlElement)anchor).InsertAfterSelf(tagEl);
            }
            else
            {
                props.PrependChild(tagEl);
                if (alias is not null) props.PrependChild(alias);
            }
            Tagged++;
        }

        private static string DeriveName(SdtElement sdt, string placeholder)
        {
            var phLabel = PlaceholderLabel(placeholder);
            var inline = InlineLabel(sdt, out var group);
            var ctx = TableContext.Of(sdt);

            string label;
            if (ctx is null)
            {
                label = phLabel ?? inline ?? (placeholder.Length > 0 ? placeholder : "内容");
            }
            else if (ctx.InListRow && !ctx.CellHasColon)
            {
                // 清单表里位号、名称、品牌这类单值列：列名即字段名
                label = ctx.ColumnHeader.Length > 0 ? ctx.ColumnHeader : phLabel ?? inline ?? "内容";
            }
            else if (ctx.InListRow)
            {
                label = inline ?? phLabel ?? ctx.ColumnHeader;
            }
            else if (ctx.CellHasColon)
            {
                // 键值表里格子自带标签（「位号：____」「数量：____」）：左边那格不是它的标题
                label = inline ?? phLabel ?? ctx.CellKey ?? "内容";
            }
            else
            {
                // 键值表：行标题 + 段内标签（「下放料方式：| 内径____，____」）
                var key = ctx.RowKey;
                var inner = inline ?? phLabel;
                label = key.Length == 0 ? inner ?? "内容"
                    : inner is null || key.Contains(inner) ? key
                    : inner.Contains(key) ? inner
                    : $"{key}·{inner}";
                if (ctx.MultiValue && ctx.ColumnHeader.Length > 0 && group is null) group = ctx.ColumnHeader;
                if (ctx.RowUnit is not null && !label.Contains(ctx.RowUnit)) label = $"{label}（{ctx.RowUnit}）";
            }
            label = Clean(label);
            return group is null || label.StartsWith(group) ? label : $"{Clean(group)}·{label}";
        }

        /// <summary>「填写工作压力」→「工作压力」；通用占位（单击此处输入文字）取不出名字。</summary>
        private static string? PlaceholderLabel(string placeholder)
        {
            if (placeholder.Length == 0 || GenericPlaceholder.IsMatch(placeholder)) return null;
            var m = Regex.Match(placeholder, @"^(?:请)?(?:填写|输入|单击输入|选择)(.+?)[。.]?$");
            if (!m.Success) return null;
            var v = Clean(m.Groups[1].Value);
            return v.Length >= 2 ? v : null;
        }

        /// <summary>同一段落里紧挨控件的标签：「工作压力：____MPa」「装置采用____管阀件」。
        /// group 是段首的分组前缀（「内筒：工作压力____，工作温度____」里的「内筒」）。
        /// 紧挨着取不到时退到本分句（以分号断开）开头的「XX：」——「下放料方式：内径____，____」第二格就是「下放料方式」。</summary>
        private static string? InlineLabel(SdtElement sdt, out string? group)
        {
            group = null;
            if (sdt is not SdtRun) return null;
            var para = sdt.Ancestors<Paragraph>().FirstOrDefault();
            if (para is null) return null;
            var tokens = Tokens(para);
            var at = tokens.FindIndex(t => ReferenceEquals(t.Sdt, sdt));
            if (at < 0) return null;

            var before = new StringBuilder();
            for (var i = at - 1; i >= 0 && tokens[i].Sdt is null; i--) before.Insert(0, tokens[i].Text);
            var after = new StringBuilder();
            for (var i = at + 1; i < tokens.Count && tokens[i].Sdt is null; i++) after.Append(tokens[i].Text);

            // 段首分组：「内筒：」后面紧跟的还是文字标签（而不是控件），才算分组
            var full = string.Concat(tokens.Select(t => t.Sdt is null ? t.Text : "\u0001"));
            var gm = Regex.Match(full, @"^\s*([^\s：:；;，,\u0001]{1,4})[：:]([^\u0001：:；;，,]+)\u0001");
            if (gm.Success) group = gm.Groups[1].Value;

            // 本分句开头的「XX：」
            var upTo = string.Concat(tokens.Take(at).Select(t => t.Sdt is null ? t.Text : "\u0001"));
            var clauseStart = upTo.LastIndexOfAny(['；', ';', '。']);
            var clause = clauseStart >= 0 ? upTo[(clauseStart + 1)..] : upTo;
            if (group is not null && clause.TrimStart().StartsWith(group + "：")) clause = clause.TrimStart()[(group.Length + 1)..];
            var ck = Regex.Match(clause, @"^\s*([^：:\u0001]{2,16})[：:]");
            var clauseKey = ck.Success ? Clean(TrailingUnit.Replace(ck.Groups[1].Value, "")) : null;

            var pre = before.ToString();
            var cut = pre.LastIndexOfAny(SegmentBreaks);
            var preSeg = (cut >= 0 ? pre[(cut + 1)..] : pre).Trim();
            if (group is not null && preSeg.StartsWith(group)) preSeg = preSeg[group.Length..].TrimStart('：', ':');
            var colonStyle = preSeg.EndsWith('：') || preSeg.EndsWith(':');
            var innerColon = preSeg.TrimEnd('：', ':').LastIndexOfAny(['：', ':']);
            if (innerColon >= 0) { preSeg = preSeg[(innerColon + 1)..]; colonStyle = true; }
            var preLabel = Clean(LeadingUnit(preSeg));

            var post = after.ToString();
            var stop = post.IndexOfAny(SegmentBreaks);
            var postSeg = Clean(TrailingUnit.Replace(LeadingUnit(stop >= 0 ? post[..stop] : post), ""));

            if (colonStyle && preLabel.Length >= 2) return preLabel;
            if (preLabel.Length >= 2 && clauseKey is not null)
                return clauseKey.Contains(preLabel) ? clauseKey : $"{clauseKey}·{preLabel}";
            if (postSeg.Length >= 2 && !postSeg.All(char.IsAsciiLetterOrDigit)) return postSeg;
            if (preLabel.Length >= 2) return preLabel;
            return clauseKey is { Length: >= 2 } ? clauseKey : null;
        }

        private static string LeadingUnit(string s)
        {
            var m = Regex.Match(s.TrimStart(), @"^(MPaG|MPa|kPa|Pa|℃|°C|kg/m3|kg/h|m3/h|Nm3/h|r/min|N·m|μm|mm|kW|kw|cP|ml|mL|kg|%|m)(?![a-zA-Z])");
            return m.Success ? s.TrimStart()[m.Length..] : s;
        }
    }

    /// <summary>控件在表格里的位置信息：行标题、列名、是否清单行、该行是否多列取值。</summary>
    private sealed record TableContext(
        int RowIndex, string RowKey, bool RowKeyHasColon, string? RowUnit, string ColumnHeader,
        bool InListRow, bool CellHasColon, string? CellKey, bool MultiValue)
    {
        public static TableContext? Of(SdtElement sdt)
        {
            // 控件可能包着整个单元格（Word 的单元格级控件），这时单元格在控件里面
            var cell = sdt.Ancestors<TableCell>().FirstOrDefault() ?? sdt.Descendants<TableCell>().FirstOrDefault();
            var row = cell?.Ancestors<TableRow>().FirstOrDefault();
            var table = row?.Ancestors<Table>().FirstOrDefault();
            if (cell is null || row is null || table is null) return null;
            var rows = table.Elements<TableRow>().ToList();
            var rowIndex = rows.IndexOf(row);
            var header = Cells(rows[0]);
            var isList = header.Count >= 5 && header.All(c => !c.Descendants<SdtElement>().Any() && c.InnerText.Trim().Length > 0);

            // 列名：往上找最近一行「只有文字（勾选框可以有）」的表头行，取同一列那格的文字
            var col = GridStart(row, cell);
            var colHeader = "";
            for (var r = rowIndex - 1; r >= 0 && colHeader.Length == 0; r--)
            {
                if (rows[r].Descendants<SdtElement>().Any(s => !IsCheckbox(s))) continue; // 取值行（含单元格级控件）不是表头
                var h = Cells(rows[r]).FirstOrDefault(c => Covers(rows[r], c, col));
                if (h is not null) colHeader = Clean(PlainText(h).Replace("☐", ""));
                break;
            }

            // 行标题：同一行左侧最近的一格纯文字；本行左边是空的（纵向合并的续行）就往上找
            var (key, keyColon, unit) = FindRowKey(row, cell);
            for (var r = rowIndex - 1; key.Length == 0 && r > 0; r--)
            {
                var upperCells = Cells(rows[r]);
                var upper = upperCells.FirstOrDefault(c => GridStart(rows[r], c) >= col) ?? upperCells.LastOrDefault();
                if (upper is null) break;
                (key, keyColon, unit) = FindRowKey(rows[r], upper);
            }
            var valueCells = Cells(row).Count(c => c.Descendants<SdtElement>().Any() || c.Ancestors<SdtElement>().Any());
            var plain = PlainText(cell).Trim();
            var hasColon = plain.Contains('：') || plain.Contains(':');
            var cellKey = hasColon ? Clean(TrailingUnit.Replace(plain[..plain.IndexOfAny(['：', ':'])], "")) : null;
            return new TableContext(rowIndex, key, keyColon, unit, colHeader, isList && rowIndex > 0,
                hasColon, string.IsNullOrEmpty(cellKey) ? null : cellKey, valueCells >= 2);
        }

        /// <summary>一行的单元格，含被单元格级控件包着的格子。</summary>
        private static List<TableCell> Cells(TableRow row)
            => row.Descendants<TableCell>().Where(c => ReferenceEquals(c.Ancestors<TableRow>().First(), row)).ToList();

        private static (string Key, bool Colon, string? Unit) FindRowKey(TableRow row, TableCell cell)
        {
            var cells = Cells(row);
            for (var i = cells.IndexOf(cell) - 1; i >= 0; i--)
            {
                var t = PlainText(cells[i]).Trim();
                // 空格、序号、「（1）」这类编号不是标题
                if (t.Length == 0 || Regex.IsMatch(t, @"^[（(]?[\d.\s•]+[)）]?$")) continue;
                var colon = t.EndsWith('：') || t.EndsWith(':');
                var bare = t.TrimEnd('：', ':', ' ');
                var um = TrailingUnit.Match(bare);
                var unit = um.Success ? NormalizeUnit(um.Groups[1].Value) : null;
                return (Clean(TrailingUnit.Replace(bare, "")), colon, unit);
            }
            return ("", false, null);
        }

        private static string NormalizeUnit(string u) => u switch
        {
            "Mpa" or "mpa" => "MPa",
            "kw" => "kW",
            "cp" => "cP",
            _ => u
        };

        private static int GridStart(TableRow row, TableCell cell)
        {
            var col = row.TableRowProperties?.GetFirstChild<GridBefore>()?.Val?.Value ?? 0;
            foreach (var c in Cells(row))
            {
                if (ReferenceEquals(c, cell)) return col;
                col += c.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
            }
            return col;
        }

        private static bool Covers(TableRow row, TableCell cell, int col)
        {
            var start = GridStart(row, cell);
            var span = cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
            return col >= start && col < start + span;
        }
    }

    private record Token(string Text, SdtElement? Sdt);

    /// <summary>段落拆成「文字 / 控件」的顺序序列（控件内文字不算标签）。</summary>
    private static List<Token> Tokens(Paragraph p)
    {
        var list = new List<Token>();
        void Visit(OpenXmlElement el)
        {
            foreach (var c in el.ChildElements)
            {
                switch (c)
                {
                    case SdtElement s: list.Add(new Token("", s)); break;
                    case Run r: list.Add(new Token(string.Concat(r.Elements().Select(RunPiece)), null)); break;
                    default: Visit(c); break;
                }
            }
        }
        Visit(p);
        return list;
    }

    private static string RunPiece(OpenXmlElement e) => e switch
    {
        Text t => t.Text,
        TabChar => "\t",
        _ => ""
    };

    /// <summary>元素里不属于任何控件的文字。范围按所在表格算：包着整个单元格的控件，里面的字也是控件内容。</summary>
    private static string PlainText(OpenXmlElement el)
    {
        var scope = el.Ancestors<Table>().FirstOrDefault() ?? el;
        return string.Concat(el.Descendants<Text>()
            .Where(t => !t.Ancestors<SdtElement>().Any(a => ReferenceEquals(a, scope) || a.Ancestors().Contains(scope)))
            .Select(t => t.Text));
    }

    /// <summary>控件绑定的文档属性名（title / keywords / description…），没绑定为 null。</summary>
    private static string? BoundProperty(SdtElement sdt)
    {
        var xpath = sdt.SdtProperties?.GetFirstChild<DataBinding>()?.XPath?.Value;
        if (xpath is null) return null;
        var m = Regex.Match(xpath, @":(\w+)\[\d+\]\s*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string SdtText(SdtElement sdt) => string.Concat(sdt.Descendants<Text>().Select(t => t.Text));

    private static bool IsCheckbox(SdtElement sdt)
        => sdt.SdtProperties?.GetFirstChild<W14.SdtContentCheckBox>() is not null;

    /// <summary>勾选框后面紧跟的文字（到下一个控件或分隔符为止）就是这个选项的名字。</summary>
    internal static string CheckboxLabel(SdtElement box)
    {
        var para = box.Ancestors<Paragraph>().FirstOrDefault();
        if (para is null) return "";
        var tokens = Tokens(para);
        var at = tokens.FindIndex(t => ReferenceEquals(t.Sdt, box));
        var sb = new StringBuilder();
        for (var i = at + 1; i < tokens.Count && tokens[i].Sdt is null; i++) sb.Append(tokens[i].Text);
        var s = sb.ToString();
        var stop = s.IndexOfAny(['/', '／', '；', ';', '，', ',', '\t', '☐', '☑', '☒']);
        return Clean(stop >= 0 ? s[..stop] : s);
    }

    private static int ColumnIndex(List<TableCell> header, string name)
        => header.FindIndex(c => c.InnerText.Trim() == name);

    private static string Squash(string s) => Regex.Replace(s, @"\s+", "").Trim();

    /// <summary>标签清洗：去冒号、去首尾标点；斜杠换成顿号（标题里第一个斜杠是章节/名称的分隔符）。</summary>
    private static string Clean(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim().Trim('：', ':', '；', ';', '，', ',', '。', '、', '•', '“', '”', '"', ' ');
        return s;
    }

    private static int ChineseNumber(string s)
    {
        const string digits = "一二三四五六七八九";
        if (s == "十") return 10;
        if (s.StartsWith('十')) return 10 + digits.IndexOf(s[1]) + 1;
        if (s.EndsWith('十')) return (digits.IndexOf(s[0]) + 1) * 10;
        if (s.Contains('十')) return (digits.IndexOf(s[0]) + 1) * 10 + digits.IndexOf(s[^1]) + 1;
        return digits.IndexOf(s[0]) + 1;
    }
}
