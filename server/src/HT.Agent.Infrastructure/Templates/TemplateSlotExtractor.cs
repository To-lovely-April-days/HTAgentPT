using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Templates;

/// <summary>模板槽位抽取（FR-5.3、5.2.4）：扫描 Word 内容控件（SDT），控件 Tag 即槽位标识，
/// 控件内占位文字自动抽取为提问话术初值——模板作者不发明标记语法、不逐项录入。
/// 控件标题（别名）抽为显示名称；写成「章节/名称」还能一并带出所属章节，
/// 让模板上传后即为完整定义、直接可启用。</summary>
public static class TemplateSlotExtractor
{
    /// <param name="Required">模板里已经写好内容的控件（不是灰色占位状态）带着默认值，留空时保留原文，
    /// 不强制必填；只在模板用了占位状态时这样区分，老模板一律必填。</param>
    public record ExtractedSlot(string Tag, string? Name, string? Section, string? Prompt,
        SlotDataType DataType, string? Choices, bool ForbidInherit, string? SubFields, int SortOrder,
        string? Unit = null, bool Required = true);

    public record ExtractResult(IReadOnlyList<ExtractedSlot> Slots, IReadOnlyList<string> Warnings);

    /// <summary>候选项按原文存（中文、英寸号不转义成 \uXXXX），管理界面里看得懂、改得动。</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>默认禁止继承的槽位名（FR-5.7）：客户名称、项目编号、报价金额、日期、联系人。
    /// 按 Tag 与占位文字联合判定，管理员可在界面改。</summary>
    private static readonly string[] ForbidHints =
        ["customer", "client", "project_no", "contract_no", "amount", "price", "date", "contact",
         "客户", "项目编号", "合同编号", "金额", "报价", "日期", "联系"];

    public static ExtractResult Extract(Stream docx)
    {
        using var ms = new MemoryStream();
        docx.CopyTo(ms);
        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var main = doc.MainDocumentPart
            ?? throw new InvalidDataException("不是有效的 Word 模板");

        var slots = new List<ExtractedSlot>();
        var warnings = new List<string>();
        var seen = new Dictionary<string, int>();
        var order = 0;
        var usesPlaceholders = UsesPlaceholderState(main.Document.Body!);
        // 同一 Tag 的一组勾选框合成一个单选槽位，选项是各框后面的文字
        var checkboxGroups = main.Document.Body!.Descendants<SdtElement>()
            .Where(IsCheckbox)
            .GroupBy(s => s.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value ?? "")
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var sdt in main.Document.Body!.Descendants<SdtElement>())
        {
            // 重复节内的子控件由父级的 SubFields 声明，不单列为槽位
            if (sdt.Ancestors<SdtElement>().Any(a => IsRepeatingSection(a))) continue;

            var props = sdt.SdtProperties;
            var tag = props?.GetFirstChild<Tag>()?.Val?.Value;
            var placeholder = InnerText(sdt);
            if (string.IsNullOrWhiteSpace(tag))
            {
                // 目录、以及只是把几处控件包在一起的外壳，本来就不是槽位
                if (props?.GetFirstChild<SdtContentDocPartObject>() is not null) continue;
                if (sdt.Descendants<SdtElement>().Any()) continue;
                warnings.Add($"发现无 Tag 的内容控件（占位文字「{Truncate(placeholder)}」），已跳过——控件 Tag 是槽位标识，模板里必须设置");
                continue;
            }
            if (seen.TryGetValue(tag, out var first))
            {
                // 同一字段在文中多处出现（公司名称、装置名称）：回填时各处一起填，这里只列一个槽位。
                // 类型对不上才提醒——那多半是模板里 Tag 抄错了
                var (dupType, _, _) = DetectType(sdt, [], checkboxGroups);
                if (IsTextual(dupType) && IsTextual(slots[first].DataType))
                {
                    // 同一字段既有整段控件也有行内控件：按行内文本问，填进整段也没问题
                    if (dupType == SlotDataType.Text) slots[first] = slots[first] with { DataType = SlotDataType.Text };
                    continue;
                }
                if (dupType != slots[first].DataType)
                    warnings.Add($"Tag「{tag}」在多处出现且控件类型不同，按第一处的类型处理");
                continue;
            }

            var (dataType, choices, subFields) = DetectType(sdt, warnings, checkboxGroups);
            var (name, section) = ParseAlias(props?.GetFirstChild<SdtAlias>()?.Val?.Value);
            var unit = UnitAfter(sdt);
            var hasDefault = usesPlaceholders && !IsCheckbox(sdt) && !ShowsPlaceholder(sdt)
                             && !string.IsNullOrWhiteSpace(placeholder);
            seen[tag] = slots.Count;
            slots.Add(new ExtractedSlot(
                tag, name, section,
                string.IsNullOrWhiteSpace(placeholder) ? null : placeholder.Trim(),
                dataType, choices,
                ForbidHints.Any(h => tag.Contains(h, StringComparison.OrdinalIgnoreCase) ||
                                     (placeholder?.Contains(h) ?? false) ||
                                     (name?.Contains(h) ?? false)),
                subFields, order++, unit,
                Required: !hasDefault && !(usesPlaceholders && IsCheckbox(sdt))));
        }
        if (slots.Count == 0)
            warnings.Add("模板中没有任何带 Tag 的内容控件。用下划线/方括号/底纹标注的可变项抽不出来，须先改为内容控件");
        return new ExtractResult(slots, warnings);
    }

    private static (SlotDataType, string?, string?) DetectType(SdtElement sdt, List<string> warnings,
        IReadOnlyDictionary<string, List<SdtElement>> checkboxGroups)
    {
        var props = sdt.SdtProperties!;
        // 勾选类：复选框内容控件（5.2.4：不使用勾选符号字符）。
        // 同 Tag 的一组框（☐保温☐隔热☐保冷☐无）是一道单选题，单个框是「是/否」
        if (IsCheckbox(sdt))
        {
            var tag = props.GetFirstChild<Tag>()?.Val?.Value ?? "";
            if (checkboxGroups.TryGetValue(tag, out var group) && group.Count >= 2)
            {
                var options = group.Select(TemplateNormalizer.CheckboxLabel).Where(o => o.Length > 0).Distinct().ToList();
                if (options.Count >= 2)
                    return (SlotDataType.SingleChoice, JsonSerializer.Serialize(options, Json), null);
            }
            return (SlotDataType.SingleChoice, JsonSerializer.Serialize(new[] { "是", "否" }, Json), null);
        }
        if (props.GetFirstChild<SdtContentDate>() is not null)
            return (SlotDataType.Date, null, null);
        // 下拉列表只能选；组合框在 Word 里是「选择或填写」，候选项照给，但也收候选项以外的值
        var drop = props.GetFirstChild<SdtContentDropDownList>()?.Elements<ListItem>();
        var combo = props.GetFirstChild<SdtContentComboBox>()?.Elements<ListItem>();
        if (drop is not null || combo is not null)
        {
            var items = (drop ?? combo!)
                .Select(i => (i.Value?.Value ?? i.DisplayText?.Value)?.Trim())
                .Where(v => !string.IsNullOrEmpty(v) && !IsPromptItem(v!))
                .Distinct()
                .ToList();
            return (drop is not null ? SlotDataType.SingleChoice : SlotDataType.ChoiceOrText,
                JsonSerializer.Serialize(items, Json), null);
        }
        // 重复节：一行为行模板，子字段各自为行内控件（5.2.4）
        if (IsRepeatingSection(sdt))
        {
            var subs = sdt.Descendants<SdtElement>()
                .Select(child => new
                {
                    tag = child.SdtProperties?.GetFirstChild<Tag>()?.Val?.Value,
                    name = ParseAlias(child.SdtProperties?.GetFirstChild<SdtAlias>()?.Val?.Value).Item1
                           ?? InnerText(child)
                })
                .Where(x => x.tag is not null)
                .Select(x => new { x.tag, name = string.IsNullOrWhiteSpace(x.name) ? x.tag : x.name!.Trim() })
                .ToList();
            if (subs.Count == 0)
                warnings.Add("重复节控件内没有带 Tag 的子控件，生成时无法按数据复制行");
            return (SlotDataType.RepeatingRows, null, JsonSerializer.Serialize(subs, Json));
        }
        // 块级控件包整段/整表 → 长段落；行内控件 → 文本（数值/日期等由管理员在界面细分）
        return (sdt is SdtBlock ? SlotDataType.LongText : SlotDataType.Text, null, null);
    }

    /// <summary>控件紧后面那截文字里的计量单位（模板写成「全容积 ____ ml」，ml 是版面固定文字）。
    /// 存下来有三处用：提问时显示单位、继承取值时把重复单位去掉、回填时不会出现「20000ml ml」。</summary>
    private static string? UnitAfter(SdtElement sdt)
    {
        var text = string.Concat(sdt.ElementsAfter().SelectMany(e => e.Descendants<Text>()).Select(t => t.Text));
        if (text.Length == 0 && sdt.Parent is not null)
            text = string.Concat(sdt.Parent.ElementsAfter().SelectMany(e => e.Descendants<Text>()).Select(t => t.Text));
        var m = System.Text.RegularExpressions.Regex.Match(text.TrimStart(),
            @"^(MPaG|MPa|kPa|bar|Pa|Nm3/h|m3/h|kg/h|kg/m3|N·m|μm|µm|ml|mL|µL|uL|L|kg|g|t|℃|°C|K|cp|cP|mPa·s|rpm|r/min|Hz|kW|W|V|A|mm|cm|m3|m³|m|%)(?![A-Za-z])");
        return m.Success ? m.Value : null;
    }

    /// <summary>控件标题（别名）→ (显示名称, 章节)。「章节/名称」按第一个斜杠切分（全角／也认）；
    /// 没有斜杠就只当显示名称，章节仍由管理员在界面补。</summary>
    private static (string?, string?) ParseAlias(string? alias)
    {
        alias = alias?.Trim();
        if (string.IsNullOrEmpty(alias)) return (null, null);
        var cut = alias.IndexOfAny(['/', '／']);
        if (cut <= 0 || cut >= alias.Length - 1) return (alias, null);
        var section = alias[..cut].Trim();
        var name = alias[(cut + 1)..].Trim();
        return (name.Length == 0 ? alias : name, section.Length == 0 ? null : section);
    }

    private static bool IsTextual(SlotDataType t) => t is SlotDataType.Text or SlotDataType.LongText;

    /// <summary>下拉里「选择一项」这类提示项不是候选值。</summary>
    private static bool IsPromptItem(string v)
        => v.TrimEnd('。', '.') is "选择一项" or "请选择" or "选择" or "选择或填写" or "选择/填写";

    internal static bool IsCheckbox(SdtElement sdt)
        => sdt.SdtProperties?.GetFirstChild<DocumentFormat.OpenXml.Office2010.Word.SdtContentCheckBox>() is not null;

    /// <summary>控件正显示灰色占位文字（Word 的 showingPlcHdr）：里面的字是提示，不是内容。</summary>
    internal static bool ShowsPlaceholder(SdtElement sdt)
        => sdt.SdtProperties?.GetFirstChild<ShowingPlaceholder>() is not null;

    /// <summary>模板是否用了 Word 的占位状态。用了，才能据此区分「提示文字」和「写好的默认内容」。</summary>
    internal static bool UsesPlaceholderState(OpenXmlElement body)
        => body.Descendants<SdtElement>().Any(ShowsPlaceholder);

    internal static bool IsRepeatingSection(SdtElement sdt)
        => sdt.SdtProperties?.ChildElements.Any(e => e.LocalName == "repeatingSection") ?? false;

    private static string InnerText(SdtElement sdt)
        => string.Concat(sdt.Descendants<Text>().Select(t => t.Text));

    private static string Truncate(string? s) => string.IsNullOrEmpty(s) ? "空" : s.Length <= 20 ? s : s[..20] + "…";
}
