namespace HT.Agent.Application.Logic;

/// <summary>首页天气：把天气服务（高德）给的天气描述、气温、风力换成首页用的几样东西——
/// 画什么天（晴、多云、雨、雪……）、日期后面写的那几个字、小人穿哪一套、问候的小字要不要换成提醒。
/// 只是装饰，不进对话、不进模型。这里是纯逻辑，取数在 WeatherService。</summary>
public static class WeatherLogic
{
    /// <summary>天气服务给的原始数。Text 是天气描述（「小雨」「多云」「中度霾」）；Min/Max 是今天的最低最高气温；
    /// YesterdayMin 用来看降温（没有就不提醒降温）；WindLevel 是风力级数。</summary>
    public record Now(string City, double Temp, double Min, double Max, double? YesterdayMin, string Text, int WindLevel, bool IsDay);

    /// <summary>给首页的。Kind：clear 晴 / cloudy 多云 / overcast 阴 / rain 雨 / storm 雷雨 / snow 雪 / fog 雾 / haze 霾 / wind 大风。
    /// Outfit：normal / cool 围巾 / cold 羽绒服 / freezing 全套保暖 / hot 短袖草帽 / rain 雨衣 / mask 口罩。</summary>
    public record View(string City, int Temp, int Min, int Max, string Kind, string Label, string Outfit, bool VeryHot, bool IsDay, string? Tip);

    /// <summary>六级风起算大风。</summary>
    public const int WindyLevel = 6;

    /// <summary>天气描述 → 画什么天。先看雷、雨雪，再看雾霾沙尘、大风，最后看阴晴。</summary>
    public static string KindOf(string? text, int windLevel)
    {
        var t = text ?? "";
        if (t.Contains('雷') || t.Contains("冰雹")) return "storm";
        if (t.Contains("雨夹雪") || t.Contains("冻雨")) return "rain";
        if (t.Contains('雪')) return "snow";
        if (t.Contains('雨')) return "rain";
        if (t.Contains('霾') || t.Contains('沙') || t.Contains('尘')) return "haze";
        if (t.Contains('雾')) return "fog";
        if (windLevel >= WindyLevel || t.Contains("大风") || t.Contains("暴风") || t.Contains("飓风") || t.Contains("龙卷")) return "wind";
        if (t.Contains('阴')) return "overcast";
        if (t.Contains('云')) return "cloudy";
        if (t.Contains('晴')) return "clear";
        return "cloudy";
    }

    /// <summary>风力：高德给的是「≤3」「4」「4-5」这样的字，取里面最大的数字。</summary>
    public static int WindLevelOf(string? power)
    {
        var best = 0;
        foreach (var part in (power ?? "").Split('-', '~', '～'))
        {
            var digits = new string(part.Where(char.IsAsciiDigit).ToArray());
            if (int.TryParse(digits, out var n)) best = Math.Max(best, n);
        }
        return best;
    }

    /// <summary>小人穿哪一套：下雨穿雨衣、雾霾戴口罩，其余按今天的最高最低气温（一整天不变）。</summary>
    public static string OutfitOf(string kind, double min, double max)
    {
        if (kind is "rain" or "storm") return "rain";
        if (kind is "fog" or "haze") return "mask";
        if (max >= 30) return "hot";
        if (min < 0) return "freezing";
        if (min < 10) return "cold";
        if (min < 18) return "cool";
        return "normal";
    }

    /// <summary>问候的小字换成提醒（只在值得提醒的天气换）；没有就照用模型写的。只说生活。</summary>
    public static string? TipOf(string kind, double min, double max, double? yesterdayMin)
    {
        var drop = yesterdayMin is { } y ? (int)Math.Round(y - min) : 0;
        return kind switch
        {
            "storm" => "有雷雨，先别急着出门。",
            "rain" => "下雨了，路上慢点，记得带伞。",
            "snow" => "下雪了，路上注意防滑。",
            "haze" => "空气不太好，出门戴口罩。",
            "fog" => "今天有雾，开车慢一点。",
            _ when max >= 35 => "天太热了，多喝水，少晒太阳。",
            _ when drop >= 8 => $"今天降温 {drop}°，多穿点。",
            _ when min < 0 => "零下了，帽子手套都戴上。",
            "wind" => "风大，多穿点，注意保暖。",
            _ => null,
        };
    }

    public static View Build(Now n)
    {
        var kind = KindOf(n.Text, n.WindLevel);
        var label = kind == "wind" && !n.Text.Contains('风') ? "大风" : n.Text;
        return new View(n.City, (int)Math.Round(n.Temp), (int)Math.Round(n.Min), (int)Math.Round(n.Max), kind, label,
            OutfitOf(kind, n.Min, n.Max), n.Max >= 35, n.IsDay, TipOf(kind, n.Min, n.Max, n.YesterdayMin));
    }
}
