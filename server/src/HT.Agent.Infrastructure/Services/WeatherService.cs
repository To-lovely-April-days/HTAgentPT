using System.Globalization;
using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace HT.Agent.Infrastructure.Services;

/// <summary>首页天气：按后台「系统设置」里填的公司所在城市取（同事都在内网，按 IP 查不出城市），全公司共用，30 分钟取一次。
/// 天气源是高德开放平台的天气查询（国内连得上；要一个「Web 服务」类型的 key，免费额度一天 5000 次，这里一天用不到 100 次）。
/// 请求里只有城市名、城市编码和 key，不带任何人的信息。城市或 key 没填、连不上、对方报错：都当没有天气，首页照旧。</summary>
public class WeatherService(IHttpClientFactory http, IRuntimeConfig config, IMemoryCache cache, ILogger<WeatherService> logger)
{
    private const string Api = "https://restapi.amap.com/v3";
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    public async Task<WeatherLogic.View?> TodayAsync(CancellationToken ct)
    {
        var city = (await config.GetStringAsync(ConfigKeys.HomeWeatherCity, "", ct)).Trim();
        var key = (await config.GetStringAsync(ConfigKeys.HomeWeatherKey, "", ct)).Trim();
        if (city.Length == 0 || key.Length == 0) return null;
        var ck = "weather:" + city;
        if (cache.TryGetValue(ck, out WeatherLogic.View? hit)) return hit;
        WeatherLogic.View? view = null;
        try { view = await FetchAsync(city, key); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            logger.LogInformation("取不到 {City} 的天气：{Message}", city, e.Message);
        }
        // 取不到也记一下，10 分钟内不再去试，免得每个人打开首页都等一次超时
        cache.Set(ck, view, view is null ? RetryAfter : Fresh);
        return view;
    }

    /// <summary>高德的返回：status 不是 "1" 就是出错了（key 不对、超额度……），info 里写着原因。</summary>
    private async Task<JsonElement?> GetAsync(HttpClient client, string url)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync(url));
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("status", out var st) && st.GetString() == "1") return root;
        logger.LogInformation("高德天气接口报错：{Info}", root.TryGetProperty("info", out var info) ? info.GetString() : "未知");
        return null;
    }

    private async Task<WeatherLogic.View?> FetchAsync(string city, string key)
    {
        var client = http.CreateClient("weather");
        var k = Uri.EscapeDataString(key);

        // 城市名 → 城市编码（adcode），记 7 天
        var codeKey = "weather-adcode:" + city;
        if (!cache.TryGetValue(codeKey, out string? adcode) || string.IsNullOrEmpty(adcode))
        {
            var d = await GetAsync(client, $"{Api}/config/district?keywords={Uri.EscapeDataString(city)}&subdistrict=0&key={k}");
            if (d is not { } dr || !dr.TryGetProperty("districts", out var ds) || ds.GetArrayLength() == 0)
            {
                logger.LogInformation("天气：找不到城市「{City}」", city);
                return null;
            }
            adcode = ds[0].GetProperty("adcode").GetString();
            if (string.IsNullOrEmpty(adcode)) return null;
            cache.Set(codeKey, adcode, TimeSpan.FromDays(7));
        }

        var live = await GetAsync(client, $"{Api}/weather/weatherInfo?city={adcode}&extensions=base&key={k}");
        if (live is not { } lr || !lr.TryGetProperty("lives", out var lives) || lives.GetArrayLength() == 0) return null;
        var now = lives[0];
        var text = now.GetProperty("weather").GetString() ?? "";
        var temp = Num(now, "temperature") ?? 0;
        var wind = WeatherLogic.WindLevelOf(now.TryGetProperty("windpower", out var wp) ? wp.GetString() : null);

        // 今天的最高最低（预报的第一天）；拿不到就用现在的气温
        double min = temp, max = temp;
        var fc = await GetAsync(client, $"{Api}/weather/weatherInfo?city={adcode}&extensions=all&key={k}");
        if (fc is { } fr && fr.TryGetProperty("forecasts", out var fs) && fs.GetArrayLength() > 0
            && fs[0].TryGetProperty("casts", out var casts) && casts.GetArrayLength() > 0)
        {
            var c0 = casts[0];
            var day = Num(c0, "daytemp"); var night = Num(c0, "nighttemp");
            if (day is { } a && night is { } b) { min = Math.Min(Math.Min(a, b), temp); max = Math.Max(Math.Max(a, b), temp); }
        }

        // 降温：高德不给昨天的，自己记着每天的最低气温（服务重启后第一天不提醒降温）
        var today = HomeService.Now.Date;
        cache.Set($"weather-min:{city}:{today:yyyy-MM-dd}", min, TimeSpan.FromDays(2));
        double? yesterday = cache.TryGetValue($"weather-min:{city}:{today.AddDays(-1):yyyy-MM-dd}", out double y) ? y : null;

        var hour = HomeService.Now.Hour;
        return WeatherLogic.Build(new WeatherLogic.Now(city, temp, min, max, yesterday, text, wind, hour >= 6 && hour < 18));
    }

    private static double? Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && double.TryParse(v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
}
