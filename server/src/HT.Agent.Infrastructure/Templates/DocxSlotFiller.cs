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
            // 模板用了 Word 占位状态时，没处于占位状态的控件里是写好的默认内容（「西门子」「DC24V」），留空不清
            var usesPlaceholders = TemplateSlotExtractor.UsesPlaceholderState(main.Document.Body!);
            var boundValues = new Dictionary<string, string>();
            foreach (var sdt in main.Document.Body!.Descendants<SdtElement>().ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (sdt.Ancestors<SdtElement>().Any(TemplateSlotExtractor.IsRepeatingSection)) continue;
                var tag = sdt.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value;
                if (tag is null || !byTag.TryGetValue(tag, out var input)) continue;
                var isCheckbox = TemplateSlotExtractor.IsCheckbox(sdt);

                if (string.IsNullOrWhiteSpace(input.Value))
                {
                    var hasDefault = usesPlaceholders && !TemplateSlotExtractor.ShowsPlaceholder(sdt);
                    if (!isCheckbox && !hasDefault)
                    {
                        SetText(sdt, string.Empty); // 留空：清掉占位文字，不把「请填写…」印进正式文件
                        ClearPlaceholderState(sdt);
                    }
                    blank++;
                    continue;
                }
                if (input.DataType == SlotDataType.RepeatingRows && TemplateSlotExtractor.IsRepeatingSection(sdt))
                {
                    if (FillRepeating(sdt, input.Value, warnings)) filled++;
                    else blank++;
                    continue;
                }
                if (isCheckbox)
                {
                    SetCheckbox(sdt, input.Value);
                    filled++;
                    continue;
                }
                SetText(sdt, input.Value);
                ClearPlaceholderState(sdt);
                if (BoundCoreProperty(sdt) is { } prop) boundValues[prop] = input.Value;
                filled++;
            }

            // 绑定文档属性的控件（首页公司名称、装置名称）：Word 打开时按属性刷新控件，属性不改就会被刷回空白
            foreach (var (prop, value) in boundValues) SetCoreProperty(doc, prop, value);
            if (HasTableOfContents(main)) RequestFieldUpdate(main); // 目录页码随内容变化，打开时让 Word 更新

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

    /// <summary>退出 Word 占位状态：去掉 showingPlcHdr 标记和占位文字的灰色，
    /// 填进去的值按控件设定的样式显示，不再像提示文字那样发灰、点一下就被选中清空。</summary>
    private static void ClearPlaceholderState(SdtElement sdt)
    {
        var props = sdt.SdtProperties;
        if (props?.GetFirstChild<ShowingPlaceholder>() is null) return;
        props.RemoveAllChildren<ShowingPlaceholder>();
        var style = props.GetFirstChild<RunProperties>()?.GetFirstChild<RunStyle>();
        foreach (var run in sdt.Descendants<Run>())
        {
            var rPr = run.RunProperties;
            if (rPr?.Color?.Val?.Value is "808080") rPr.Color.Remove();
            if (style is not null && rPr?.RunStyle is null)
            {
                rPr ??= run.PrependChild(new RunProperties());
                rPr.PrependChild((RunStyle)style.CloneNode(true));
            }
        }
    }

    /// <summary>勾选框：一组框（☐保温☐隔热☐保冷☐无）按框后的文字对上取值的那个打勾；
    /// 单个框按「是/否」。勾选符号与字体取控件里设定的 checkedState / uncheckedState。</summary>
    private static void SetCheckbox(SdtElement sdt, string value)
    {
        var box = sdt.SdtProperties!.GetFirstChild<DocumentFormat.OpenXml.Office2010.Word.SdtContentCheckBox>()!;
        var label = TemplateNormalizer.CheckboxLabel(sdt);
        var v = value.Trim();
        var on = v is "是" or "勾选" or "有" or "true" or "True" or "1"
            || (label.Length > 0 && (v == label || v.Split(['、', ',', '，', '/', ' '], StringSplitOptions.RemoveEmptyEntries).Contains(label)));

        var checkedEl = box.GetFirstChild<DocumentFormat.OpenXml.Office2010.Word.Checked>();
        if (checkedEl is null)
        {
            checkedEl = new DocumentFormat.OpenXml.Office2010.Word.Checked();
            box.PrependChild(checkedEl);
        }
        checkedEl.Val = on ? DocumentFormat.OpenXml.Office2010.Word.OnOffValues.One : DocumentFormat.OpenXml.Office2010.Word.OnOffValues.Zero;

        string? hex = on
            ? box.GetFirstChild<DocumentFormat.OpenXml.Office2010.Word.CheckedState>()?.Val?.Value
            : box.GetFirstChild<DocumentFormat.OpenXml.Office2010.Word.UncheckedState>()?.Val?.Value;
        string? font = on
            ? box.GetFirstChild<DocumentFormat.OpenXml.Office2010.Word.CheckedState>()?.Font?.Value
            : box.GetFirstChild<DocumentFormat.OpenXml.Office2010.Word.UncheckedState>()?.Font?.Value;
        var symbol = hex is not null && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code)
            ? char.ConvertFromUtf32(code)
            : on ? "☒" : "☐";
        var text = sdt.Descendants<Text>().FirstOrDefault();
        if (text is null) return;
        text.Text = symbol;
        foreach (var extra in sdt.Descendants<Text>().Skip(1)) extra.Text = string.Empty;
        if (font is null) return;
        var run = text.Ancestors<Run>().FirstOrDefault();
        if (run is null) return;
        var rPr = run.RunProperties ?? run.PrependChild(new RunProperties());
        var fonts = rPr.RunFonts ?? rPr.PrependChild(new RunFonts());
        fonts.Ascii = font;
        fonts.HighAnsi = font;
    }

    /// <summary>控件绑定的核心文档属性（dc:title / cp:keywords …）。</summary>
    private static string? BoundCoreProperty(SdtElement sdt)
    {
        var binding = sdt.SdtProperties?.GetFirstChild<DataBinding>();
        var xpath = binding?.XPath?.Value;
        if (xpath is null || !xpath.Contains("coreProperties")) return null;
        var m = System.Text.RegularExpressions.Regex.Match(xpath, @":(\w+)\[\d+\]\s*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static void SetCoreProperty(WordprocessingDocument doc, string prop, string value)
    {
        var p = doc.PackageProperties;
        switch (prop)
        {
            case "title": p.Title = value; break;
            case "subject": p.Subject = value; break;
            case "creator": p.Creator = value; break;
            case "keywords": p.Keywords = value; break;
            case "description": p.Description = value; break;
            case "category": p.Category = value; break;
            case "contentStatus": p.ContentStatus = value; break;
        }
    }

    private static bool HasTableOfContents(MainDocumentPart main)
        => main.Document.Body!.Descendants<FieldCode>().Any(f => f.Text.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase))
           || main.Document.Body!.Descendants<SimpleField>().Any(f => f.Instruction?.Value?.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>设置「打开时更新域」，目录页码由 Word 重新计算。</summary>
    private static void RequestFieldUpdate(MainDocumentPart main)
    {
        var settingsPart = main.DocumentSettingsPart ?? main.AddNewPart<DocumentSettingsPart>();
        settingsPart.Settings ??= new Settings();
        var settings = settingsPart.Settings;
        var update = settings.GetFirstChild<UpdateFieldsOnOpen>();
        if (update is null)
        {
            update = new UpdateFieldsOnOpen();
            settings.AddChild(update); // AddChild 按 schema 顺序插入，Word 对 settings 的元素顺序很挑剔
        }
        update.Val = true;
        settings.Save();
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
