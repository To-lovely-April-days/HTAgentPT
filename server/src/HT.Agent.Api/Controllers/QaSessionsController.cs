using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Controllers;

/// <summary>会话历史（FR-4.10）与反馈（FR-4.11）。只碰自己的会话。</summary>
[ApiController]
[Route("api/qa-sessions")]
[Authorize]
public class QaSessionsController(AppDbContext db, ICurrentUser me) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await db.QaSessions.AsNoTracking()
            .Where(s => s.UserId == me.UserId)
            .OrderByDescending(s => s.UpdatedAt).Take(100)
            .Select(s => new { s.Id, s.Title, s.CreatedAt, s.UpdatedAt, s.ProjectId })
            .ToListAsync(ct));

    public record ProjectBody(Guid? ProjectId);

    /// <summary>把对话放进项目、或移出来（ProjectId 为空）。</summary>
    [HttpPut("{id:guid}/project")]
    public async Task<IActionResult> SetProject(Guid id, [FromBody] ProjectBody b, CancellationToken ct)
    {
        var s = await db.QaSessions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.UserId, ct);
        if (s is null) return NotFound();
        if (b.ProjectId is { } pid && !await db.ChatProjects.AnyAsync(p => p.Id == pid && p.OwnerId == me.UserId && p.ArchivedAt == null, ct))
            return NotFound();
        s.ProjectId = b.ProjectId;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>存给助手自己用的现场（等用户确认时的 pending、做到一半存的 checkpoint：整段对话、系统提示词、查到的原文）不发给界面：又大又用不上。</summary>
    private static string? ForClient(string? payload)
    {
        if (payload is null || !(payload.Contains("\"pending\"") || payload.Contains("\"checkpoint\""))) return payload;
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(payload) is not System.Text.Json.Nodes.JsonObject o) return payload;
            if (o["pending"] is not null) { o.Remove("pending"); o["waiting"] = true; }
            o.Remove("checkpoint");
            return o.ToJsonString();
        }
        catch (System.Text.Json.JsonException) { return payload; }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Messages(Guid id, CancellationToken ct)
    {
        var owns = await db.QaSessions.AnyAsync(s => s.Id == id && s.UserId == me.UserId, ct);
        if (!owns) return NotFound();
        var rows = await db.QaMessages.AsNoTracking()
            .Where(m => m.SessionId == id)
            .OrderBy(m => m.At)
            .Select(m => new { m.Id, m.Question, m.RewrittenQuery, m.Answer, m.Sources, m.Payload, m.Intent, m.NoResultHints, m.Helpful, m.At })
            .ToListAsync(ct);
        return Ok(rows.Select(m => m with { Payload = ForClient(m.Payload) }));
    }

    /// <summary>一条对话的完整时间线：问答的轮次 + 这条对话里起过的生成会话的轮次，按时间并成一串。
    /// 选完模板之后的填写过程存在生成会话里，不并进来的话，打开历史只能看到「递出模板」那一步，
    /// 后面填了什么全看不见。旧的 {id} 端点保持原样，逐项核对台还在用。</summary>
    [HttpGet("{id:guid}/timeline")]
    public async Task<IActionResult> Timeline(Guid id, CancellationToken ct)
    {
        var owns = await db.QaSessions.AnyAsync(s => s.Id == id && s.UserId == me.UserId, ct);
        if (!owns) return NotFound();

        var qa = await db.QaMessages.AsNoTracking()
            .Where(m => m.SessionId == id).OrderBy(m => m.At)
            .Select(m => new { m.Id, m.Question, m.Answer, m.Sources, m.Payload, m.Intent, m.At, m.Helpful, m.FeedbackReason })
            .ToListAsync(ct);

        // 这条对话里起过的生成会话（可能不止一个：先出任务单，再出报价）
        var gens = await db.GenerationSessions.AsNoTracking()
            .Where(g => g.QaSessionId == id && g.CreatedById == me.UserId)
            .Join(db.Templates.AsNoTracking(), g => g.TemplateId, t => t.Id,
                (g, t) => new { g.Id, g.Status, g.CreatedAt, TemplateName = t.Name })
            .ToListAsync(ct);
        var genIds = gens.Select(g => g.Id).ToList();
        var genMsgs = genIds.Count == 0 ? [] : await db.GenChatMessages.AsNoTracking()
            .Where(m => genIds.Contains(m.SessionId)).OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.SessionId, m.Role, m.Content, m.Payload, m.At })
            .ToListAsync(ct);

        var turns = new List<Turn>();
        foreach (var m in qa)
        {
            turns.Add(new Turn("qa", $"q{m.Id}", "user", m.Question, null, null, null, null, m.At));
            turns.Add(new Turn("qa", $"a{m.Id}", "assistant", m.Answer ?? "", m.Sources, ForClient(m.Payload), null, null, m.At, m.Intent,
                m.Id, m.Helpful, m.FeedbackReason));
        }
        var nameOf = gens.ToDictionary(g => g.Id, g => g.TemplateName);
        foreach (var m in genMsgs)
            turns.Add(new Turn("gen", $"g{m.Id}", m.Role, m.Content, null, m.Payload,
                m.SessionId, nameOf[m.SessionId], m.At));

        // 未完成的那一份可以接着填——已经生成完的就只是历史，不再把人拉回填写态
        var resume = gens.Where(g => g.Status != GenerationStatus.Completed)
            .OrderByDescending(g => g.CreatedAt).FirstOrDefault();

        return Ok(new
        {
            turns = turns.OrderBy(t => t.At).ToList(),
            resume = resume is null ? null : new { sessionId = resume.Id, templateName = resume.TemplateName }
        });
    }

    /// <summary>时间线上的一轮。问答与生成两边的消息并成同一种形状，前端按 kind 分别还原。</summary>
    public record Turn(string Kind, string Id, string Role, string Text,
        string? Sources, string? Payload, Guid? GenSessionId, string? TemplateName, DateTimeOffset At,
        string? Intent = null, Guid? MessageId = null, bool? Helpful = null, string? FeedbackReason = null);

    /// <summary>「哪里不对」的选项。旧问答页传的是一句话原因，对不上选项的当成补充说明、原因记「其他」。</summary>
    public static readonly string[] Reasons = ["no_docs", "wrong_docs", "wrong", "incomplete", "other"];

    public record FeedbackBody(bool Helpful, string? Reason, string? Comment = null, bool Notify = false);

    /// <summary>反馈采集（FR-4.11）：有帮助 / 没帮上。没帮上的进后台「回答质量」等管理员处理；
    /// 改回「有帮助」就撤掉还没处理的那条。</summary>
    [HttpPost("messages/{messageId:guid}/feedback")]
    public async Task<IActionResult> Feedback(Guid messageId, [FromBody] FeedbackBody body, CancellationToken ct)
    {
        var message = await db.QaMessages.Include(m => m.Session)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null || message.Session!.UserId != me.UserId) return NotFound();
        var reason = body.Reason is not null && Reasons.Contains(body.Reason) ? body.Reason : body.Helpful ? null : "other";
        var comment = (body.Reason is not null && !Reasons.Contains(body.Reason) ? body.Reason : body.Comment)?.Trim();
        if (comment is { Length: > 500 }) comment = comment[..500];
        message.Helpful = body.Helpful;
        message.FeedbackReason = body.Helpful ? null : reason;

        var issue = await db.AnswerIssues.FirstOrDefaultAsync(i => i.MessageId == messageId && i.Source == IssueSource.Feedback, ct);
        if (body.Helpful)
        {
            if (issue is { Status: IssueStatus.Open }) db.AnswerIssues.Remove(issue);
        }
        else
        {
            if (issue is null)
            {
                issue = new AnswerIssue
                {
                    Id = Guid.NewGuid(), CompanyId = message.Session.CompanyId, MessageId = messageId,
                    Source = IssueSource.Feedback, ReporterId = me.UserId, CreatedAt = DateTimeOffset.UtcNow
                };
                db.AnswerIssues.Add(issue);
            }
            issue.Reason = reason;
            issue.Comment = string.IsNullOrEmpty(comment) ? null : comment;
            issue.Notify = body.Notify;
            if (issue.Status != IssueStatus.Open) { issue.Status = IssueStatus.Open; issue.ResolvedAt = null; issue.SeenAt = null; }
        }
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>我反馈过、已经处理好、还没看过的（首页提示「处理好了」）。两周以前的不再提。</summary>
    [HttpGet("feedback/resolved")]
    public async Task<IActionResult> Resolved(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-14);
        return Ok(await db.AnswerIssues.AsNoTracking()
            .Where(i => i.ReporterId == me.UserId && i.Notify && i.Status == IssueStatus.Resolved && i.SeenAt == null && i.ResolvedAt >= since)
            .OrderByDescending(i => i.ResolvedAt).Take(3)
            .Select(i => new { i.Id, question = i.Message!.Question, note = i.ResolveNote, i.ResolvedAt })
            .ToListAsync(ct));
    }

    [HttpPost("feedback/{issueId:guid}/seen")]
    public async Task<IActionResult> Seen(Guid issueId, CancellationToken ct)
    {
        var issue = await db.AnswerIssues.FirstOrDefaultAsync(i => i.Id == issueId && i.ReporterId == me.UserId, ct);
        if (issue is null) return NotFound();
        issue.SeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
