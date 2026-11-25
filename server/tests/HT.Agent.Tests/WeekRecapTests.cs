using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class WeekRecapTests
{
    private static DateTimeOffset At(int d, int h) => new(2026, 11, d, h, 0, 0, TimeSpan.FromHours(8));   // 2026-11-23 是周一

    [Theory]
    [InlineData(27, 13, false)]   // 周五中午还没到
    [InlineData(27, 14, true)]
    [InlineData(28, 9, true)]     // 周六
    [InlineData(29, 22, true)]    // 周日
    [InlineData(30, 9, false)]    // 下周一就没了
    [InlineData(25, 15, false)]   // 周三
    public void 周五下午到周日出(int d, int h, bool show) => Assert.Equal(show, WeekRecap.ShowOn(At(d, h)));

    [Fact]
    public void 一周从周一零点算()
    {
        Assert.Equal(At(23, 0), WeekRecap.WeekStart(At(27, 15)));
        Assert.Equal(At(23, 0), WeekRecap.WeekStart(At(29, 23)));
        Assert.Equal(At(23, 0), WeekRecap.WeekStart(At(23, 0)));
    }

    [Fact]
    public void 数回答里查了几次资料()
    {
        const string p = """{"kind":"assistant","steps":[{"tool":"think"},{"tool":"search_documents"},{"tool":"search_standards"},{"tool":"run_python"},{"tool":"fetch_url"},{"tool":"query_projects"}]}""";
        Assert.Equal(4, WeekRecap.Lookups(p));
        Assert.Equal(0, WeekRecap.Lookups(null));
        Assert.Equal(0, WeekRecap.Lookups("""{"steps": 坏了"""));
        Assert.Equal(0, WeekRecap.Lookups("""{"kind":"ledger"}"""));
    }

    [Fact]
    public void 最费劲的是来回最多的那个_至少三轮()
    {
        var h = WeekRecap.HardestOf([("A", 2, 0), ("中科科乐技术协议", 9, 2), ("B", 9, 0)]);
        Assert.Equal(("中科科乐技术协议", 9, 2), (h!.Title, h.Rounds, h.Files));
        Assert.Null(WeekRecap.HardestOf([("A", 2, 1)]));
    }
}
