using HT.Agent.Application.Logic;
using HT.Agent.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

/// <summary>首页：今天的问候、最要紧的几件正事。每人每天生成一次。</summary>
[ApiController]
[Route("api/home")]
[Authorize]
public class HomeController(HomeService home, WeatherService weather) : ControllerBase
{
    /// <summary>refresh=true 是「换一批」，每天最多 3 次。</summary>
    [HttpGet("today")]
    public async Task<IActionResult> Today([FromQuery] bool refresh, CancellationToken ct)
    {
        var t = await home.TodayAsync(refresh, ct);
        var b = t.Brief;
        return Ok(new
        {
            name = t.Name,
            date = HomeService.Now.ToString("yyyy-MM-dd"),
            lines = b.Lines.ToDictionary(kv => kv.Key, kv => new { title = kv.Value.Title, sub = kv.Value.Sub }),
            work = b.Work.Select(w => new { w.Id, w.Emoji, w.Title, w.Badge, w.Tone, w.Detail, w.Action, w.Prompt }),
            recent = t.Recent,
            offWork = t.OffWork,
            refreshLeft = t.RefreshLeft,
            generated = b.Generated,
            pending = t.Pending,
        });
    }

    /// <summary>首页天气（公司所在城市，全公司共用）。没填城市、取不到时 ok=false，首页照旧。</summary>
    [HttpGet("weather")]
    public async Task<IActionResult> Weather(CancellationToken ct)
    {
        var w = await weather.TodayAsync(ct);
        if (w is null) return Ok(new { ok = false });
        return Ok(new { ok = true, w.City, w.Temp, w.Min, w.Max, w.Kind, w.Label, w.Outfit, w.VeryHot, w.IsDay, w.Tip });
    }
}
