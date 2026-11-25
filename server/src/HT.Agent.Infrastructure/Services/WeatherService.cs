using System.Globalization;
using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace HT.Agent.Infrastructure.Services;

/// <summary>首页天气：按后台「系统设置」里填的公司所在城市取（同事都在内网，按 IP 查不出城市），全公司共用，30 分钟取一次。
/// 天气源是 Open-Meteo（免费、不用注册），请求里只有城市名和坐标，不带任何人的信息。
/// 城市没填、服务器上不了外网、对方出错：都当没有天气，首页照旧。</summary>
public class WeatherService(IHttpClientFactory http, IRuntimeConfig config, IMemoryCache cache, ILogger<WeatherService> logger)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    public async Task<WeatherLogic.View?> TodayAsync(CancellationToken ct)
    {
        var city = (await config.GetStringAsync(ConfigKeys.HomeWeatherCity, "", ct)).Trim();
        if (city.Length == 0) return null;
        var key = "weather:" + city;
        if (cache.TryGetValue(key, out WeatherLogic.View? hit)) return hit;
        WeatherLogic.View? view = null;
        try { view = await FetchAsync(city); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            logger.LogInformation("取不到 {City} 的天气：{Message}", city, e.Message);
        }
        // 取不到也记一下，10 分钟内不再去试，免得每个人打开首页都等一次超时
        cache.Set(key, view, view is null ? RetryAfter : Fresh);
        return view;
    }

    private async Task<WeatherLogic.View?> FetchAsync(string city)
    {
        var client = http.CreateClient("weather");
        var geoKey = "weather-geo:" + city;
        if (!cache.TryGetValue(geoKey, out (double Lat, double Lon)? at) || at is null)
        {
            using var geo = JsonDocument.Parse(await client.GetStringAsync(
                $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=zh&format=json"));
            if (!geo.RootElement.TryGetProperty("results", out var rs) || rs.GetArrayLength() == 0)
            {
                logger.LogInformation("天气：找不到城市「{City}」", city);
                return null;
            }
            at = (rs[0].GetProperty("latitude").GetDouble(), rs[0].GetProperty("longitude").GetDouble());
            cache.Set(geoKey, at, TimeSpan.FromDays(7));
        }
        var lat = at.Value.Lat.ToString("0.####", CultureInfo.InvariantCulture);
        var lon = at.Value.Lon.ToString("0.####", CultureInfo.InvariantCulture);

        // daily 带上昨天（past_days=1）：[0] 是昨天，[1] 是今天
        using var fc = JsonDocument.Parse(await client.GetStringAsync(
            $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}"
            + "&current=temperature_2m,weather_code,wind_speed_10m,is_day&daily=temperature_2m_max,temperature_2m_min&past_days=1&forecast_days=1&timezone=auto"));
        var cur = fc.RootElement.GetProperty("current");
        var daily = fc.RootElement.GetProperty("daily");
        var mins = daily.GetProperty("temperature_2m_min");
        var maxs = daily.GetProperty("temperature_2m_max");
        var today = mins.GetArrayLength() - 1;
        double? Num(JsonElement a, int i) => i >= 0 && i < a.GetArrayLength() && a[i].ValueKind == JsonValueKind.Number ? a[i].GetDouble() : null;
        var temp = cur.GetProperty("temperature_2m").GetDouble();

        // PM2.5 另一个接口，取不到就不看霾
        double? pm25 = null;
        try
        {
            using var air = JsonDocument.Parse(await client.GetStringAsync(
                $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={lat}&longitude={lon}&current=pm2_5"));
            if (air.RootElement.TryGetProperty("current", out var ac) && ac.TryGetProperty("pm2_5", out var pm) && pm.ValueKind == JsonValueKind.Number)
                pm25 = pm.GetDouble();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { /* 没有空气质量就不看霾 */ }

        return WeatherLogic.Build(new WeatherLogic.Now(city, temp, Num(mins, today) ?? temp, Num(maxs, today) ?? temp, Num(mins, today - 1),
            cur.GetProperty("weather_code").GetInt32(), cur.TryGetProperty("wind_speed_10m", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : 0,
            !cur.TryGetProperty("is_day", out var d) || d.GetInt32() == 1, pm25));
    }
}
