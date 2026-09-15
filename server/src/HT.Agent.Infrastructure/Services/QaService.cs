using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
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
    IProjectService projects,
    IAuditWriter audit,
    ICurrentUser me) : IQaService
{
    public async IAsyncEnumerable<QaEvent> AskStreamAsync(
        QaRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var session = await GetOrCreateSessionAsync(req, ct);

        // 意图路由（FR-4.1）：台账查询转 M3 结构化返回，生成与翻译转对应模块，
        // 其余进检索流程。判定结果对用户可见（intent 事件），可传 forcedIntent 手动纠正。
        var intent = await RouteIntentAsync(req, ct);
        if (intent != IntentRouter.Knowledge)
        {
            await foreach (var ev in HandleRoutedIntentAsync(intent, req, session, ct))
                yield return ev;
            yield break;
        }

        var historyTurns = await config.GetIntAsync(ConfigKeys.HistoryTurns, 6, ct);
        var history = await db.QaMessages.AsNoTracking()
            .Where(m => m.SessionId == session.Id && m.Answer != null)
            .OrderByDescending(m => m.At).Take(historyTurns)
            .OrderBy(m => m.At)
            .ToListAsync(ct);

        // 查询改写（FR-4.2）两半：多轮补全指代 + 术语表同义注入。改写结果对用户可见。
        var rewritten = history.Count == 0 ? req.Question : await RewriteAsync(req.Question, history, ct);
        var injected = await InjectTermsAsync(rewritten, ct);

        // 单轮最大检索次数（5.1 末段）：改写后的问题拦在阈值下时，退回原始问题再试，
        // 到上限还没过阈值就返回未找到。候选按信息量从高到低排列。
        var maxAttempts = Math.Max(1, await config.GetIntAsync(ConfigKeys.MaxRetrievalPerTurn, 3, ct));
        var candidates = new List<string> { injected };
        if (rewritten != injected) candidates.Add(rewritten);
        if (req.Question != rewritten && req.Question != injected) candidates.Add(req.Question);
        RetrievalResult result = null!;
        foreach (var query in candidates.Take(maxAttempts))
        {
            result = await retrieval.RetrieveAsync(req.Retrieval with { Query = query }, ct);
            if (result.AboveThreshold) break;
        }

        yield return new QaEvent("meta", new
        {
            sessionId = session.Id,
            intent = IntentRouter.Knowledge,
            rewrittenQuery = result.RewrittenQuery,
            hitCount = result.Chunks.Count,
            topScore = result.TopScore,
            notice = result.Notice
        });

        var message = new QaMessage
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            Question = req.Question,
            Intent = IntentRouter.Knowledge,
            RewrittenQuery = result.RewrittenQuery == req.Question ? null : result.RewrittenQuery,
            At = DateTimeOffset.UtcNow
        };

        if (!result.AboveThreshold)
        {
            // 未找到就如实说没有（FR-4.7）；检索降级时明说是降级，不冒充真没有（10.3）
            message.NoResultHints = JsonSerializer.Serialize(result.PossiblyRelatedDocs);
            db.QaMessages.Add(message);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await WriteTraceAsync("qa.ask", message, result, answered: false, ct);
            yield return new QaEvent("no_result", new
            {
                message = result.Notice is null
                    ? "知识库中没有找到足以回答这个问题的内容。"
                    : $"本轮检索处于降级状态（{result.Notice}），未找到不代表知识库中确实没有，请稍后重试。",
                possiblyRelatedDocs = result.PossiblyRelatedDocs,
                topScore = result.TopScore,
                notice = result.Notice
            });
            yield break;
        }

        // 留痕前移（FR-4.12）：检索内容即将出手，先落审计——客户端中途断开也要有据可查
        await WriteTraceAsync("qa.ask", message, result, answered: true, ct);

        var prompt = await BuildPromptAsync(req.Question, result, history, ct);
        var answer = new StringBuilder();
        var completed = false;
        try
        {
            await foreach (var delta in chat.StreamAsync(prompt, ct))
            {
                answer.Append(delta);
                yield return new QaEvent("delta", new { text = delta });
            }

            // 来源标注（FR-4.9）：文档、章节与页码，可展开原文并跳转原件
            var sources = BuildSources(result);
            yield return new QaEvent("sources", sources);

            message.Answer = answer.ToString();
            message.Sources = JsonSerializer.Serialize(sources);
            db.QaMessages.Add(message);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            completed = true;
            yield return new QaEvent("done", new { messageId = message.Id });
        }
        finally
        {
            // 断连兜底：生成已部分完成但未保存时，用不带取消的令牌把残答与来源落库，
            // 会话历史与审计不因客户端断开而出现「有留痕无消息」的缺口
            if (!completed && answer.Length > 0)
            {
                message.Answer = answer + "\n[回答因连接中断而不完整]";
                message.Sources = JsonSerializer.Serialize(BuildSources(result));
                db.QaMessages.Add(message);
                session.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
    }

    private static List<object> BuildSources(RetrievalResult result)
        => result.Chunks.Select((c, i) => (object)new
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

    /// <summary>术语注入（FR-4.2 后半）：命中已审定术语时把对侧语言的说法并入检索词，扩充召回。
    /// 注入结果对用户可见（随改写一起展示）。</summary>
    private async Task<string> InjectTermsAsync(string query, CancellationToken ct)
    {
        var terms = await db.Terms.AsNoTracking()
            .Where(t => t.Status == TermStatus.Approved)
            .Select(t => new { t.Zh, t.En })
            .Take(2000)
            .ToListAsync(ct);
        if (terms.Count == 0) return query;
        var additions = new List<string>();
        foreach (var t in terms)
        {
            if (t.Zh.Length >= 2 && query.Contains(t.Zh) && !query.Contains(t.En, StringComparison.OrdinalIgnoreCase))
                additions.Add(t.En);
            else if (t.En.Length >= 3 && query.Contains(t.En, StringComparison.OrdinalIgnoreCase) && !query.Contains(t.Zh))
                additions.Add(t.Zh);
        }
        if (additions.Count == 0) return query;
        return query + " " + string.Join(' ', additions.Distinct().Take(8));
    }

    private static readonly string[] KnownIntents =
        [IntentRouter.Knowledge, IntentRouter.Ledger, IntentRouter.Generate, IntentRouter.Translate];

    private async Task<string> RouteIntentAsync(QaRequest req, CancellationToken ct)
    {
        string intent;
        if (req.ForcedIntent is not null)
        {
            intent = KnownIntents.Contains(req.ForcedIntent) ? req.ForcedIntent : IntentRouter.Knowledge;
        }
        else
        {
            var customers = await db.VocabTerms.AsNoTracking()
                .Where(v => v.VocabKey == VocabKeys.CustomerName && v.IsActive)
                .Select(v => v.Value).ToListAsync(ct);
            var devices = await db.VocabTerms.AsNoTracking()
                .Where(v => v.VocabKey == VocabKeys.DeviceType && v.IsActive)
                .Select(v => v.Value).ToListAsync(ct);
            var pattern = await config.GetAsync(ConfigKeys.IntentProjectNoPattern, ct);
            intent = IntentRouter.Classify(req.Question, customers, devices, pattern);
        }
        // 权限门在意图确定之后，对自动判定与手动纠正一视同仁（FR-7.3）——
        // forcedIntent 不是权限提升通道。无台账权限者强指台账记一条越权审计后按知识问答走。
        if (intent == IntentRouter.Ledger && !me.Permissions.Contains(PermissionKeys.ProjectSearch))
        {
            if (req.ForcedIntent == IntentRouter.Ledger)
                await audit.WriteAsync(new AuditEntry("authz.denied", AuditResult.Denied,
                    UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                    Detail: new { permission = PermissionKeys.ProjectSearch, via = "qa.forcedIntent" },
                    Ip: me.Ip, TerminalId: me.TerminalId), ct);
            return IntentRouter.Knowledge;
        }
        return intent;
    }

    private async IAsyncEnumerable<QaEvent> HandleRoutedIntentAsync(
        string intent, QaRequest req, QaSession session, [EnumeratorCancellation] CancellationToken ct)
    {
        var message = new QaMessage
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            Question = req.Question,
            Intent = intent,
            At = DateTimeOffset.UtcNow
        };

        if (intent == IntentRouter.Ledger)
        {
            // 纵深防御：路由层已挡过一次，这里再验一次——将来任何新调用路径漏了权限门都会撞在这
            if (!me.Permissions.Contains(PermissionKeys.ProjectSearch))
                throw new ForbiddenException("FORBIDDEN", "你的角色没有项目台账检索权限。本次请求已被记录。");
            // 台账查询以结构化方式返回表格，不经模型生成（FR-3.4/4.1）
            var customers = await db.VocabTerms.AsNoTracking()
                .Where(v => v.VocabKey == VocabKeys.CustomerName && v.IsActive)
                .Select(v => v.Value).ToListAsync(ct);
            var devices = await db.VocabTerms.AsNoTracking()
                .Where(v => v.VocabKey == VocabKeys.DeviceType && v.IsActive)
                .Select(v => v.Value).ToListAsync(ct);
            var f = IntentRouter.ExtractFilters(req.Question, customers, devices);
            int? yearFrom = f.YearFrom, yearTo = f.YearTo;
            if (yearFrom is null && f.RecentYears is not null)
                yearFrom = DateTimeOffset.UtcNow.Year - f.RecentYears + 1;
            var result = await projects.SearchAsync(new ProjectSearchRequest(
                CustomerName: f.CustomerName, YearFrom: yearFrom, YearTo: yearTo, DeviceType: f.DeviceType), ct);

            yield return new QaEvent("meta", new { sessionId = session.Id, intent, hitCount = result.Rows.Count, topScore = 0.0, rewrittenQuery = req.Question });
            yield return new QaEvent("table", new
            {
                intent,
                filters = new { customer = f.CustomerName, deviceType = f.DeviceType, yearFrom, yearTo },
                rows = result.Rows,
                amountVisible = result.AmountVisible,
                note = "台账查询为结构化结果，不经模型生成。筛选条件由提问解析而来，可修改后重查，或用 forcedIntent=knowledge 转知识问答。"
            });
            message.Answer = $"[台账] 按解析出的条件返回 {result.Rows.Count} 条项目记录";
        }
        else
        {
            // 生成与翻译转交对应模块（FR-4.1）：给出跳转指令，由前端切到对应工作区
            var target = intent == IntentRouter.Generate ? "generate" : "translate";
            var name = intent == IntentRouter.Generate ? "方案生成" : "翻译";
            yield return new QaEvent("meta", new { sessionId = session.Id, intent, hitCount = 0, topScore = 0.0, rewrittenQuery = req.Question });
            yield return new QaEvent("redirect", new
            {
                intent,
                module = target,
                message = $"这个请求更适合在「{name}」里完成，已为你准备切换。判定有误可用 forcedIntent=knowledge 按知识问答处理。"
            });
            message.Answer = $"[分流] 已转交{name}模块";
        }

        db.QaMessages.Add(message);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("qa.ask", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "qa_message", TargetId: message.Id.ToString(),
            Detail: new { question = req.Question, intent, forced = req.ForcedIntent is not null }), ct);
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

    /// <summary>会话留痕（FR-4.12）：提问、改写结果、命中分块与操作人。
    /// 在检索内容出手之前写入——客户端断连不能抹掉「谁问了什么、命中了哪些块」。</summary>
    private Task WriteTraceAsync(string action, QaMessage m, RetrievalResult result, bool answered, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "qa_message", TargetId: m.Id.ToString(),
            Detail: new
            {
                question = m.Question,
                rewritten = m.RewrittenQuery,
                answered,
                topScore = result.TopScore,
                notice = result.Notice,
                hitChunks = result.Chunks.Select(c => c.ChunkId).ToList()
            }), ct);
}
