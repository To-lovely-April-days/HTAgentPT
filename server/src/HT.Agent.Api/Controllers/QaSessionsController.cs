using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
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
            .Select(s => new { s.Id, s.Title, s.CreatedAt, s.UpdatedAt })
            .ToListAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Messages(Guid id, CancellationToken ct)
    {
        var owns = await db.QaSessions.AnyAsync(s => s.Id == id && s.UserId == me.UserId, ct);
        if (!owns) return NotFound();
        return Ok(await db.QaMessages.AsNoTracking()
            .Where(m => m.SessionId == id)
            .OrderBy(m => m.At)
            .Select(m => new { m.Id, m.Question, m.RewrittenQuery, m.Answer, m.Sources, m.Payload, m.Intent, m.NoResultHints, m.Helpful, m.At })
            .ToListAsync(ct));
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
            .Select(m => new { m.Id, m.Question, m.Answer, m.Sources, m.Payload, m.At })
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
            turns.Add(new Turn("qa", $"a{m.Id}", "assistant", m.Answer ?? "", m.Sources, m.Payload, null, null, m.At));
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
        string? Sources, string? Payload, Guid? GenSessionId, string? TemplateName, DateTimeOffset At);

    public record FeedbackBody(bool Helpful, string? Reason);

    /// <summary>反馈采集（FR-4.11）：有用 / 无效，无效可填原因。</summary>
    [HttpPost("messages/{messageId:guid}/feedback")]
    public async Task<IActionResult> Feedback(Guid messageId, [FromBody] FeedbackBody body, CancellationToken ct)
    {
        var message = await db.QaMessages.Include(m => m.Session)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null || message.Session!.UserId != me.UserId) return NotFound();
        message.Helpful = body.Helpful;
        message.FeedbackReason = body.Helpful ? null : body.Reason;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
