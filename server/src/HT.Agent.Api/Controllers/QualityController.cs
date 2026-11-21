using System.Text.Json;
using HT.Agent.Api.Quality;
using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using HT.Agent.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

/// <summary>后台「回答质量」：答错的问题（员工反馈 + 系统发现）、测试题与准确率。
/// 只看本公司的；跟语料管理放在一起，同一个权限。</summary>
[ApiController]
[Route("api/quality")]
[Authorize]
[RequirePermission(PermissionKeys.CorpusManage)]
public class QualityController(AppDbContext db, ICurrentUser me, IRuntimeConfig config, IVocabService vocab,
    IAuditWriter audit, EvalRunner runner) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── 概况 ─────────────────────────────────────────

    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] int days = 7, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        var from = DateTimeOffset.UtcNow.AddDays(-days);
        var prevFrom = from.AddDays(-days);
        var msgs = db.QaMessages.AsNoTracking().Where(m => m.Session!.CompanyId == me.CompanyId);
        var answers = await msgs.CountAsync(m => m.At >= from, ct);
        var users = await msgs.Where(m => m.At >= from).Select(m => m.Session!.UserId).Distinct().CountAsync(ct);
        var helpful = await msgs.CountAsync(m => m.At >= from && m.Helpful == true, ct);
        var notHelpful = await msgs.CountAsync(m => m.At >= from && m.Helpful == false, ct);
        var prevNotHelpful = await msgs.CountAsync(m => m.At >= prevFrom && m.At < from && m.Helpful == false, ct);
        var issues = db.AnswerIssues.AsNoTracking().Where(i => i.CompanyId == me.CompanyId);
        var noEvidence = await issues.CountAsync(i => i.CreatedAt >= from && i.Source == IssueSource.NoEvidence, ct);
        var unverified = await issues.CountAsync(i => i.CreatedAt >= from && i.Source == IssueSource.Unverified, ct);
        var open = await issues.CountAsync(i => i.Status == IssueStatus.Open, ct);
        var oldest = await issues.Where(i => i.Status == IssueStatus.Open).MinAsync(i => (DateTimeOffset?)i.CreatedAt, ct);
        return Ok(new { days, answers, users, helpful, notHelpful, prevNotHelpful, noEvidence, unverified, open, oldestOpen = oldest });
    }

    // ── 答错的问题 ───────────────────────────────────

    [HttpGet("issues")]
    public async Task<IActionResult> Issues([FromQuery] string status = "open", [FromQuery] string? source = null,
        [FromQuery] string? reason = null, CancellationToken ct = default)
    {
        var q = db.AnswerIssues.AsNoTracking().Where(i => i.CompanyId == me.CompanyId);
        q = status switch
        {
            "open" => q.Where(i => i.Status == IssueStatus.Open),
            "done" => q.Where(i => i.Status != IssueStatus.Open),
            _ => q
        };
        if (source == "feedback") q = q.Where(i => i.Source == IssueSource.Feedback);
        else if (source == "system") q = q.Where(i => i.Source != IssueSource.Feedback);
        if (!string.IsNullOrEmpty(reason)) q = q.Where(i => i.Reason == reason);
        var rows = await q.OrderByDescending(i => i.CreatedAt).Take(300)
            .Select(i => new
            {
                i.Id, i.Source, i.Reason, i.Category, i.Status, i.CreatedAt, i.EvalQuestionId, i.ReporterId,
                question = i.Message!.Question, askerId = i.Message.Session!.UserId
            })
            .ToListAsync(ct);
        var people = await PeopleAsync(rows.Select(r => r.ReporterId ?? r.askerId), ct);
        return Ok(rows.Select(r =>
        {
            people.TryGetValue(r.ReporterId ?? r.askerId, out var p);
            return new
            {
                r.Id, r.Source, r.Reason, r.Category, r.Status, r.CreatedAt, r.EvalQuestionId, r.question,
                person = p?.Name, dept = p?.Dept
            };
        }));
    }

    [HttpGet("issues/{id:guid}")]
    public async Task<IActionResult> Issue(Guid id, CancellationToken ct)
    {
        var i = await db.AnswerIssues.AsNoTracking().Where(x => x.Id == id && x.CompanyId == me.CompanyId)
            .Select(x => new
            {
                x.Id, x.Source, x.Reason, x.Comment, x.Notify, x.Status, x.Category, x.ResolveNote, x.ResolvedAt, x.CreatedAt,
                x.EvalQuestionId, x.ReporterId, x.MessageId,
                x.Message!.Question, x.Message.Answer, x.Message.Payload, x.Message.At, askerId = x.Message.Session!.UserId
            })
            .FirstOrDefaultAsync(ct);
        if (i is null) return NotFound();
        var threshold = await config.GetDoubleAsync(ConfigKeys.ScoreThreshold, 0.62, ct);
        var people = await PeopleAsync([i.ReporterId ?? i.askerId], ct);
        people.TryGetValue(i.ReporterId ?? i.askerId, out var p);
        return Ok(new
        {
            i.Id, i.Source, i.Reason, i.Comment, i.Notify, i.Status, i.Category, i.ResolveNote, i.ResolvedAt, i.CreatedAt,
            i.EvalQuestionId, i.MessageId, i.Question, i.Answer, answeredAt = i.At,
            payload = ParsePayload(i.Payload), threshold, facts = IssueFacts.From(i.Payload, threshold),
            person = p?.Name, dept = p?.Dept
        });
    }

    public static readonly string[] Categories = ["missing", "parse", "alias", "crowded", "model", "not_issue"];

    public record IssueUpdate(string? Category, string? Status, string? Note);

    [HttpPut("issues/{id:guid}")]
    public async Task<IActionResult> UpdateIssue(Guid id, [FromBody] IssueUpdate body, CancellationToken ct)
    {
        var i = await db.AnswerIssues.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == me.CompanyId, ct);
        if (i is null) return NotFound();
        if (body.Category is not null) i.Category = Categories.Contains(body.Category) ? body.Category : null;
        if (body.Note is not null) i.ResolveNote = body.Note.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 200)] : null;
        if (body.Status is "resolved" or "dismissed" or "open")
        {
            i.Status = body.Status switch { "resolved" => IssueStatus.Resolved, "dismissed" => IssueStatus.Dismissed, _ => IssueStatus.Open };
            i.ResolvedAt = i.Status == IssueStatus.Open ? null : DateTimeOffset.UtcNow;
            i.ResolvedById = i.Status == IssueStatus.Open ? null : me.UserId;
            i.SeenAt = null;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("quality.issue.update", AuditResult.Success, UserId: me.UserId, Username: me.Username,
            CompanyId: me.CompanyId, TargetType: "answer_issue", TargetId: id.ToString(),
            Detail: new { body.Category, body.Status }), ct);
        return NoContent();
    }

    public record AliasBody(string Alias, string Value, string Kind);

    /// <summary>当场加别名：问题里的叫法 → 公司的标准说法。词表里已有这个标准说法就并进它的别名，没有就新建一条。</summary>
    [HttpPost("aliases")]
    [RequirePermission(PermissionKeys.MetaManage)]
    public async Task<IActionResult> AddAlias([FromBody] AliasBody body, CancellationToken ct)
    {
        var key = body.Kind is VocabKeys.CustomerName or VocabKeys.DeviceType ? body.Kind : "common_term";
        var alias = (body.Alias ?? "").Trim();
        var value = (body.Value ?? "").Trim();
        if (alias.Length == 0 || value.Length == 0 || alias == value)
            throw new DomainRuleException("ALIAS_EMPTY", "叫法和标准说法都要填，而且不能一样");
        var term = await db.VocabTerms.AsNoTracking().FirstOrDefaultAsync(v => v.VocabKey == key && v.Value == value, ct);
        if (term is null)
            await vocab.AddAsync(key, value, alias, ct);
        else
        {
            var list = QuestionHints.SplitAliases(term.Aliases).ToList();
            if (!list.Contains(alias)) list.Add(alias);
            await vocab.UpdateAliasesAsync(term.Id, string.Join("、", list), ct);
        }
        return NoContent();
    }

    // ── 测试题 ───────────────────────────────────────

    [HttpGet("eval/questions")]
    public async Task<IActionResult> Questions(CancellationToken ct)
    {
        var qs = await db.EvalQuestions.AsNoTracking().Where(q => q.CompanyId == me.CompanyId && q.IsActive)
            .OrderBy(q => q.Code).ToListAsync(ct);
        var runs = await db.EvalRuns.AsNoTracking().Where(r => r.CompanyId == me.CompanyId && r.Status == EvalRunStatus.Done)
            .OrderByDescending(r => r.StartedAt).Take(6).Select(r => new { r.Id, r.StartedAt }).ToListAsync(ct);
        var runIds = runs.Select(r => r.Id).ToList();
        var results = await db.EvalResults.AsNoTracking().Where(x => runIds.Contains(x.RunId))
            .Select(x => new { x.RunId, x.QuestionId, x.RetrievalOk, x.AnswerOk }).ToListAsync(ct);
        var order = runs.OrderBy(r => r.StartedAt).Select(r => r.Id).ToList();
        return Ok(qs.Select(q => new
        {
            q.Id, q.Code, q.Question, q.ExpectDocs, q.ExpectProjects, q.ExpectPoints, q.ExpectAsk, q.Dept, q.Source, q.IssueId, q.CreatedAt,
            history = order.Select(rid => results.FirstOrDefault(x => x.RunId == rid && x.QuestionId == q.Id))
                .Where(x => x is not null).Select(x => new { retrievalOk = x!.RetrievalOk, answerOk = x.AnswerOk }).ToList()
        }));
    }

    public record QuestionBody(string Question, List<string>? ExpectDocs, List<string>? ExpectProjects, List<string>? ExpectPoints,
        bool ExpectAsk, string? Dept, Guid? IssueId);

    [HttpPost("eval/questions")]
    public async Task<IActionResult> AddQuestion([FromBody] QuestionBody body, CancellationToken ct)
    {
        var q = new EvalQuestion
        {
            Id = Guid.NewGuid(), CompanyId = me.CompanyId, Code = await NextCodeAsync(ct), Question = "",
            Source = body.IssueId is null ? "manual" : "issue", IssueId = body.IssueId, CreatedById = me.UserId, CreatedAt = DateTimeOffset.UtcNow
        };
        Apply(q, body);
        db.EvalQuestions.Add(q);
        if (body.IssueId is Guid iid)
        {
            var issue = await db.AnswerIssues.FirstOrDefaultAsync(i => i.Id == iid && i.CompanyId == me.CompanyId, ct);
            if (issue is not null) issue.EvalQuestionId = q.Id;
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { q.Id, q.Code });
    }

    [HttpPut("eval/questions/{id:guid}")]
    public async Task<IActionResult> UpdateQuestion(Guid id, [FromBody] QuestionBody body, CancellationToken ct)
    {
        var q = await db.EvalQuestions.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == me.CompanyId, ct);
        if (q is null) return NotFound();
        Apply(q, body);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("eval/questions/{id:guid}")]
    public async Task<IActionResult> RemoveQuestion(Guid id, CancellationToken ct)
    {
        var q = await db.EvalQuestions.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == me.CompanyId, ct);
        if (q is null) return NotFound();
        q.IsActive = false;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record ImportBody(string Text);

    /// <summary>批量导入：一行一题的 JSON，格式和 deploy/eval/questions.example.jsonl 一样；// 开头的行跳过。</summary>
    [HttpPost("eval/questions/import")]
    public async Task<IActionResult> Import([FromBody] ImportBody body, CancellationToken ct)
    {
        var added = 0;
        var errors = new List<string>();
        var codes = (await db.EvalQuestions.Where(q => q.CompanyId == me.CompanyId).Select(q => q.Code).ToListAsync(ct)).ToHashSet();
        var lines = (body.Text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//")).ToList();
        if (lines.Count > 1000) throw new DomainRuleException("IMPORT_TOO_MANY", "一次最多导入 1000 题");
        var n = 0;
        foreach (var line in lines)
        {
            n++;
            try
            {
                var o = JsonDocument.Parse(line).RootElement;
                var text = Str(o, "q")?.Trim();
                if (string.IsNullOrEmpty(text)) { errors.Add($"第 {n} 行没有 q（问题）"); continue; }
                var code = Str(o, "id")?.Trim();
                if (string.IsNullOrEmpty(code) || codes.Contains(code)) code = await NextCodeAsync(ct, codes);
                codes.Add(code);
                db.EvalQuestions.Add(new EvalQuestion
                {
                    Id = Guid.NewGuid(), CompanyId = me.CompanyId, Code = code[..Math.Min(code.Length, 32)], Question = text,
                    ExpectDocs = Arr(o, "expect_docs"), ExpectProjects = Arr(o, "expect_projects"), ExpectPoints = Arr(o, "expect_points"),
                    ExpectAsk = o.TryGetProperty("expect_ask", out var ea) && ea.ValueKind == JsonValueKind.True,
                    Dept = Str(o, "dept"), Source = "import", CreatedById = me.UserId, CreatedAt = DateTimeOffset.UtcNow
                });
                added++;
            }
            catch (JsonException) { errors.Add($"第 {n} 行不是合法的 JSON"); }
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { added, errors = errors.Take(20) });
    }

    // ── 跑一遍 ───────────────────────────────────────

    [HttpGet("eval/runs")]
    public async Task<IActionResult> Runs(CancellationToken ct)
        => Ok(await db.EvalRuns.AsNoTracking().Where(r => r.CompanyId == me.CompanyId)
            .OrderByDescending(r => r.StartedAt).Take(20)
            .Select(r => new
            {
                r.Id, r.Status, r.Total, r.Finished, r.RetrievalTotal, r.RetrievalOk, r.AnswerTotal, r.AnswerOk,
                r.RunAs, r.Changes, r.Error, r.StartedAt, r.FinishedAt
            })
            .ToListAsync(ct));

    /// <summary>Level：按哪一档跑（auto / fast / standard / deep，空=和员工一样按默认）。同一批题三档各跑一遍，比准确率和用时。</summary>
    public record StartRunBody(string? Level);

    [HttpPost("eval/runs")]
    public async Task<IActionResult> StartRun([FromBody] StartRunBody? body, CancellationToken ct)
    {
        if (await db.EvalRuns.AnyAsync(r => r.CompanyId == me.CompanyId && r.Status == EvalRunStatus.Running, ct))
            throw new DomainRuleException("EVAL_RUNNING", "上一轮还在跑，等它跑完再来");
        var total = await db.EvalQuestions.CountAsync(q => q.CompanyId == me.CompanyId && q.IsActive, ct);
        if (total == 0) throw new DomainRuleException("EVAL_EMPTY", "还没有测试题。先从错题里存几题，或者导入一批。");

        var runAs = (await config.GetStringAsync(ConfigKeys.QualityEvalUser, "", ct)).Trim();
        if (runAs.Length == 0) runAs = me.Username;
        else if (!await db.Users.AnyAsync(u => u.Username == runAs && u.CompanyId == me.CompanyId && u.IsActive, ct))
            throw new DomainRuleException("EVAL_USER", $"系统设置里的测试账号「{runAs}」不存在或已停用");

        var snapshot = await SnapshotAsync(total, ct);
        var level = HT.Agent.Application.Logic.ThinkLevels.Parse(body?.Level);
        snapshot["eval.level"] = level is null ? "auto" : HT.Agent.Application.Logic.ThinkLevels.Code(level.Value);
        var prev = await db.EvalRuns.AsNoTracking().Where(r => r.CompanyId == me.CompanyId && r.Status == EvalRunStatus.Done && r.Config != null)
            .OrderByDescending(r => r.StartedAt).Select(r => r.Config).FirstOrDefaultAsync(ct);
        var prevSnap = prev is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(prev);
        var run = new EvalRun
        {
            Id = Guid.NewGuid(), CompanyId = me.CompanyId, Total = total, RunAs = runAs, StartedById = me.UserId,
            StartedAt = DateTimeOffset.UtcNow, Config = JsonSerializer.Serialize(snapshot),
            Changes = string.Join("\n", ConfigDiff.Describe(prevSnap, snapshot)) is { Length: > 0 } c ? c : null
        };
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("quality.eval.start", AuditResult.Success, UserId: me.UserId, Username: me.Username,
            CompanyId: me.CompanyId, TargetType: "eval_run", TargetId: run.Id.ToString(), Detail: new { total, runAs }), ct);
        await runner.EnqueueAsync(run.Id);
        return Ok(new { run.Id });
    }

    [HttpGet("eval/runs/{id:guid}")]
    public async Task<IActionResult> Run(Guid id, CancellationToken ct)
    {
        var run = await db.EvalRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == me.CompanyId, ct);
        if (run is null) return NotFound();
        var prevId = await db.EvalRuns.AsNoTracking()
            .Where(r => r.CompanyId == me.CompanyId && r.Status == EvalRunStatus.Done && r.StartedAt < run.StartedAt)
            .OrderByDescending(r => r.StartedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        var results = await db.EvalResults.AsNoTracking().Where(x => x.RunId == id)
            .Join(db.EvalQuestions.AsNoTracking(), x => x.QuestionId, q => q.Id, (x, q) => new
            {
                x.QuestionId, q.Code, q.Question, q.Dept, x.RetrievalOk, x.AnswerOk, x.AskOk, x.MissingPoints, x.FoundDocs,
                x.Answer, x.Error, x.Seconds
            })
            .OrderBy(x => x.Code).ToListAsync(ct);
        var prev = prevId is null ? [] : await db.EvalResults.AsNoTracking().Where(x => x.RunId == prevId)
            .Select(x => new { x.QuestionId, x.RetrievalOk, x.AnswerOk }).ToListAsync(ct);
        var prevBy = prev.GroupBy(x => x.QuestionId).ToDictionary(g => g.Key, g => g.First());
        static bool Pass(bool? r, bool? a) => r != false && a != false;
        return Ok(new
        {
            run.Id, run.Status, run.Total, run.Finished, run.RetrievalTotal, run.RetrievalOk, run.AnswerTotal, run.AnswerOk,
            run.RunAs, run.Changes, run.Error, run.StartedAt, run.FinishedAt, previousRunId = prevId,
            results = results.Select(x =>
            {
                var had = prevBy.TryGetValue(x.QuestionId, out var p);
                var now = Pass(x.RetrievalOk, x.AnswerOk);
                var trend = !had ? "new" : now == Pass(p!.RetrievalOk, p.AnswerOk) ? "same" : now ? "better" : "worse";
                return new { x.QuestionId, x.Code, x.Question, x.Dept, x.RetrievalOk, x.AnswerOk, x.AskOk, x.MissingPoints, x.FoundDocs, x.Answer, x.Error, x.Seconds, trend };
            })
        });
    }

    // ── 小工具 ───────────────────────────────────────

    private record Person(string Name, string? Dept);

    private async Task<Dictionary<Guid, Person>> PeopleAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Department, u.DepartmentId }).ToListAsync(ct);
        var deptIds = users.Where(u => u.DepartmentId != null).Select(u => u.DepartmentId!.Value).Distinct().ToList();
        var depts = await db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        return users.ToDictionary(u => u.Id, u => new Person(u.DisplayName,
            u.DepartmentId is Guid d && depts.TryGetValue(d, out var n) ? n : u.Department));
    }

    private static object? ParsePayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(payload); }
        catch (JsonException) { return null; }
    }

    private static void Apply(EvalQuestion q, QuestionBody b)
    {
        var text = (b.Question ?? "").Trim();
        if (text.Length == 0) throw new DomainRuleException("EVAL_Q_EMPTY", "问题不能为空");
        q.Question = text[..Math.Min(text.Length, 1000)];
        q.ExpectDocs = Clean(b.ExpectDocs);
        q.ExpectProjects = Clean(b.ExpectProjects);
        q.ExpectPoints = Clean(b.ExpectPoints);
        q.ExpectAsk = b.ExpectAsk;
        q.Dept = string.IsNullOrWhiteSpace(b.Dept) ? null : b.Dept.Trim();
    }

    private static List<string> Clean(List<string>? xs)
        => (xs ?? []).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct().Take(20).ToList();

    private async Task<string> NextCodeAsync(CancellationToken ct, HashSet<string>? taken = null)
    {
        taken ??= (await db.EvalQuestions.Where(q => q.CompanyId == me.CompanyId).Select(q => q.Code).ToListAsync(ct)).ToHashSet();
        var max = taken.Select(c => c.StartsWith('Q') && int.TryParse(c[1..], out var v) ? v : 0).DefaultIfEmpty(0).Max();
        return $"Q{max + 1:000}";
    }

    /// <summary>当时的检索配置和资料、别名、术语、测试题数量。和上一次比就知道中间改了什么。</summary>
    private async Task<Dictionary<string, string>> SnapshotAsync(int questions, CancellationToken ct)
    {
        var snap = new Dictionary<string, string>();
        var all = await config.GetAllAsync(ct);
        foreach (var (key, _, count) in ConfigDiff.Items)
            if (!count && all.TryGetValue(key, out var v)) snap[key] = v.Trim('"');
        snap["count.docs"] = (await db.Documents.CountAsync(d => d.ParseStatus == ParseStatus.Parsed && !d.IsWithdrawn, ct)).ToString();
        var aliases = await db.VocabTerms.Where(v => v.IsActive && v.Aliases != null).Select(v => v.Aliases).ToListAsync(ct);
        snap["count.aliases"] = aliases.Sum(a => QuestionHints.SplitAliases(a).Count).ToString();
        snap["count.terms"] = (await db.Terms.CountAsync(t => t.Status == TermStatus.Approved, ct)).ToString();
        snap["count.questions"] = questions.ToString();
        return snap;
    }

    private static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Arr(JsonElement e, string k)
        => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).Take(20).ToList()
            : [];
}
