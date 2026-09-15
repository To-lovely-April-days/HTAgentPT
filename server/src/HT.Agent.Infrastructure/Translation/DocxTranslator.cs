using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace HT.Agent.Infrastructure.Translation;

/// <summary>docx 格式回填（FR-6.3）：提取段落译后回填原位置。
/// 回填范围：正文、表格单元格（表格里的内容本身就是 w:p）、页眉页脚、脚注尾注、文本框
/// （w:txbxContent 内也是 w:p，Descendants 统一覆盖）。
/// 无法回填的元素如实列出（FR-6.3 末句）——域代码、图表、SmartArt、批注不翻译就说不翻译。</summary>
public static class DocxTranslator
{
    public record Result(byte[] Output, int Paragraphs, int Translated, IReadOnlyList<string> Unfillable);

    public static async Task<Result> TranslateAsync(
        Stream input,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<string>>> translateBatch,
        int batchChars,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await input.CopyToAsync(ms, ct);
        ms.Position = 0;

        int paragraphsCount, translatedCount;
        List<string> unfillableList;
        // Open XML 的包内容在 Dispose 时才落回流——字节必须在 using 结束之后取，
        // 否则拿到的是原文（报告说译了、文件没变，这种静默错误比抛异常更糟）
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var main = doc.MainDocumentPart
                ?? throw new InvalidDataException("不是有效的 Word 文档（缺少主文档部件）");

            var roots = new List<OpenXmlPartRootElement> { main.Document };
            roots.AddRange(main.HeaderParts.Select(p => (OpenXmlPartRootElement)p.Header));
            roots.AddRange(main.FooterParts.Select(p => (OpenXmlPartRootElement)p.Footer));
            if (main.FootnotesPart?.Footnotes is { } fn) roots.Add(fn);
            if (main.EndnotesPart?.Endnotes is { } en) roots.Add(en);

            // 收集可译段落。mc:Fallback 是 Choice 的降级副本，翻译它会把同一文本框译两遍
            var targets = new List<(Paragraph P, string Text)>();
            foreach (var root in roots)
            foreach (var p in root.Descendants<Paragraph>())
            {
                if (p.Ancestors().Any(a => a.LocalName == "Fallback")) continue;
                var text = string.Concat(p.Descendants<Text>()
                    .Where(t => !t.Ancestors().Any(a => a.LocalName == "Fallback"))
                    .Select(t => t.Text));
                if (!string.IsNullOrWhiteSpace(text))
                    targets.Add((p, text));
            }

            // 分批翻译（FR-6.2）：批内段数与译出段数必须一致，不一致的批整批标记失败原样保留
            var translations = new string?[targets.Count];
            var batch = new List<int>();
            var size = 0;
            async Task FlushAsync()
            {
                if (batch.Count == 0) return;
                var sources = batch.Select(i => targets[i].Text).ToList();
                var outputs = await translateBatch(sources, ct);
                if (outputs.Count == sources.Count)
                    for (var k = 0; k < batch.Count; k++)
                        translations[batch[k]] = outputs[k];
                batch.Clear();
                size = 0;
            }
            for (var i = 0; i < targets.Count; i++)
            {
                if (size > 0 && size + targets[i].Text.Length > batchChars) await FlushAsync();
                batch.Add(i);
                size += targets[i].Text.Length;
            }
            await FlushAsync();

            // 回填：首个 w:t 承载全部译文并继承其 run 格式，其余 w:t 清空。
            // 非文本 run（图片、换行、域）原位保留——这正是「保留编号与图片位置」的实现方式
            var translated = 0;
            for (var i = 0; i < targets.Count; i++)
            {
                if (translations[i] is not { } value) continue;
                var texts = targets[i].P.Descendants<Text>()
                    .Where(t => !t.Ancestors().Any(a => a.LocalName == "Fallback")).ToList();
                if (texts.Count == 0) continue;
                texts[0].Text = value;
                texts[0].Space = SpaceProcessingModeValues.Preserve;
                foreach (var t in texts.Skip(1)) t.Text = string.Empty;
                translated++;
            }

            // 无法回填清单（FR-6.3 末句）
            var unfillable = new List<string>();
            var fieldCount = roots.Sum(r => r.Descendants<SimpleField>().Count()) +
                             roots.Sum(r => r.Descendants<FieldCode>().Count());
            if (fieldCount > 0) unfillable.Add($"域代码（页码/交叉引用等）× {fieldCount}：保留原样未翻译");
            var chartCount = main.ChartParts?.Count() ?? 0;
            if (chartCount > 0) unfillable.Add($"图表 × {chartCount}：图表内文字未翻译");
            var diagramCount = main.DiagramDataParts?.Count() ?? 0;
            if (diagramCount > 0) unfillable.Add($"SmartArt × {diagramCount}：图形内文字未翻译");
            if (main.WordprocessingCommentsPart is not null)
                unfillable.Add("批注：未翻译（批注是审阅痕迹，不属正文）");
            var failedBatches = translations.Count(t => t is null);
            if (failedBatches > 0)
                unfillable.Add($"段落 × {failedBatches}：译出段数与原文不对应，保留原文");

            main.Document.Save();
            foreach (var hp in main.HeaderParts) hp.Header.Save();
            foreach (var fp in main.FooterParts) fp.Footer.Save();
            main.FootnotesPart?.Footnotes.Save();
            main.EndnotesPart?.Endnotes.Save();

            paragraphsCount = targets.Count;
            translatedCount = translated;
            unfillableList = unfillable;
        }
        return new Result(ms.ToArray(), paragraphsCount, translatedCount, unfillableList);
    }
}
