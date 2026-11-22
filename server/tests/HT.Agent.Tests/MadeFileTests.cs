using System.IO.Compression;
using System.Text.Json;
using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class MadeFileTests
{
    private static byte[] Zip(string entry)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            using (var w = new StreamWriter(z.CreateEntry(entry).Open())) w.Write("<x/>");
        return ms.ToArray();
    }

    [Theory]
    [InlineData("报价单.xlsx", "xlsx")]
    [InlineData("out/修订.DOCX", "docx")]
    [InlineData("a.csv", "csv")]
    [InlineData("a.txt", null)]
    public void Kind_from_name(string name, string? kind) => Assert.Equal(kind, MadeFiles.KindOf(name));

    [Fact]
    public void Basic_check_catches_wrong_files()
    {
        Assert.Empty(MadeFiles.BasicProblems(Zip("xl/workbook.xml"), "xlsx"));
        Assert.NotEmpty(MadeFiles.BasicProblems(Zip("word/document.xml"), "xlsx"));
        Assert.NotEmpty(MadeFiles.BasicProblems("hello"u8.ToArray(), "docx"));
        Assert.Empty(MadeFiles.BasicProblems("%PDF-1.4 ..."u8.ToArray(), "pdf"));
        Assert.NotEmpty(MadeFiles.BasicProblems("PK"u8.ToArray(), "pdf"));
        Assert.NotEmpty(MadeFiles.BasicProblems([], "csv"));
    }

    [Fact]
    public void Detail_reads_like_the_card()
    {
        var info = JsonDocument.Parse("""{"sheets":3,"formulas":42,"errors":0}""").RootElement;
        Assert.Equal("3 个工作表 · 42 个公式 · 已检查：能打开，公式没有错误", MadeFiles.Detail("xlsx", info, true));
        var doc = JsonDocument.Parse("""{"revisions":7,"comments":4,"pages":2}""").RootElement;
        Assert.Equal("修订 7 处 · 批注 4 条 · 2 页 · 已检查：能打开", MadeFiles.Detail("docx", doc, true));
        Assert.Equal("检查服务没连上，只确认了文件能打开", MadeFiles.Detail("pdf", null, false));
    }

    [Fact]
    public void Gives_up_after_three_tries()
    {
        Assert.Contains("还能再试 2 次", MadeFiles.ForModelAfterFail("a.xlsx", ["D9 #REF!"], 1));
        var last = MadeFiles.ForModelAfterFail("a.xlsx", ["D9 #REF!"], 3);
        Assert.Contains("不要再做这个文件", last);
        Assert.Contains("D9 #REF!", last);
    }

    [Fact]
    public void File_skills_follow_switches()
    {
        Assert.Equal("xlsx", MadeFiles.SkillKind("excel-files"));
        Assert.Equal("pdf", MadeFiles.SkillKind("pdf-files"));
        Assert.Null(MadeFiles.SkillKind("material-selection"));
        Assert.Equal("xlsx", MadeFiles.SwitchOf("csv"));
    }

    [Fact]
    public void Cad_files_are_recognised_and_checked()
    {
        Assert.Equal("dxf", MadeFiles.KindOf("30L加氢釜外形图.dxf"));
        Assert.Equal("step", MadeFiles.KindOf("model.STP"));
        Assert.True(MadeFiles.IsCad("step"));
        Assert.False(MadeFiles.IsCad("xlsx"));
        var dxf = System.Text.Encoding.ASCII.GetBytes("  0\nSECTION\n  2\nHEADER\n  0\nENDSEC\n  0\nEOF\n");
        Assert.Empty(MadeFiles.BasicProblems(dxf, "dxf"));
        Assert.NotEmpty(MadeFiles.BasicProblems(System.Text.Encoding.ASCII.GetBytes("  0\nSECTION\n"), "dxf"));
        Assert.Empty(MadeFiles.BasicProblems(System.Text.Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;"), "step"));
        Assert.NotEmpty(MadeFiles.BasicProblems(System.Text.Encoding.ASCII.GetBytes("hello"), "step"));
        Assert.Contains("CAD", MadeFiles.Detail("dxf", null, false));
    }
}
