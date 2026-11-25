namespace HT.Agent.Application.Logic;

/// <summary>首页天气：把天气服务给的数（WMO 天气代码、气温、风速、PM2.5）换成首页用的几样东西——
/// 画什么天（晴、多云、雨、雪……）、日期后面写的那几个字、小人穿哪一套、问候的小字要不要换成提醒。
/// 只是装饰，不进对话、不进模型。这里是纯逻辑，取数在 WeatherService。</summary>
public static class WeatherLogic
{
    /// <summary>天气服务给的原始数。Min/Max 是今天的最低最高气温；YesterdayMin 用来看降温。</summary>
    public record Now(string City, double Temp, double Min, double Max, double? YesterdayMin, int Code, double WindKmh, bool IsDay, double? Pm25);

    /// <summary>给首页的。Kind：clear 晴 / cloudy 多云 / overcast 阴 / rain 雨 / storm 雷雨 / snow 雪 / fog 雾 / haze 霾 / wind 大风。
    /// Outfit：normal / cool 围巾 / cold 羽绒服 / freezing 全套保暖 / hot 短袖草帽 / rain 雨衣 / mask 口罩。</summary>
    public record View(string City, int Temp, int Min, int Max, string Kind, string Label, string Outfit, bool VeryHot, bool IsDay, string? Tip);

    /// <summary>六级风（39 km/h）起算大风。</summary>
    public const double WindyKmh = 39;
    /// <summary>PM2.5 到中度污染（115）算霾。</summary>
    public const double HazePm25 = 115;

    public static string KindOf(int code, double windKmh, double? pm25)
    {
        if (code >= 95) return "storm";
        if (code is >= 71 and <= 77 or 85 or 86) return "snow";
        if (code is >= 51 and <= 67 or >= 80 and <= 82) return "rain";
        if (code is 45 or 48) return "fog";
        if (pm25 >= HazePm25) return "haze";
        if (windKmh >= WindyKmh) return "wind";
        return code switch { 0 => "clear", 3 => "overcast", _ => "cloudy" };
    }

    /// <summary>日期后面写的那几个字。</summary>
    public static string LabelOf(int code, string kind) => kind switch
    {
        "haze" => "霾",
        "wind" => "大风",
        _ => code switch
        {
            0 => "晴",
            1 => "晴间多云",
            2 => "多云",
            3 => "阴",
            45 or 48 => "雾",
            51 or 53 or 55 => "毛毛雨",
            56 or 57 or 66 or 67 => "冻雨",
            61 => "小雨",
            63 => "中雨",
            65 => "大雨",
            71 => "小雪",
            73 => "中雪",
            75 => "大雪",
            77 => "雪粒",
            80 or 81 => "阵雨",
            82 => "强阵雨",
            85 => "阵雪",
            86 => "强阵雪",
            95 => "雷阵雨",
            96 or 99 => "雷阵雨伴冰雹",
            _ => "多云",
        },
    };

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
        var kind = KindOf(n.Code, n.WindKmh, n.Pm25);
        return new View(n.City, (int)Math.Round(n.Temp), (int)Math.Round(n.Min), (int)Math.Round(n.Max), kind, LabelOf(n.Code, kind),
            OutfitOf(kind, n.Min, n.Max), n.Max >= 35, n.IsDay, TipOf(kind, n.Min, n.Max, n.YesterdayMin));
    }
}
