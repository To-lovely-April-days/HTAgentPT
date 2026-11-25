using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class WeatherLogicTests
{
    [Theory]
    [InlineData("晴", 2, "clear")]
    [InlineData("少云", 2, "cloudy")]
    [InlineData("晴间多云", 2, "cloudy")]
    [InlineData("阴", 2, "overcast")]
    [InlineData("小雨", 2, "rain")]
    [InlineData("阵雨", 2, "rain")]
    [InlineData("雨夹雪", 2, "rain")]
    [InlineData("雷阵雨", 2, "storm")]
    [InlineData("雷阵雨并伴有冰雹", 2, "storm")]
    [InlineData("中雪", 2, "snow")]
    [InlineData("浓雾", 2, "fog")]
    [InlineData("中度霾", 2, "haze")]
    [InlineData("扬沙", 2, "haze")]
    [InlineData("晴", 6, "wind")]
    [InlineData("大风", 2, "wind")]
    public void 天气描述换成画什么天(string text, int wind, string kind)
        => Assert.Equal(kind, WeatherLogic.KindOf(text, wind));

    [Theory]
    [InlineData("≤3", 3)]
    [InlineData("4", 4)]
    [InlineData("4-5", 5)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void 风力取最大的级数(string? power, int level) => Assert.Equal(level, WeatherLogic.WindLevelOf(power));

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
        Assert.Null(WeatherLogic.TipOf("clear", 15, 24, null));
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
        var v = WeatherLogic.Build(new WeatherLogic.Now("上海", 8.6, 6.2, 11.4, 9, "小雨", 3, true));
        Assert.Equal(("上海", 9, 6, 11, "rain", "小雨", "rain", false), (v.City, v.Temp, v.Min, v.Max, v.Kind, v.Label, v.Outfit, v.VeryHot));
        Assert.Equal("大风", WeatherLogic.Build(new WeatherLogic.Now("上海", 8, 6, 11, null, "晴", 7, true)).Label);
    }
}
