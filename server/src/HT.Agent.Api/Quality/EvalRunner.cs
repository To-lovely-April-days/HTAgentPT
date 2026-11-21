using System.Text.Json;
using System.Threading.Channels;
using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Quality;

/// <summary>后台「跑一遍」：用测试账号把所有测试题挨个问一遍，按 EvalScoring 打分。
/// 每题开一个临时对话，问完就删——不留在测试账号的历史里，也不算进使用统计和错题。
/// 一次只跑一轮；服务重启时没跑完的那轮标成失败。</summary>
public sealed class EvalRunner(IServiceScopeFactory scopes, ILogger<EvalRunner> logger) : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PerQuestion = TimeSpan.FromMinutes(5);

    public ValueTask EnqueueAsync(Guid runId) => _queue.Writer.WriteAsync(runId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.EvalRuns.Where(r => r.Status == EvalRunStatus.Running)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, EvalRunStatus.Failed)
                    .SetProperty(r => r.Error, "服务重启，这次没跑完")
                    .SetProperty(r => r.FinishedAt, DateTimeOffset.UtcNow), stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "清理没跑完的测试失败");
        }

        await foreach (var runId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await RunAsync(runId, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "跑测试题出错 {Run}", runId);
                await FailAsync(runId, "跑的过程中出错，看服务日志");
            }
        }
    }

    private async Task RunAsync(Guid runId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.EvalRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.Status != EvalRunStatus.Running) return;
        var user = await db.Users.Include(u => u.Role).AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == run.RunAs && u.CompanyId == run.CompanyId && u.IsActive, ct);
        if (user?.Role is null)
        {
            await FailAsync(runId, $"测试账号 {run.RunAs} 不存在、已停用或没有角色");
            return;
        }
        var questions = await db.EvalQuestions.AsNoTracking()
            .Where(q => q.CompanyId == run.CompanyId && q.IsActive).OrderBy(q => q.Code).ToListAsync(ct);
        run.Total = questions.Count;
        await db.SaveChangesAsync(ct);
        // 按哪一档跑（开跑时记在配置快照里）：auto 就和员工一样按默认
        string? level = null;
        try { if (run.Config is not null && JsonSerializer.Deserialize<Dictionary<string, string>>(run.Config)?.TryGetValue("eval.level", out var l) == true) level = l; }
        catch (JsonException) { /* 老数据没有 */ }

        foreach (var q in questions)
        {
            var result = await AskOneAsync(user, q, level, ct);
            db.EvalResults.Add(result);
            run.Finished++;
            if (result.RetrievalOk is not null) { run.RetrievalTotal++; if (result.RetrievalOk == true) run.RetrievalOk++; }
            if (result.AnswerOk is not null) { run.AnswerTotal++; if (result.AnswerOk == true) run.AnswerOk++; }
            await db.SaveChangesAsync(ct);
        }
        run.Status = EvalRunStatus.Done;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<EvalResult> AskOneAsync(AppUser user, EvalQuestion q, string? level, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var result = new EvalResult { QuestionId = q.Id };
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<CurrentUserHolder>().Populate(user.Id, user.Username, user.Role!.Code, user.CompanyId,
            user.ActiveSessionId ?? Guid.Empty, user.Role.Classifications, user.Role.Permissions, user.CustomerNo, "eval", "eval-runner");
        var db = sp.GetRequiredService<AppDbContext>();
        var session = new QaSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, CompanyId = user.CompanyId, Title = $"[准确率测试] {q.Code}",
            CreatedAt = started, UpdatedAt = started
        };
        db.QaSessions.Add(session);
        await db.SaveChangesAsync(ct);

        var answer = "";
        var asked = false;
        var cards = new List<EvalScoring.Card>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerQuestion);
        try
        {
            var qa = sp.GetRequiredService<IQaService>();
            await foreach (var ev in qa.AskStreamAsync(new QaRequest(session.Id, q.Question, new RetrievalRequest(q.Question), Level: level, LevelForced: level is not null and not "auto"), timeout.Token))
            {
                var p = JsonSerializer.SerializeToElement(ev.Payload, Json);
                switch (ev.Kind)
                {
                    case "delta": answer += Str(p, "text"); break;
                    case "text": answer = Str(p, "text") ?? Str(p, "message") ?? answer; break;
                    case "ask": asked = true; break;
                    case "evidence":
                        cards.AddRange(Cards(p, "used", true));
                        cards.AddRange(Cards(p, "other", false));
                        break;
                    case "sources":
                        if (p.ValueKind == JsonValueKind.Array)
                            cards.AddRange(p.EnumerateArray().Select(s => new EvalScoring.Card(Str(s, "docTitle"), null, null, true)));
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Error = "超过 5 分钟没答完";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Error = ex is DomainRuleException d ? d.Message : "出错：" + ex.GetType().Name;
        }

        var s = EvalScoring.Grade(q.ExpectDocs, q.ExpectProjects, q.ExpectPoints, q.ExpectAsk, answer, cards, asked);
        result.RetrievalOk = result.Error is null ? s.RetrievalOk : s.RetrievalOk is null ? null : false;
        result.AnswerOk = result.Error is null ? s.AnswerOk : s.AnswerOk is null ? null : false;
        result.AskOk = s.AskOk;
        result.MissingPoints = s.MissingPoints;
        result.FoundDocs = s.FoundDocs;
        result.Answer = answer.Length > 4000 ? answer[..4000] : answer;
        result.Seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1);

        // 临时对话连同消息、系统发现的问题一起删掉
        await db.QaSessions.Where(x => x.Id == session.Id).ExecuteDeleteAsync(CancellationToken.None);
        return result;
    }

    private static IEnumerable<EvalScoring.Card> Cards(JsonElement p, string key, bool used)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(c => new EvalScoring.Card(Str(c, "title"), Str(c, "fileName"), Str(c, "projectNo"), used)).ToList()
            : [];

    private static string? Str(JsonElement e, string k)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task FailAsync(Guid runId, string error)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.EvalRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, EvalRunStatus.Failed).SetProperty(r => r.Error, error)
                .SetProperty(r => r.FinishedAt, DateTimeOffset.UtcNow));
    }
}
