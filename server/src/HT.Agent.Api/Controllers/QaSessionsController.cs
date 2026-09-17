using HT.Agent.Application.Abstractions;
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
