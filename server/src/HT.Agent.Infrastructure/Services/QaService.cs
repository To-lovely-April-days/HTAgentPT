using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>问答编排（M4 第 3 步最短链路）：改写 → 检索 → 阈值 → 生成 → 来源 → 留痕。
/// 提示词明确要求仅依据给定内容作答（FR-4.8）；模型从不提供事实，只组织语言。</summary>
public class QaService(
    AppDbContext db,
    IRetrievalService retrieval,
    IChatModelClient chat,
    IRuntimeConfig config,
    IAuditWriter audit,
    ICurrentUser me) : IQaService
{
    public async IAsyncEnumerable<QaEvent> AskStreamAsync(
        QaRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var session = await GetOrCreateSessionAsync(req, ct);
        var historyTurns = await config.GetIntAsync(ConfigKeys.HistoryTurns, 6, ct);
        var history = await db.QaMessages.AsNoTracking()
            .Where(m => m.SessionId == session.Id && m.Answer != null)
            .OrderByDescending(m => m.At).Take(historyTurns)
            .OrderBy(m => m.At)
            .ToListAsync(ct);

        // 查询改写（FR-4.2）：多轮时补全指代；改写结果对用户可见。检索每轮独立执行（FR-4.10）。
        var rewritten = history.Count == 0 ? req.Question : await RewriteAsync(req.Question, history, ct);

        var result = await retrieval.RetrieveAsync(req.Retrieval with { Query = rewritten }, ct);
        yield return new QaEvent("meta", new
        {
            sessionId = session.Id,
            rewrittenQuery = result.RewrittenQuery,
            hitCount = result.Chunks.Count,
            topScore = result.TopScore
        });

        var message = new QaMessage
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            Question = req.Question,
            RewrittenQuery = rewritten == req.Question ? null : rewritten,
            At = DateTimeOffset.UtcNow
        };

        if (!result.AboveThreshold)
        {
            // 未找到就如实说没有（FR-4.7），并列出可能相关的文档名——不硬答
            message.NoResultHints = JsonSerializer.Serialize(result.PossiblyRelatedDocs);
            db.QaMessages.Add(message);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await WriteTraceAsync(message, result, answered: false, ct);
            yield return new QaEvent("no_result", new
            {
                message = "知识库中没有找到足以回答这个问题的内容。",
                possiblyRelatedDocs = result.PossiblyRelatedDocs,
                topScore = result.TopScore
            });
            yield break;
        }

        var prompt = await BuildPromptAsync(req.Question, result, history, ct);
        var answer = new StringBuilder();
        await foreach (var delta in chat.StreamAsync(prompt, ct))
        {
            answer.Append(delta);
            yield return new QaEvent("delta", new { text = delta });
        }

        // 来源标注（FR-4.9）：文档、章节与页码，可展开原文并跳转原件
        var sources = result.Chunks.Select((c, i) => new
        {
            index = i + 1,
            chunkId = c.ChunkId,
            docId = c.DocId,
            docTitle = c.DocTitle,
            section = c.SectionPath,
            pageNo = c.PageNo,
            score = Math.Round(c.RerankScore, 4),
            classification = c.Classification.ToString(),
            excerpt = c.Text.Length <= 200 ? c.Text : c.Text[..200]
        }).ToList();
        yield return new QaEvent("sources", sources);

        message.Answer = answer.ToString();
        message.Sources = JsonSerializer.Serialize(sources);
        db.QaMessages.Add(message);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await WriteTraceAsync(message, result, answered: true, ct);
        yield return new QaEvent("done", new { messageId = message.Id });
    }

    private async Task<QaSession> GetOrCreateSessionAsync(QaRequest req, CancellationToken ct)
    {
        if (req.SessionId is not null)
        {
            var existing = await db.QaSessions
                .FirstOrDefaultAsync(s => s.Id == req.SessionId && s.UserId == me.UserId, ct);
            if (existing is not null) return existing;
        }
        var session = new QaSession
        {
            Id = Guid.NewGuid(),
            UserId = me.UserId,
            CompanyId = me.CompanyId,
            Title = req.Question.Length <= 40 ? req.Question : req.Question[..40],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.QaSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    private async Task<string> RewriteAsync(string question, List<QaMessage> history, CancellationToken ct)
    {
        var turns = new List<ChatTurn>
        {
            new("system", "把用户的最新提问改写为一个不依赖上文、指代完整的独立检索问题。只输出改写后的问题本身，不解释。")
        };
        foreach (var h in history)
        {
            turns.Add(new ChatTurn("user", h.Question));
            if (h.Answer is not null)
                turns.Add(new ChatTurn("assistant", h.Answer.Length <= 300 ? h.Answer : h.Answer[..300]));
        }
        turns.Add(new ChatTurn("user", question));
        try
        {
            var rewritten = (await chat.CompleteAsync(turns, ct)).Trim();
            return rewritten.Length is > 0 and < 500 ? rewritten : question;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return question; // 改写失败不阻断——用原问题检索
        }
    }

    private async Task<IReadOnlyList<ChatTurn>> BuildPromptAsync(
        string question, RetrievalResult result, List<QaMessage> history, CancellationToken ct)
    {
        // 上下文预算按字符近似（中文语料下 token≈字符），超出按重排分从低到高截（E15 文案同源）
        var budget = await config.GetIntAsync(ConfigKeys.ContextTokens, 6000, ct);
        var sb = new StringBuilder();
        sb.AppendLine("你是企业内部知识助手。回答规则：");
        sb.AppendLine("1. 仅依据下方[参考内容]作答，不得补充任何参考内容之外的知识或推测。");
        sb.AppendLine("2. 每个论断句末标注来源编号，如 [1]、[2]。");
        sb.AppendLine("3. 参考内容不足以回答时，直说哪部分回答不了，不要硬答。");
        sb.AppendLine();
        sb.AppendLine("[参考内容]");
        var used = 0;
        for (var i = 0; i < result.Chunks.Count; i++)
        {
            var c = result.Chunks[i];
            var header = $"[{i + 1}] 《{c.DocTitle}》{(c.SectionPath is null ? "" : $" · {c.SectionPath}")}{(c.PageNo is null ? "" : $" · 第 {c.PageNo} 页")}";
            var body = c.Text;
            if (used + body.Length > budget && i > 0) break;
            used += body.Length;
            sb.AppendLine(header);
            sb.AppendLine(body);
            sb.AppendLine();
        }
        var turns = new List<ChatTurn> { new("system", sb.ToString()) };
        foreach (var h in history.TakeLast(4))
        {
            turns.Add(new ChatTurn("user", h.Question));
            if (h.Answer is not null)
                turns.Add(new ChatTurn("assistant", h.Answer.Length <= 500 ? h.Answer : h.Answer[..500]));
        }
        turns.Add(new ChatTurn("user", question));
        return turns;
    }

    /// <summary>会话留痕（FR-4.12）：提问、改写结果、命中分块、生成回答与操作人，写入审计。</summary>
    private Task WriteTraceAsync(QaMessage m, RetrievalResult result, bool answered, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry("qa.ask", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "qa_message", TargetId: m.Id.ToString(),
            Detail: new
            {
                question = m.Question,
                rewritten = m.RewrittenQuery,
                answered,
                topScore = result.TopScore,
                hitChunks = result.Chunks.Select(c => c.ChunkId).ToList(),
                answerLength = m.Answer?.Length ?? 0
            }), ct);
}
