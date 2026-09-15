using HT.Agent.Api.Security;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

/// <summary>使用统计（FR-9.8，E18）：各模块调用次数、活跃人数、检索命中率、无结果比例，按周输出。
/// 数据源就是审计——FR-9.1 已经把每次调用都记了，统计只是换个切面读。</summary>
[ApiController]
[Route("api/stats")]
[Authorize]
[RequirePermission(PermissionKeys.AuditRead)]
public class StatsController(AppDbContext db) : ControllerBase
{
    /// <summary>动作键 → 模块名（界面文案不出现技术名称，模块名与表 9-1 对齐）。</summary>
    private static readonly (string Prefix, string Module)[] ModuleMap =
    [
        ("qa.ask", "检索问答"), ("retrieve.query", "检索问答"),
        ("project.search", "项目查询"), ("project.get", "项目查询"),
        ("gen.", "方案生成"), ("clause.assemble", "报价合同"),
        ("translate.", "翻译"), ("case.", "故障案例"), ("ticket.", "报修工单")
    ];

    [HttpGet("usage")]
    public async Task<IActionResult> Usage([FromQuery] int weeks = 8, CancellationToken ct = default)
    {
        weeks = Math.Clamp(weeks, 1, 26);
        var from = StartOfWeek(DateTimeOffset.UtcNow).AddDays(-7 * (weeks - 1));
        var logs = await db.AuditLogs.AsNoTracking()
            .Where(a => a.At >= from && a.Result == AuditResult.Success)
            .Select(a => new { a.At, a.Action, a.UserId, a.Detail })
            .ToListAsync(ct);

        var byWeek = logs.GroupBy(a => StartOfWeek(a.At)).OrderBy(g => g.Key).Select(g =>
        {
            var moduleCounts = ModuleMap
                .GroupBy(m => m.Module)
                .ToDictionary(mg => mg.Key,
                    mg => g.Count(a => mg.Any(m => a.Action.StartsWith(m.Prefix, StringComparison.Ordinal))));
            var asks = g.Where(a => a.Action == "qa.ask" && a.Detail != null).ToList();
            var answered = asks.Count(a => a.Detail!.Contains("\"answered\": true") || a.Detail!.Contains("\"answered\":true"));
            var noResult = asks.Count(a => a.Detail!.Contains("\"answered\": false") || a.Detail!.Contains("\"answered\":false"));
            var totalAsks = answered + noResult;
            return new
            {
                weekStart = g.Key.ToString("yyyy-MM-dd"),
                modules = moduleCounts,
                totalCalls = moduleCounts.Values.Sum(),
                activeUsers = g.Where(a => a.UserId != null).Select(a => a.UserId).Distinct().Count(),
                // 命中率 = 过阈值且给出回答的占比；无结果 = 如实返回未找到的占比（不是故障率）
                hitRate = totalAsks == 0 ? (double?)null : Math.Round(100.0 * answered / totalAsks, 1),
                noResultRate = totalAsks == 0 ? (double?)null : Math.Round(100.0 * noResult / totalAsks, 1)
            };
        }).ToList();
        return Ok(byWeek);
    }

    /// <summary>无结果问题清单（E18 的语料缺口表）：这个比例要读成缺口线索，不是错误率。</summary>
    [HttpGet("no-result")]
    public async Task<IActionResult> NoResult([FromQuery] int days = 7, CancellationToken ct = default)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 90));
        var rows = await db.QaMessages.AsNoTracking()
            .Where(m => m.At >= from && m.Answer == null && m.NoResultHints != null)
            .OrderByDescending(m => m.At).Take(500)
            .Select(m => new { m.Question, m.RewrittenQuery, m.At, m.NoResultHints })
            .ToListAsync(ct);
        var grouped = rows.GroupBy(r => r.RewrittenQuery ?? r.Question)
            .Select(g => new { question = g.Key, count = g.Count(), lastAt = g.Max(x => x.At), hints = g.First().NoResultHints })
            .OrderByDescending(x => x.count).Take(50).ToList();
        return Ok(grouped);
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset t)
    {
        var d = t.UtcDateTime.Date;
        var diff = ((int)d.DayOfWeek + 6) % 7; // 周一起算
        return new DateTimeOffset(d.AddDays(-diff), TimeSpan.Zero);
    }
}
