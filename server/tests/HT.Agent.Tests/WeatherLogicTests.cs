using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class WeatherLogicTests
{
    [Theory]
    [InlineData(0, 5, null, "clear")]
    [InlineData(2, 5, null, "cloudy")]
    [InlineData(3, 5, null, "overcast")]
    [InlineData(61, 5, null, "rain")]
    [InlineData(81, 5, null, "rain")]
    [InlineData(95, 5, null, "storm")]
    [InlineData(73, 5, null, "snow")]
    [InlineData(86, 5, null, "snow")]
    [InlineData(45, 5, null, "fog")]
    [InlineData(1, 5, 150.0, "haze")]
    [InlineData(61, 5, 150.0, "rain")]      // 下雨优先于霾
    [InlineData(0, 45, null, "wind")]
    public void 天气代码换成画什么天(int code, double wind, double? pm25, string kind)
        => Assert.Equal(kind, WeatherLogic.KindOf(code, wind, pm25));

    [Theory]
    [InlineData("rain", 5, 12, "rain")]
    [InlineData("storm", 25, 33, "rain")]
    [InlineData("haze", -3, 4, "mask")]
    [InlineData("clear", 24, 33, "hot")]
    [InlineData("snow", -4, 1, "freezing")]
    [InlineData("cloudy", 3, 9, "cold")]
    [InlineData("cloudy", 12, 19, "cool")]
    [InlineData("clear", 19, 27, "normal")]
    public void 小人穿哪一套(string kind, double min, double max, string outfit)
        => Assert.Equal(outfit, WeatherLogic.OutfitOf(kind, min, max));

    [Fact]
    public void 大降温提醒多穿点_平常的天不换问候()
    {
        Assert.Equal("今天降温 9°，多穿点。", WeatherLogic.TipOf("cloudy", 3, 9, 12));
        Assert.Null(WeatherLogic.TipOf("clear", 15, 24, 16));
        Assert.Equal("下雨了，路上慢点，记得带伞。", WeatherLogic.TipOf("rain", 15, 24, 16));
    }

    [Fact]
    public void 提醒只说生活()
    {
        foreach (var k in new[] { "storm", "rain", "snow", "haze", "fog", "wind", "clear" })
            foreach (var (min, max, y) in new[] { (-5.0, 2.0, 5.0), (20.0, 37.0, 21.0), (2.0, 9.0, 14.0) })
                Assert.False(HomeBrief.Unfit(WeatherLogic.TipOf(k, min, max, y)));
    }

    [Fact]
    public void 组装给首页的()
    {
        var v = WeatherLogic.Build(new WeatherLogic.Now("上海", 8.6, 6.2, 11.4, 9, 61, 12, true, 30));
        Assert.Equal(("上海", 9, 6, 11, "rain", "小雨", "rain", false), (v.City, v.Temp, v.Min, v.Max, v.Kind, v.Label, v.Outfit, v.VeryHot));
    }
}
