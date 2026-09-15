using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class CaseRendererTests
{
    [Fact]
    public void 固定模板渲染_含全部字段()
    {
        var text = CaseRenderer.Render("FC-2025-0001", "CJF-5L", "E-17",
            "搅拌电机过载停机", "磁力耦合卡滞", "1. 泄压\n2. 清洗", "轴承一套", "已解决",
            new Dictionary<string, string> { ["现场温度"] = "-10℃" });
        Assert.Contains("【故障案例 FC-2025-0001】", text);
        Assert.Contains("设备型号：CJF-5L", text);
        Assert.Contains("报警代码：E-17", text);
        Assert.Contains("故障现象：搅拌电机过载停机", text);
        Assert.Contains("处理结果：已解决", text);
        Assert.Contains("现场温度：-10℃", text);
    }

    [Fact]
    public void 渲染是纯函数_同输入同输出()
    {
        string R() => CaseRenderer.Render("FC-1", "M", null, "P", "C", "S", null, "R");
        Assert.Equal(R(), R());
        Assert.DoesNotContain("报警代码", R()); // 可选字段为空不渲染标签
    }
}

public class TextSegmenterTests
{
    [Fact]
    public void 分批_不超单批上限且不丢段()
    {
        var paras = Enumerable.Range(0, 10).Select(i => new string((char)('a' + i), 120)).ToList();
        var batches = TextSegmenter.Batch(paras, 300);
        Assert.All(batches, b => Assert.True(b.Sum(p => p.Length) <= 300));
        Assert.Equal(10, batches.Sum(b => b.Count));
    }

    [Fact]
    public void 超长单段按句边界硬切()
    {
        var para = string.Concat(Enumerable.Repeat("这是一句用于验证边界回退的话。", 40)); // 600 字
        var batches = TextSegmenter.Batch([para], 300);
        var pieces = batches.SelectMany(b => b).ToList();
        Assert.True(pieces.Count >= 2);
        Assert.All(pieces.SkipLast(1), p => Assert.EndsWith("。", p)); // 句边界收尾
        Assert.Equal(para.Length, pieces.Sum(p => p.Length)); // 不丢字
    }

    [Fact]
    public void 空行与回车换行清理()
    {
        var paras = TextSegmenter.SplitParagraphs("第一段\r\n\r\n第二段\n");
        Assert.Equal(["第一段", "第二段"], paras);
    }
}

public class DocxTranslatorTests
{
    private static byte[] MinimalDocx(string paragraphText)
    {
        using var ms = new MemoryStream();
        using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                   ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new DocumentFormat.OpenXml.Wordprocessing.Body(
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.Run(
                            new DocumentFormat.OpenXml.Wordprocessing.Text(paragraphText)))));
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task 译文写回输出字节_而非停留在未落盘的包缓冲()
    {
        // 回归锁：ToArray 曾在 Dispose 前取字节——报告说译了、文件还是原文
        var input = new MemoryStream(MinimalDocx("原文段落"));
        var result = await Infrastructure.Translation.DocxTranslator.TranslateAsync(
            input, (batch, _) => Task.FromResult<IReadOnlyList<string>>(
                batch.Select(t => "[EN] " + t).ToList()), 3000);
        Assert.Equal(1, result.Translated);
        using var reopened = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(
            new MemoryStream(result.Output), false);
        Assert.Contains("[EN] 原文段落", reopened.MainDocumentPart!.Document.InnerText);
    }
}
