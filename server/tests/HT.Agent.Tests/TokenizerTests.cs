using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class TokenizerTests
{
    [Fact]
    public void 型号与报警代码整串保留不被切分()
    {
        // 7.2：设备型号与报警代码一类含数字与连字符的串需保证不被错误切分
        var tokens = ChineseTokenizer.Tokenize("CJF-5L 报警代码 E-17 排查").Split(' ');
        Assert.Contains("cjf-5l", tokens);
        Assert.Contains("e-17", tokens);
    }

    [Fact]
    public void 中文按二元切分()
    {
        var tokens = ChineseTokenizer.Tokenize("磁力耦合").Split(' ');
        Assert.Equal(["磁力", "力耦", "耦合"], tokens);
    }

    [Fact]
    public void 单个汉字保留为单字()
    {
        Assert.Equal("釜", ChineseTokenizer.Tokenize("釜"));
    }

    [Fact]
    public void 查询侧转_tsquery_为OR并加引号()
    {
        var q = ChineseTokenizer.ToTsQuery("CJF-5L 过载");
        Assert.Contains("'cjf-5l'", q);
        Assert.Contains(" | ", q);
        Assert.DoesNotContain("&", q);
    }

    [Fact]
    public void tsquery_语法字符被剔除()
    {
        var q = ChineseTokenizer.ToTsQuery("a&b|c!d");
        Assert.DoesNotContain("&", q.Replace(" | ", ""));
        Assert.DoesNotContain("!", q);
    }

    [Fact]
    public void 索引侧与查询侧同一分词_同词必然互相命中()
    {
        var indexed = ChineseTokenizer.Tokenize("搅拌电机过载停机").Split(' ').ToHashSet();
        var queried = ChineseTokenizer.Tokenize("电机过载").Split(' ');
        Assert.True(queried.All(indexed.Contains));
    }
}
