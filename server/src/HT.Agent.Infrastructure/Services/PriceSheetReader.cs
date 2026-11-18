using DocumentFormat.OpenXml.Packaging;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace HT.Agent.Infrastructure.Services;

/// <summary>把 .xlsx 读成 <see cref="SheetGrid"/>：每个工作表的非空行（带 Excel 行号）和合并单元格。</summary>
public static class PriceSheetReader
{
    public static List<SheetGrid> Read(Stream xlsx, int maxRows = 20000)
    {
        using var ms = new MemoryStream();
        xlsx.CopyTo(ms);
        ms.Position = 0;
        SpreadsheetDocument doc;
        try { doc = SpreadsheetDocument.Open(ms, false); }
        catch (Exception ex) when (ex is DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or InvalidDataException or FileFormatException)
        {
            throw new DomainRuleException("PRICE_BAD_FILE", "读不了这个文件。只支持 .xlsx；老的 .xls 请用 Excel 另存为 .xlsx 再上传");
        }
        using (doc)
        {
            var wb = doc.WorkbookPart ?? throw new DomainRuleException("PRICE_BAD_FILE", "Excel 文件里没有工作簿");
            var sst = wb.SharedStringTablePart?.SharedStringTable;
            var list = new List<SheetGrid>();
            foreach (var sheet in wb.Workbook?.Sheets?.Elements<S.Sheet>() ?? [])
            {
                if (sheet.Id?.Value is not { } relId || wb.GetPartById(relId) is not WorksheetPart ws) continue;
                var rows = new List<SheetRow>();
                foreach (var row in ws.Worksheet.Descendants<S.Row>().Take(maxRows))
                {
                    var cells = new List<string>();
                    foreach (var c in row.Elements<S.Cell>())
                    {
                        var col = ColumnIndex(c.CellReference?.Value);
                        while (col > 0 && cells.Count < col - 1) cells.Add("");
                        cells.Add(Text(c, sst).Replace('\n', ' ').Trim());
                    }
                    if (cells.Any(v => v.Length > 0)) rows.Add(new SheetRow((int)(row.RowIndex?.Value ?? (uint)(rows.Count + 1)), cells.ToArray()));
                }
                var merges = new List<CellRange>();
                foreach (var m in ws.Worksheet.Descendants<S.MergeCell>())
                {
                    var parts = (m.Reference?.Value ?? "").Split(':');
                    if (parts.Length != 2) continue;
                    var (r1, c1) = Ref(parts[0]);
                    var (r2, c2) = Ref(parts[1]);
                    if (r1 > 0 && r2 >= r1 && c2 >= c1) merges.Add(new CellRange(r1, c1, r2, c2));
                }
                list.Add(new SheetGrid(sheet.Name?.Value ?? $"Sheet{list.Count + 1}", rows, merges));
            }
            return list;
        }
    }

    private static string Text(S.Cell c, S.SharedStringTable? sst)
    {
        var raw = c.CellValue?.InnerText ?? c.InlineString?.InnerText ?? "";
        if (c.DataType?.Value == S.CellValues.SharedString && sst is not null && int.TryParse(raw, out var i) && i >= 0 && i < sst.ChildElements.Count)
            return sst.ElementAt(i).InnerText;
        if (c.DataType?.Value == S.CellValues.Boolean) return raw == "1" ? "TRUE" : "FALSE";
        // 数字：Excel 存的是 23.0973451327434 这种，原样保留，入库时再按需要取舍
        return raw;
    }

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

    /// <summary>「G116」→ (行 116, 列 6)。</summary>
    private static (int Row, int Col) Ref(string r)
    {
        var col = ColumnIndex(r) - 1;
        var digits = new string(r.SkipWhile(char.IsLetter).ToArray());
        return (int.TryParse(digits, out var row) ? row : 0, Math.Max(0, col));
    }
}
