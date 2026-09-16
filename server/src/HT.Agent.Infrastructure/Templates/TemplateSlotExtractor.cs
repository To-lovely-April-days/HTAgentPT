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
    public record ExtractedSlot(string Tag, string? Name, string? Section, string? Prompt,
        SlotDataType DataType, string? Choices, bool ForbidInherit, string? SubFields, int SortOrder);

    public record ExtractResult(IReadOnlyList<ExtractedSlot> Slots, IReadOnlyList<string> Warnings);

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
        var seen = new HashSet<string>();
        var order = 0;

        foreach (var sdt in main.Document.Body!.Descendants<SdtElement>())
        {
            // 重复节内的子控件由父级的 SubFields 声明，不单列为槽位
            if (sdt.Ancestors<SdtElement>().Any(a => IsRepeatingSection(a))) continue;

            var props = sdt.SdtProperties;
            var tag = props?.GetFirstChild<Tag>()?.Val?.Value;
            var placeholder = InnerText(sdt);
            if (string.IsNullOrWhiteSpace(tag))
            {
                warnings.Add($"发现无 Tag 的内容控件（占位文字「{Truncate(placeholder)}」），已跳过——控件 Tag 是槽位标识，模板里必须设置");
                continue;
            }
            if (!seen.Add(tag))
            {
                warnings.Add($"Tag「{tag}」重复出现，仅保留第一处（表 5-2：槽位标识在模板中唯一）");
                continue;
            }

            var (dataType, choices, subFields) = DetectType(sdt, warnings);
            var (name, section) = ParseAlias(props?.GetFirstChild<SdtAlias>()?.Val?.Value);
            slots.Add(new ExtractedSlot(
                tag, name, section,
                string.IsNullOrWhiteSpace(placeholder) ? null : placeholder.Trim(),
                dataType, choices,
                ForbidHints.Any(h => tag.Contains(h, StringComparison.OrdinalIgnoreCase) ||
                                     (placeholder?.Contains(h) ?? false)),
                subFields, order++));
        }
        if (slots.Count == 0)
            warnings.Add("模板中没有任何带 Tag 的内容控件。用下划线/方括号/底纹标注的可变项抽不出来，须先改为内容控件");
        return new ExtractResult(slots, warnings);
    }

    private static (SlotDataType, string?, string?) DetectType(SdtElement sdt, List<string> warnings)
    {
        var props = sdt.SdtProperties!;
        // 勾选类：复选框内容控件（5.2.4：不使用勾选符号字符）
        if (props.ChildElements.Any(e => e.LocalName == "checkbox"))
            return (SlotDataType.SingleChoice, JsonSerializer.Serialize(new[] { "是", "否" }), null);
        if (props.GetFirstChild<SdtContentDate>() is not null)
            return (SlotDataType.Date, null, null);
        var list = props.GetFirstChild<SdtContentDropDownList>()?.Elements<ListItem>()
                   ?? props.GetFirstChild<SdtContentComboBox>()?.Elements<ListItem>();
        if (list is not null)
            return (SlotDataType.SingleChoice,
                JsonSerializer.Serialize(list.Select(i => i.Value?.Value ?? i.DisplayText?.Value).Where(v => v != null)),
                null);
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
            return (SlotDataType.RepeatingRows, null, JsonSerializer.Serialize(subs));
        }
        // 块级控件包整段/整表 → 长段落；行内控件 → 文本（数值/日期等由管理员在界面细分）
        return (sdt is SdtBlock ? SlotDataType.LongText : SlotDataType.Text, null, null);
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

    internal static bool IsRepeatingSection(SdtElement sdt)
        => sdt.SdtProperties?.ChildElements.Any(e => e.LocalName == "repeatingSection") ?? false;

    private static string InnerText(SdtElement sdt)
        => string.Concat(sdt.Descendants<Text>().Select(t => t.Text));

    private static string Truncate(string? s) => string.IsNullOrEmpty(s) ? "空" : s.Length <= 20 ? s : s[..20] + "…";
}
