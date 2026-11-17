using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HT.Agent.Application.Abstractions;

namespace HT.Agent.Infrastructure.Templates;

/// <summary>按设备清单改写 Word：每台设备复制一份它的块，没选的块删掉，回填之前做。
/// - 设备参数表块 = 表题段落 + 紧跟的表格；表前「此表适用于…」是给填表人看的说明，一并去掉。
///   复制出来的表按顺序重编表号（表3-1、3-2…）。
/// - 部件块 = 清单里的一行；位号、数量两列是固定文字，按清单改写。
///   配套行正文里「详见…表3-1」改成它那张表重编后的表号。
/// 复制件里的控件 Tag 改成「块#设备Id.序号」，与 <see cref="EquipmentBlocks.Expand"/> 展开的槽位一一对应。
/// 文档里没有块（不是按设备展开的模板）时原样返回。</summary>
public static class EquipmentDocExpander
{
    private static readonly Regex TableRef = new(@"表\s*(\d+)\s*[-－—]\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex Instruction = new(@"^此表适用于", RegexOptions.Compiled);

    public static byte[] Expand(byte[] docx, IReadOnlyList<EquipmentItem> items)
    {
        using var ms = new MemoryStream();
        ms.Write(docx);
        ms.Position = 0;
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            var blocks = BlockOrder(body);
            if (blocks.Count == 0) return docx;

            var tableNo = new Dictionary<string, string>();   // 设备Id → 重编后的表号
            var chapter = "3";
            var n = 0;
            var rowsToFix = new List<(TableRow Row, EquipmentItem Item)>();

            foreach (var block in blocks)
            {
                var chosen = items.Where(i => i.Block == block && i.Id is not null).ToList();
                if (EquipmentBlocks.IsTable(block))
                {
                    var region = TableRegion(body, block);
                    if (region.Count == 0) continue;
                    var caption = region.OfType<Paragraph>().FirstOrDefault();
                    if (caption is not null && TableRef.Match(caption.InnerText) is { Success: true } cm)
                        chapter = cm.Groups[1].Value;
                    foreach (var note in Instructions(region[0])) note.Remove();
                    OpenXmlElement anchor = region[^1];
                    foreach (var item in chosen)
                    {
                        var no = $"表{chapter}-{++n}";
                        tableNo[item.Id!] = no;
                        foreach (var el in region)
                        {
                            var copy = Retag(el.CloneNode(true), block, item.Id!);
                            if (copy is Paragraph p) ReplaceTableRef(p, no);
                            anchor.InsertAfterSelf(copy);
                            anchor = copy;
                        }
                    }
                    foreach (var el in region) el.Remove();
                }
                else
                {
                    var row = body.Descendants<TableRow>().FirstOrDefault(r => OwnTags(r).Any(t => InBlock(t, block)));
                    if (row is null) continue;
                    OpenXmlElement anchor = row;
                    foreach (var item in chosen)
                    {
                        var copy = (TableRow)Retag(row.CloneNode(true), block, item.Id!);
                        anchor.InsertAfterSelf(copy);
                        anchor = copy;
                        rowsToFix.Add((copy, item));
                    }
                    row.Remove();
                }
            }

            foreach (var (row, item) in rowsToFix)
            {
                SetColumn(row, "位号", item.TagNo);
                SetColumn(row, "数量", item.Qty.ToString());
                TrimNameSuffix(row, item.Name);
                if (item.LinkedTo is not null && tableNo.TryGetValue(item.LinkedTo, out var no))
                    foreach (var p in row.Descendants<Paragraph>()) ReplaceTableRef(p, no);
            }
            RenumberPermissions(body);
            doc.MainDocumentPart.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>模板里控件外围常有「所有人可编辑」区段（permStart/permEnd），id 在文档里应唯一。
    /// 复制件带着原 id，这里按文档顺序给重复的起止对换新 id。</summary>
    private static void RenumberPermissions(Body body)
    {
        var starts = body.Descendants<PermStart>().ToList();
        if (starts.Count == 0) return;
        var next = starts.Select(p => p.Id?.Value ?? 0).Concat(body.Descendants<PermEnd>().Select(p => p.Id?.Value ?? 0)).Max() + 1;
        var seen = new HashSet<int>();
        var pending = new Dictionary<int, Queue<int>>();   // 原 id → 已换新 id、还没遇到结束标记的
        foreach (var el in body.Descendants())
        {
            if (el is PermStart ps && ps.Id?.Value is { } sid)
            {
                if (seen.Add(sid)) continue;
                var fresh = next++;
                ps.Id = fresh;
                if (!pending.TryGetValue(sid, out var q)) pending[sid] = q = new Queue<int>();
                q.Enqueue(fresh);
            }
            else if (el is PermEnd pe && pe.Id?.Value is { } eid && pending.TryGetValue(eid, out var q) && q.Count > 0)
            {
                // 原件的结束标记在前（原件已删除，剩下的都是复制件）：先到的起点配先到的终点
                pe.Id = q.Dequeue();
            }
        }
    }

    /// <summary>部件行的正文（不含控件里的占位字），给目录认「详见…表3-1」用。</summary>
    public static Dictionary<string, string> RowTexts(byte[] docx)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        var body = doc.MainDocumentPart?.Document.Body;
        var map = new Dictionary<string, string>();
        if (body is null) return map;
        foreach (var row in body.Descendants<TableRow>())
        {
            var block = OwnTags(row).Select(EquipmentBlocks.Parse).FirstOrDefault(p => p is { Instance: null } && !EquipmentBlocks.IsTable(p.Value.Block));
            if (block is null || map.ContainsKey(block.Value.Block)) continue;
            // 格与格之间隔开：「详见表3-1」后面紧跟数量列的「1」，连起来就成了「表3-11」
            map[block.Value.Block] = string.Join(" | ", row.Elements<TableCell>().Select(c => string.Concat(
                c.Descendants<Text>().Where(t => !t.Ancestors<SdtElement>().Any()).Select(t => t.Text))));
        }
        return map;
    }

    /// <summary>文档里出现的块，按出现顺序。</summary>
    private static List<string> BlockOrder(Body body)
        => body.Descendants<SdtElement>().Select(TagOf).Where(t => t is not null)
            .Select(t => EquipmentBlocks.Parse(t!)).Where(p => p is { Instance: null })
            .Select(p => p!.Value.Block).Distinct().ToList();

    /// <summary>设备参数表块：顶层里含该块控件的段落（表题）与表格，按文档顺序。</summary>
    private static List<OpenXmlElement> TableRegion(Body body, string block)
        => body.Elements().Where(e => e is Paragraph or Table && OwnTags(e).Any(t => InBlock(t, block))).ToList();

    /// <summary>块前面紧挨着的「此表适用于…」说明段（中间隔着空段也算）。</summary>
    private static List<Paragraph> Instructions(OpenXmlElement first)
    {
        var found = new List<Paragraph>();
        for (var prev = first.PreviousSibling(); prev is Paragraph p; prev = p.PreviousSibling())
        {
            var text = p.InnerText.Trim();
            if (Instruction.IsMatch(text)) { found.Add(p); continue; }
            if (text.Length > 0 || p.Descendants<SdtElement>().Any()) break;
        }
        return found;
    }

    private static IEnumerable<string> OwnTags(OpenXmlElement el)
        => el.Descendants<SdtElement>().Select(TagOf).Where(t => t is not null).Select(t => t!);

    private static string? TagOf(SdtElement sdt) => sdt.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value;

    private static bool InBlock(string tag, string block)
        => EquipmentBlocks.Parse(tag) is { Instance: null } p && p.Block == block;

    private const string W14Ns = "http://schemas.microsoft.com/office/word/2010/wordml";

    private static OpenXmlElement Retag(OpenXmlElement el, string block, string id)
    {
        // 段落/行的 w14:paraId、textId 在文档里应唯一：复制件去掉，Word 打开时重新生成
        foreach (var node in el.Descendants().Prepend(el))
            foreach (var attr in node.GetAttributes().Where(a => a.NamespaceUri == W14Ns && a.LocalName is "paraId" or "textId").ToList())
                node.RemoveAttribute(attr.LocalName, attr.NamespaceUri);
        foreach (var sdt in el.Descendants<SdtElement>())
        {
            var tag = sdt.SdtProperties?.GetFirstChild<Tag>();
            if (tag?.Val?.Value is not { } v || EquipmentBlocks.Parse(v) is not { Instance: null } p || p.Block != block) continue;
            tag.Val = EquipmentBlocks.InstanceTag(block, id, p.Local);
            // 控件 id 在文档里应唯一：复制件清掉，Word 打开时自己补
            sdt.SdtProperties!.RemoveAllChildren<SdtId>();
        }
        return el;
    }

    /// <summary>段落里控件之外的「表3-x」改成新表号（表题、配套行的「详见…表3-1」）。
    /// 「表3-」和「3」常落在两个 run 里，所以按整段文字找，再写回各 run：
    /// 新表号放进匹配起点所在的 run，匹配跨到的其余 run 去掉被覆盖的那截。</summary>
    private static void ReplaceTableRef(Paragraph p, string no)
    {
        var texts = p.Descendants<Text>().Where(t => !t.Ancestors<SdtElement>().Any()).ToList();
        var full = string.Concat(texts.Select(t => t.Text));
        var matches = TableRef.Matches(full).Cast<Match>().Reverse().ToList();
        foreach (var m in matches)
        {
            int pos = 0, start = m.Index, end = m.Index + m.Length;
            foreach (var t in texts)
            {
                var a = pos; var b = pos + t.Text.Length;
                pos = b;
                if (b <= start || a >= end) continue;
                var from = Math.Max(start, a) - a;
                var to = Math.Min(end, b) - a;
                t.Text = t.Text[..from] + (start >= a && start < b ? no : "") + t.Text[to..];
                t.Space = SpaceProcessingModeValues.Preserve;
            }
        }
    }

    /// <summary>名称列常写成「____反应釜」：控件后面跟着固定的类别字。
    /// 清单上的名称已经带了这个字（「聚合反应釜」）就去掉固定字，免得出「聚合反应釜反应釜」。</summary>
    private static void TrimNameSuffix(TableRow row, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var table = row.Ancestors<Table>().FirstOrDefault();
        var head = table?.Elements<TableRow>().FirstOrDefault()?.Elements<TableCell>().ToList();
        var col = head?.FindIndex(c => c.InnerText.Trim() == "名称") ?? -1;
        var cells = row.Elements<TableCell>().ToList();
        if (col < 0 || col >= cells.Count || !cells[col].Descendants<SdtElement>().Any()) return;
        var fixedTexts = cells[col].Descendants<Text>().Where(t => !t.Ancestors<SdtElement>().Any()).ToList();
        var suffix = string.Concat(fixedTexts.Select(t => t.Text)).Trim();
        if (suffix.Length == 0 || !name.Trim().EndsWith(suffix)) return;
        foreach (var t in fixedTexts) t.Text = string.Empty;
    }

    /// <summary>按表头列名改写这一行某列的文字（位号、数量是版面固定文字，不是控件）。
    /// 那一格里有控件就不动——控件归槽位管。</summary>
    private static void SetColumn(TableRow row, string header, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var table = row.Ancestors<Table>().FirstOrDefault();
        var head = table?.Elements<TableRow>().FirstOrDefault()?.Elements<TableCell>().ToList();
        var col = head?.FindIndex(c => c.InnerText.Trim() == header) ?? -1;
        var cells = row.Elements<TableCell>().ToList();
        if (col < 0 || col >= cells.Count) return;
        var cell = cells[col];
        if (cell.Descendants<SdtElement>().Any()) return;
        var texts = cell.Descendants<Text>().ToList();
        if (texts.Count > 0)
        {
            texts[0].Text = value;
            texts[0].Space = SpaceProcessingModeValues.Preserve;
            foreach (var t in texts.Skip(1)) t.Text = string.Empty;
            return;
        }
        var para = cell.Elements<Paragraph>().FirstOrDefault() ?? cell.AppendChild(new Paragraph());
        para.AppendChild(new Run(new Text(value) { Space = SpaceProcessingModeValues.Preserve }));
    }
}
