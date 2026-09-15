using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Templates;

/// <summary>Word 槽位回填（FR-5.15）：按 Tag 定位内容控件写入取值，版式由模板承载不另起炉灶；
/// 页眉标注待复核；后续阶段与留空槽位清空占位文字输出空白（表 5-2 留空处理的默认档）。</summary>
public static class DocxSlotFiller
{
    public record FillInput(string Tag, string? Value, SlotDataType DataType);

    public record FillResult(byte[] Output, int Filled, int LeftBlank, IReadOnlyList<string> Warnings);

    public static async Task<FillResult> FillAsync(Stream template, IReadOnlyList<FillInput> inputs, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await template.CopyToAsync(ms, ct);
        ms.Position = 0;

        int filled = 0, blank = 0;
        var warnings = new List<string>();
        var byTag = inputs.ToDictionary(i => i.Tag);

        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var main = doc.MainDocumentPart!;
            foreach (var sdt in main.Document.Body!.Descendants<SdtElement>().ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (sdt.Ancestors<SdtElement>().Any(TemplateSlotExtractor.IsRepeatingSection)) continue;
                var tag = sdt.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value;
                if (tag is null || !byTag.TryGetValue(tag, out var input)) continue;

                if (string.IsNullOrWhiteSpace(input.Value))
                {
                    SetText(sdt, string.Empty); // 留空：清掉占位文字，不把「请填写…」印进正式文件
                    blank++;
                    continue;
                }
                if (input.DataType == SlotDataType.RepeatingRows && TemplateSlotExtractor.IsRepeatingSection(sdt))
                {
                    if (FillRepeating(sdt, input.Value, warnings)) filled++;
                    else blank++;
                    continue;
                }
                SetText(sdt, input.Value);
                filled++;
            }

            StampPendingReview(main); // 页眉待复核（FR-5.15）
            main.Document.Save();
            foreach (var hp in main.HeaderParts) hp.Header.Save();
        }
        return new FillResult(ms.ToArray(), filled, blank, warnings);
    }

    /// <summary>重复行（表 5-2）：控件内第一个重复项为行模板，按数据条数复制，子控件按子字段 Tag 填。</summary>
    private static bool FillRepeating(SdtElement sdt, string json, List<string> warnings)
    {
        List<Dictionary<string, string>>? rows;
        try { rows = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json); }
        catch (JsonException) { rows = null; }
        if (rows is null)
        {
            warnings.Add("重复行槽位的取值不是行数组 JSON，已留空");
            return false;
        }
        var content = sdt.ChildElements.FirstOrDefault(e => e.LocalName == "sdtContent");
        var rowTemplate = content?.ChildElements.FirstOrDefault(e => e.LocalName == "sdtContent" || e.LocalName == "sdt")
                          ?? content?.FirstChild;
        if (content is null || rowTemplate is null)
        {
            warnings.Add("重复节控件缺少行模板，已留空");
            return false;
        }
        var templateClone = rowTemplate.CloneNode(true);
        content.RemoveAllChildren();
        foreach (var row in rows)
        {
            var item = templateClone.CloneNode(true);
            foreach (var child in item.Descendants<SdtElement>())
            {
                var subTag = child.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value;
                SetText(child, subTag is not null && row.TryGetValue(subTag, out var v) ? v : string.Empty);
            }
            content.AppendChild(item);
        }
        return true;
    }

    /// <summary>控件文本替换：保留首个文本 run 的格式（版式来自模板，FR-5.15），多余文本清空；
    /// 无文本节点时在内容区补一个 run。多行值以换行拆成多段落文本。</summary>
    private static void SetText(OpenXmlElement sdt, string value)
    {
        var texts = sdt.Descendants<Text>().ToList();
        var lines = value.Replace("\r\n", "\n").Split('\n');
        if (texts.Count > 0)
        {
            texts[0].Text = lines[0];
            texts[0].Space = SpaceProcessingModeValues.Preserve;
            foreach (var t in texts.Skip(1)) t.Text = string.Empty;
            if (lines.Length > 1)
            {
                // 多行：在首 run 后追加 break+text，长段落的分段以软换行呈现
                var run = texts[0].Ancestors<Run>().FirstOrDefault();
                if (run is not null)
                    foreach (var line in lines.Skip(1))
                    {
                        run.AppendChild(new Break());
                        run.AppendChild(new Text(line) { Space = SpaceProcessingModeValues.Preserve });
                    }
            }
            return;
        }
        var content = sdt.ChildElements.FirstOrDefault(e => e.LocalName == "sdtContent");
        if (content is null) return;
        var para = content.Descendants<Paragraph>().FirstOrDefault();
        var newRun = new Run(new Text(value) { Space = SpaceProcessingModeValues.Preserve });
        if (para is not null) para.AppendChild(newRun);
        else content.AppendChild(new Paragraph(newRun));
    }

    /// <summary>页眉标注「待复核」（FR-5.15）。无页眉的模板补建默认页眉并挂到节属性。</summary>
    private static void StampPendingReview(MainDocumentPart main)
    {
        const string stamp = "【待复核】本文件由系统按模板生成，未经人工复核不得对外使用。";
        if (main.HeaderParts.Any())
        {
            foreach (var hp in main.HeaderParts)
                hp.Header.InsertAt(StampParagraph(stamp), 0);
            return;
        }
        var headerPart = main.AddNewPart<HeaderPart>();
        headerPart.Header = new Header(StampParagraph(stamp));
        headerPart.Header.Save();
        var headerId = main.GetIdOfPart(headerPart);
        var body = main.Document.Body!;
        var sectPr = body.Elements<SectionProperties>().LastOrDefault();
        if (sectPr is null)
        {
            sectPr = new SectionProperties();
            body.AppendChild(sectPr);
        }
        sectPr.InsertAt(new HeaderReference { Type = HeaderFooterValues.Default, Id = headerId }, 0);
    }

    private static Paragraph StampParagraph(string text)
        => new(new ParagraphProperties(new Justification { Val = JustificationValues.Right }),
            new Run(new RunProperties(new Color { Val = "B3261E" }),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
}
