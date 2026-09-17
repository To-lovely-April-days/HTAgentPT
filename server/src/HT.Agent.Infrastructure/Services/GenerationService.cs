using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using HT.Agent.Infrastructure.Templates;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>文档生成（M5）：按槽位逐项填充，先基准预填、缺口转提问、建议只带依据。
/// 来源优先级（5.2.2）：模板固定 > 继承 > AI 建议 > 用户填写，前序已填不被后序覆盖，
/// 用户主动修改除外。槽位取值整会话序列化存储（7.2）。</summary>
public class GenerationService(
    AppDbContext db,
    IFileStorage storage,
    IRetrievalService retrieval,
    IChatModelClient chat,
    IHttpClientFactory httpFactory,
    IRuntimeConfig config,
    IAuditWriter audit,
    ICurrentUser me) : IGenerationService
{
    private static readonly JsonSerializerOptions SlotJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // ── 会话 ─────────────────────────────────────────────

    public async Task<SessionView> CreateSessionAsync(Guid templateId, string? projectHint, CancellationToken ct = default)
    {
        var template = await db.Templates.AsNoTracking().Include(t => t.Slots)
            .FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw new DomainRuleException("TEMPLATE_NOT_FOUND", "模板不存在");
        if (!template.IsEnabled)
            throw new DomainRuleException("TEMPLATE_DISABLED", "模板未启用（槽位定义不完整的模板无法进入交互流程）");

        var states = template.Slots.OrderBy(s => s.SortOrder)
            .Select(s => new SlotState(s.Tag, null, null, false, false, null, null)).ToList();
        var session = new GenerationSession
        {
            Id = Guid.NewGuid(),
            TemplateId = templateId,
            ProjectHint = projectHint,
            SlotValues = JsonSerializer.Serialize(states, SlotJson),
            CreatedById = me.UserId,
            CompanyId = me.CompanyId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.GenerationSessions.Add(session);
        await db.SaveChangesAsync(ct);
        await Log("gen.session_create", session, new { template.Name, projectHint }, ct);
        return await ViewAsync(session, template, ct);
    }

    public async Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await FindAsync(sessionId, ct);
        if (session is null) return null;
        var template = await db.Templates.AsNoTracking().Include(t => t.Slots)
            .FirstAsync(t => t.Id == session.TemplateId, ct);
        return await ViewAsync(session, template, ct);
    }

    public async Task<IReadOnlyList<SessionRow>> ListSessionsAsync(CancellationToken ct = default)
    {
        var sessions = await db.GenerationSessions.AsNoTracking().Include(s => s.Template)
            .Where(s => s.CreatedById == me.UserId)
            .OrderByDescending(s => s.UpdatedAt).Take(100).ToListAsync(ct);
        var templates = await db.Templates.AsNoTracking().Include(t => t.Slots)
            .Where(t => sessions.Select(x => x.TemplateId).Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);
        // 进度口径与完成校验一致（FR-5.13）：后续阶段槽位不进分母
        return sessions.Select(s =>
        {
            var view = Completeness(templates[s.TemplateId], Parse(s.SlotValues));
            return new SessionRow(s.Id, s.Template!.Name, s.BaseProjectNo, s.Status, view.Total, view.Done, s.UpdatedAt);
        }).ToList();
    }

    // ── 基准项目（FR-5.4/5.5/5.6/5.7）──────────────────────

    public async Task<IReadOnlyList<BaseCandidate>> GetCandidatesAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await MustFindAsync(sessionId, ct);
        var hint = session.ProjectHint ?? "";
        var devices = await db.VocabTerms.AsNoTracking()
            .Where(v => v.VocabKey == VocabKeys.DeviceType && v.IsActive).Select(v => v.Value).ToListAsync(ct);
        var customers = await db.VocabTerms.AsNoTracking()
            .Where(v => v.VocabKey == VocabKeys.CustomerName && v.IsActive).Select(v => v.Value).ToListAsync(ct);
        var device = devices.Where(d => hint.Contains(d)).OrderByDescending(d => d.Length).FirstOrDefault();
        var customer = customers.Where(c => hint.Contains(c)).OrderByDescending(c => c.Length).FirstOrDefault();
        // 要点里的客户只作排序偏好（同客户优先），不做硬筛——要点常写得随意
        return await QueryCandidatesAsync(session, customer, customerStrict: false, device, keyword: null, take: 3, ct);
    }

    public async Task<IReadOnlyList<BaseCandidate>> FindCandidatesAsync(Guid sessionId, string? customer,
        string? deviceType, string? keyword, int take, CancellationToken ct = default)
    {
        var session = await MustFindAsync(sessionId, ct);
        return await QueryCandidatesAsync(session, customer, customerStrict: customer is not null, deviceType, keyword,
            Math.Clamp(take, 1, 8), ct);
    }

    private async Task<IReadOnlyList<BaseCandidate>> QueryCandidatesAsync(GenerationSession session,
        string? customer, bool customerStrict, string? device, string? keyword, int take, CancellationToken ct)
    {
        var template = await db.Templates.AsNoTracking().Include(t => t.Slots)
            .FirstAsync(t => t.Id == session.TemplateId, ct);

        var q = db.Projects.AsNoTracking().Where(p => p.CompanyId == me.CompanyId);
        if (device is not null) q = q.Where(p => p.DeviceType == device);
        if (customer is not null && customerStrict) q = q.Where(p => p.CustomerName == customer);
        var projects = await q
            .OrderByDescending(p => customer != null && p.CustomerName == customer)
            .ThenByDescending(p => keyword != null &&
                ((p.DeviceModel != null && p.DeviceModel.Contains(keyword)) ||
                 (p.SpecParams != null && p.SpecParams.Contains(keyword))))
            .ThenByDescending(p => p.Year).ThenByDescending(p => p.UpdatedAt)
            .Take(take).ToListAsync(ct);

        // 「可继承 N 项」要是真数字：按台账字段规则能取到的，加上从该项目资料里按字段名真能抽到的。
        // 数不准还不如不显示——选基准的人就是按这个数决定用不用它
        var inheritable = template.Slots
            .Where(s => !s.ForbidInherit && s.Stage == SlotStage.Current).ToList();
        var labels = template.Slots.Select(s => s.Name).ToList();
        var projectNos = projects.Select(p => p.ProjectNo).ToList();
        var docTexts = await ProjectChunkTextsAsync(projectNos, ct);

        var result = new List<BaseCandidate>();
        foreach (var p in projects)
        {
            var linkedDocs = await db.DocMetadatas.AsNoTracking()
                .CountAsync(m => m.ProjectNo == p.ProjectNo &&
                    db.Documents.Any(d => d.Id == m.DocumentId && d.ParseStatus == ParseStatus.Parsed), ct);
            var texts = docTexts.GetValueOrDefault(p.ProjectNo) ?? [];
            var count = inheritable.Count(s =>
                (s.SuggestSource ?? "").StartsWith("project.", StringComparison.Ordinal)
                    ? ProjectField(p, s.SuggestSource!["project.".Length..]) is not null
                    : FormFieldExtractor.ExtractFirst(texts, s.Name, s.Unit, labels, s.Section) is not null);
            result.Add(new BaseCandidate(p.ProjectNo, p.CustomerName, p.Year, p.DeviceType, p.DeviceModel,
                p.SpecParams, p.DeliveryStatus?.ToString(), count, linkedDocs));
        }
        return result;
    }

    /// <summary>取这些项目关联文档的分块文本（只取调用者可及密级——继承与候选计数都不是越权通道）。</summary>
    private async Task<Dictionary<string, List<string>>> ProjectChunkTextsAsync(
        List<string> projectNos, CancellationToken ct)
    {
        if (projectNos.Count == 0) return [];
        var rows = await db.Chunks.AsNoTracking()
            .Where(c => c.IsActive)
            .Join(db.DocMetadatas.AsNoTracking().Where(m => projectNos.Contains(m.ProjectNo!)),
                c => c.DocId, m => m.DocumentId, (c, m) => new { m.ProjectNo, c.Text, c.DocId })
            .Join(db.Documents.AsNoTracking().Where(d => d.ParseStatus == ParseStatus.Parsed),
                x => x.DocId, d => d.Id, (x, d) => new { x.ProjectNo, x.Text, d.Classification })
            .ToListAsync(ct);
        return rows
            .Where(x => me.Classifications.Contains(x.Classification) && x.ProjectNo is not null)
            .GroupBy(x => x.ProjectNo!)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Text).ToList());
    }

    public async Task<SessionView> SetBaseProjectAsync(Guid sessionId, string? projectNo, CancellationToken ct = default)
    {
        var session = await MustFindAsync(sessionId, ct);
        var template = await db.Templates.AsNoTracking().Include(t => t.Slots)
            .FirstAsync(t => t.Id == session.TemplateId, ct);
        if (projectNo is not null &&
            !await db.Projects.AnyAsync(p => p.ProjectNo == projectNo && p.CompanyId == me.CompanyId, ct))
            throw new DomainRuleException("PROJECT_NOT_FOUND", $"项目 {projectNo} 不在台账中");

        var states = Parse(session.SlotValues);
        // 更换基准（FR-5.5）：用户确认/填写过的保留，其余清掉重新预填
        var kept = states.Select(s => s.UserTouched
            ? s
            : new SlotState(s.Tag, null, null, false, false, null, null)).ToList();

        session.BaseProjectNo = projectNo;
        var prefilled = projectNo is null ? kept : await PrefillAsync(kept, template, projectNo, ct);
        session.SlotValues = JsonSerializer.Serialize(prefilled, SlotJson);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await Log("gen.set_base", session, new { projectNo }, ct);
        return await ViewAsync(session, template, ct);
    }

    /// <summary>继承预填（FR-5.6）：台账结构化字段 + 关联文档对应章节。
    /// 章节匹配依次尝试：精确标题 → 标题关键词 → 章节序号；均不命中视为未填。
    /// 每项记录来源项目编号与出处。禁止继承的槽位直接跳过（FR-5.7）。</summary>
    private async Task<List<SlotState>> PrefillAsync(
        List<SlotState> states, Template template, string projectNo, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.ProjectNo == projectNo, ct);
        var defs = template.Slots.ToDictionary(s => s.Tag);
        // 同模板的字段名：用来认出表头行——右邻也是字段名的话那是列名，不是取值
        var labels = template.Slots.Select(s => s.Name).ToList();
        // 基准项目关联文档的分块（仅调用者可及密级——继承不是越权通道）
        var chunks = await db.Chunks.AsNoTracking()
            .Where(c => c.IsActive && db.DocMetadatas.Any(m => m.ProjectNo == projectNo && m.DocumentId == c.DocId))
            .Join(db.Documents.AsNoTracking(), c => c.DocId, d => d.Id, (c, d) => new
            { c.SectionPath, c.Text, DocTitle = d.Title, d.Classification, Category = d.Metadata!.DocCategory, d.ParseStatus })
            .Where(x => x.ParseStatus == ParseStatus.Parsed)
            .ToListAsync(ct);
        chunks = chunks.Where(x => me.Classifications.Contains(x.Classification)).ToList();

        var result = new List<SlotState>();
        foreach (var s in states)
        {
            if (!defs.TryGetValue(s.Tag, out var def)) { result.Add(s); continue; }
            if (s.UserTouched || s.Value is not null || def.ForbidInherit || def.Stage == SlotStage.Later)
            { result.Add(s); continue; }

            var src = def.SuggestSource ?? "";
            if (src.StartsWith("project.", StringComparison.Ordinal))
            {
                var value = ProjectField(project, src["project.".Length..]);
                result.Add(value is null ? s : s with
                {
                    Value = value,
                    Source = SlotFillSource.Inherited,
                    Origin = $"{projectNo} · 台账.{src["project.".Length..]}",
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                continue;
            }
            if (src.StartsWith("doc:", StringComparison.Ordinal))
            {
                var body = src["doc:".Length..];
                var sep = body.IndexOf('#');
                var category = sep < 0 ? body : body[..sep];
                var rule = sep < 0 ? def.Name : body[(sep + 1)..];
                var pool = chunks.Where(c => category.Length == 0 || c.Category == category).ToList();
                var hit = pool.FirstOrDefault(c => LastSegment(c.SectionPath) == rule)             // 精确标题
                          ?? pool.FirstOrDefault(c => (c.SectionPath ?? "").Contains(rule))        // 标题关键词
                          ?? pool.FirstOrDefault(c => LastSegment(c.SectionPath).StartsWith(rule)); // 章节序号前缀
                result.Add(hit is null ? s : s with
                {
                    Value = StripHeadingPrefix(hit.Text),
                    Source = SlotFillSource.Inherited,
                    Origin = $"{projectNo} ·《{hit.DocTitle}》· {LastSegment(hit.SectionPath)}",
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                continue;
            }
            // 没配来源规则：按槽位显示名到基准项目的资料里找同名字段。
            // 历史任务单、参数表本来就是「字段名 + 取值」的形态，这才是「拿历史项目做基准」的常态；
            // 要求管理员先给几十个槽位逐个配规则，继承就永远是 0 项。
            // 资料常被切成多块（一份任务单就分了两块）：跨块比分取最贴的一处，
            // 不能碰到哪块算哪块——设备表与配件表都有「名称」列，先遇到的未必是要的那张
            var byName = chunks
                .Select(c => new { c, Hit = FormFieldExtractor.ExtractScored(c.Text, def.Name, def.Unit, labels, def.Section) })
                .Where(x => x.Hit is not null)
                .OrderByDescending(x => x.Hit!.Value.Score)
                .FirstOrDefault();
            result.Add(byName is null ? s : s with
            {
                Value = byName.Hit!.Value.Value,
                Source = SlotFillSource.Inherited,
                Origin = $"{projectNo} ·《{byName.c.DocTitle}》· {def.Name}",
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        return result;
    }

    // ── 槽位读写与建议（FR-5.8 至 5.12）────────────────────────

    public async Task<SlotState> PutSlotAsync(Guid sessionId, string tag, SlotPut put, CancellationToken ct = default)
    {
        var session = await MustFindAsync(sessionId, ct);
        var states = Parse(session.SlotValues);
        var idx = states.FindIndex(s => s.Tag == tag);
        if (idx < 0) throw new DomainRuleException("SLOT_NOT_FOUND", $"槽位 {tag} 不存在");
        var s = states[idx];

        if (put.AdoptSuggestion)
        {
            if (s.Source != SlotFillSource.AiSuggested || s.Value is null)
                throw new DomainRuleException("NO_SUGGESTION", "该槽位当前没有待采纳的建议——先请求建议");
            states[idx] = s with { Confirmed = true, UserTouched = true, UpdatedAt = DateTimeOffset.UtcNow };
        }
        else if (put.Value is not null)
        {
            // 用户填写/主动修改：允许覆盖任何前序来源（5.2.2 的唯一例外）
            states[idx] = s with
            {
                Value = put.Value,
                Source = SlotFillSource.Confirmed,
                Confirmed = true,
                UserTouched = true,
                Origin = "用户填写",
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
        else if (put.Confirm)
        {
            if (s.Value is null)
                throw new DomainRuleException("NOTHING_TO_CONFIRM", "空槽位没有可确认的值");
            states[idx] = s with { Confirmed = true, UserTouched = true, UpdatedAt = DateTimeOffset.UtcNow };
        }
        else
        {
            // 清空
            states[idx] = new SlotState(tag, null, null, false, true, null, DateTimeOffset.UtcNow);
        }
        session.SlotValues = JsonSerializer.Serialize(states, SlotJson);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return states[idx];
    }

    public async Task<SlotSuggestion> SuggestAsync(Guid sessionId, string tag, string? hint = null, CancellationToken ct = default)
    {
        var session = await MustFindAsync(sessionId, ct);
        var template = await db.Templates.AsNoTracking().Include(t => t.Slots)
            .FirstAsync(t => t.Id == session.TemplateId, ct);
        var def = template.Slots.FirstOrDefault(s => s.Tag == tag)
            ?? throw new DomainRuleException("SLOT_NOT_FOUND", $"槽位 {tag} 不存在");
        if (def.Stage == SlotStage.Later)
            throw new DomainRuleException("SLOT_LATER_STAGE", "后续阶段槽位不提问、不预填、不给建议（表 5-2）");

        // 项目专属信息不从历史资料带值（FR-5.7）：给个来自别的项目的合同编号或客户名，
        // 比不给更糟——用户还得逐条核对是不是串了项目
        if (def.ForbidInherit)
            return new SlotSuggestion(tag, null, [], false,
                $"「{def.Name}」属项目专属信息（客户、编号、日期、金额、联系人一类），按规则不从历史资料带值，请直接填写。");

        var states = Parse(session.SlotValues);
        var state = states.First(s => s.Tag == tag);

        // 长段落且已有继承内容：建议 = 基准章节的受限改写（FR-5.11），不改技术结论
        if (def.DataType == SlotDataType.LongText &&
            state is { Source: SlotFillSource.Inherited, Value: not null })
        {
            var rewritten = await RewriteInheritedAsync(state.Value, session.ProjectHint, ct);
            var suggestion = new SlotSuggestion(tag, rewritten,
                [new SuggestEvidence($"基准项目 {session.BaseProjectNo}", state.Origin, null,
                    state.Value.Length <= 200 ? state.Value : state.Value[..200], null)],
                true, "基于基准项目对应章节改写：仅替换已变更的参数与表述，技术结论未动（FR-5.11），请核对");
            await StoreSuggestionAsync(session, states, tag, suggestion, ct);
            return suggestion;
        }

        // 检索建议（FR-5.9）：附依据；无检索结果就明说（FR-5.10），绝不凭常识编
        var query = $"{def.Name} {def.Section} {hint ?? ""} {session.ProjectHint ?? ""} {template.DocType}".Trim();
        var result = await retrieval.RetrieveAsync(new RetrievalRequest(query), ct);
        if (!result.AboveThreshold || result.Chunks.Count == 0)
        {
            return new SlotSuggestion(tag, null, [], false,
                "暂无可参考数据：知识库中没有检索到足以支撑建议的内容，请自行填写（FR-5.10）");
        }
        var top = result.Chunks[0];
        // 命中的块常是整张表（「| 合同编号 | … | 下单日期 | … |」），整块或首行都不是这一项的值——
        // 先按字段名把该项取出来，取不到才退回按行挑
        var byField = def.DataType is SlotDataType.LongText
            ? null
            : FormFieldExtractor.ExtractFirst(result.Chunks.Select(c => c.Text), def.Name, def.Unit,
                template.Slots.Select(s => s.Name).ToList(), def.Section);
        var value = byField ?? (def.DataType is SlotDataType.LongText
            ? StripHeadingPrefix(top.Text)
            : BestLine(StripHeadingPrefix(top.Text), def.Name));
        var evidence = result.Chunks.Take(3)
            .Select(c => new SuggestEvidence(c.DocTitle, c.SectionPath, c.PageNo,
                c.Text.Length <= 200 ? c.Text : c.Text[..200], c.ChunkId)).ToList();
        var sug = new SlotSuggestion(tag, value, evidence, true,
            $"建议值取自 {evidence.Count} 处检索命中（重排分 {result.TopScore:F2}），采纳前请核对依据");
        await StoreSuggestionAsync(session, states, tag, sug, ct);
        return sug;
    }

    private async Task StoreSuggestionAsync(GenerationSession session, List<SlotState> states,
        string tag, SlotSuggestion sug, CancellationToken ct)
    {
        var idx = states.FindIndex(s => s.Tag == tag);
        var s = states[idx];
        // 前序来源已填的不被建议覆盖（5.2.2）——建议只落到空槽位；已填槽位的建议仅随响应返回
        if (s.Value is null && sug.Value is not null)
        {
            states[idx] = s with
            {
                Value = sug.Value,
                Source = SlotFillSource.AiSuggested,
                Confirmed = false,
                Origin = string.Join("；", sug.Evidence.Take(2).Select(e => e.SourceTitle)),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            session.SlotValues = JsonSerializer.Serialize(states, SlotJson);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        await Log("gen.suggest", session, new { tag, hasEvidence = sug.HasEvidence }, ct);
    }

    private async Task<string> RewriteInheritedAsync(string baseText, string? hint, CancellationToken ct)
    {
        try
        {
            return (await chat.CompleteAsync(
            [
                new ChatTurn("system",
                    "把参考段落改写为适用于新项目的版本。规则：只替换与新项目要点明显不一致的参数与表述；" +
                    "不得改变技术结论、不得增删技术要求；拿不准的地方保持原文。只输出改写后的段落。"),
                new ChatTurn("user", $"[新项目要点]\n{hint ?? "（未提供）"}\n\n[参考段落]\n{baseText}")
            ], ct)).Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return baseText; // 模型不可用：原文返回，用户自行改（不丢内容）
        }
    }

    public async Task<string> SlotChatAsync(Guid sessionId, string tag, string question, CancellationToken ct = default)
    {
        var session = await MustFindAsync(sessionId, ct);
        var template = await db.Templates.AsNoTracking().Include(t => t.Slots)
            .FirstAsync(t => t.Id == session.TemplateId, ct);
        var def = template.Slots.FirstOrDefault(s => s.Tag == tag)
            ?? throw new DomainRuleException("SLOT_NOT_FOUND", $"槽位 {tag} 不存在");

        // 单项追问（FR-5.12）：就地检索+回答，不写会话槽位状态
        var result = await retrieval.RetrieveAsync(
            new RetrievalRequest($"{def.Name} {question} {session.ProjectHint ?? ""}"), ct);
        if (!result.AboveThreshold)
            return "知识库中没有找到相关内容，无法回答这个追问。";
        var sb = new StringBuilder();
        sb.AppendLine($"围绕槽位「{def.Name}」回答用户追问。仅依据[参考内容]作答，句末标注来源编号。");
        sb.AppendLine("[参考内容]");
        for (var i = 0; i < Math.Min(4, result.Chunks.Count); i++)
            sb.AppendLine($"[{i + 1}]《{result.Chunks[i].DocTitle}》：{result.Chunks[i].Text}");
        var answer = await chat.CompleteAsync(
            [new ChatTurn("system", sb.ToString()), new ChatTurn("user", question)], ct);
        await Log("gen.slot_chat", session, new { tag, question }, ct);
        return answer;
    }

    // ── 校验、预览、渲染（FR-5.13 至 5.17）───────────────────────

    public async Task<CompletenessView> CheckCompletenessAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await MustFindAsync(sessionId, ct);
        var template = await db.Templates.AsNoTracking().Include(t => t.Slots)
            .FirstAsync(t => t.Id == session.TemplateId, ct);
        return Completeness(template, Parse(session.SlotValues));
    }

    /// <summary>FR-5.13：待填或待确认的必填槽位都算未完成——AI 建议没确认，不能进生成。</summary>
    internal static CompletenessView Completeness(Template template, List<SlotState> states)
    {
        var byTag = states.ToDictionary(s => s.Tag);
        var incomplete = new List<IncompleteSlot>();
        var current = template.Slots.Where(s => s.Stage == SlotStage.Current).ToList();
        var done = 0;
        foreach (var def in current.OrderBy(s => s.SortOrder))
        {
            byTag.TryGetValue(def.Tag, out var s);
            var empty = s?.Value is null;
            var unconfirmedAi = s is { Source: SlotFillSource.AiSuggested, Confirmed: false };
            if (!empty && !unconfirmedAi) done++;
            if (def.Required && (empty || unconfirmedAi))
                incomplete.Add(new IncompleteSlot(def.Tag, def.Name, def.Section,
                    empty ? "未填写" : "AI 建议待确认"));
        }
        return new CompletenessView(incomplete.Count == 0, current.Count, done, incomplete);
    }

    public async Task<PreviewView> PreviewAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await MustFindAsync(sessionId, ct);
        var template = await db.Templates.AsNoTracking().Include(t => t.Slots)
            .FirstAsync(t => t.Id == session.TemplateId, ct);
        var states = Parse(session.SlotValues).ToDictionary(s => s.Tag);

        var sections = template.Slots.OrderBy(s => s.SortOrder)
            .GroupBy(s => s.Section)
            .Select(g => new PreviewSection(g.Key, g.Select(def =>
            {
                states.TryGetValue(def.Tag, out var s);
                return new PreviewItem(def.Tag, def.Name, s?.Value, SourceLabel(def, s), s?.Origin);
            }).ToList()))
            .ToList();

        // PDF 经可插拔转换服务（FR-5.14 口径见 README）：未配置即 null，前端以标色结构呈现
        string? pdfKey = null;
        var converter = await config.GetStringAsync(ConfigKeys.PdfConverterUrl, "", ct);
        if (converter.Length > 0 && session.OutputFileKey is not null)
        {
            try
            {
                var http = httpFactory.CreateClient("model");
                await using var docx = await storage.OpenAsync(session.OutputFileKey, ct);
                using var form = new MultipartFormDataContent { { new StreamContent(docx), "file", "preview.docx" } };
                var resp = await http.PostAsync(converter, form, ct);
                resp.EnsureSuccessStatusCode();
                await using var pdf = await resp.Content.ReadAsStreamAsync(ct);
                pdfKey = await storage.SaveAsync(pdf, "preview.pdf", ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                pdfKey = null; // 转换服务不可用：降级为结构化预览，不阻断
            }
        }
        return new PreviewView(sections, pdfKey);
    }

    private static string SourceLabel(TemplateSlot def, SlotState? s)
    {
        if (def.Stage == SlotStage.Later) return "后续阶段";
        if (s?.Value is null) return "未填写";
        return s.Source switch
        {
            SlotFillSource.Template => "模板固定",
            SlotFillSource.Inherited when !s.Confirmed => "继承",
            SlotFillSource.AiSuggested when !s.Confirmed => "AI 建议待确认",
            SlotFillSource.AiSuggested => "已确认（来自 AI 建议）", // 预览单独标出（FR-5.14）
            _ => "已确认"
        };
    }

    public async Task<RenderResult> RenderAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await MustFindAsync(sessionId, ct);
        var template = await db.Templates.AsNoTracking().Include(t => t.Slots)
            .FirstAsync(t => t.Id == session.TemplateId, ct);
        var states = Parse(session.SlotValues);

        var check = Completeness(template, states);
        if (!check.CanRender)
            throw new DomainRuleException("INCOMPLETE",
                $"存在未完成的必填槽位（{check.Incomplete.Count} 项），不允许生成（FR-5.13）：" +
                string.Join("、", check.Incomplete.Take(6).Select(i => i.Name)));

        var byTag = states.ToDictionary(s => s.Tag);
        var inputs = template.Slots.Select(def => new DocxSlotFiller.FillInput(
            def.Tag,
            def.Stage == SlotStage.Later ? null : byTag.GetValueOrDefault(def.Tag)?.Value,
            def.DataType)).ToList();

        DocxSlotFiller.FillResult filledDoc;
        await using (var tpl = await storage.OpenAsync(template.FileKey, ct))
            filledDoc = await DocxSlotFiller.FillAsync(tpl, inputs, ct);

        var outputName = $"{template.Name}-{DateTimeOffset.UtcNow:yyyyMMdd}-{session.Id.ToString()[..8]}.docx";
        using (var outMs = new MemoryStream(filledDoc.Output))
            session.OutputFileKey = await storage.SaveAsync(outMs, outputName, ct);
        session.Status = GenerationStatus.Completed;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        // 生成记录（FR-5.17）：会话本身即记录——模板、基准、全部槽位值与来源、输出、操作人
        await Log("gen.render", session, new
        {
            template.Name, session.BaseProjectNo,
            filled = filledDoc.Filled, blank = filledDoc.LeftBlank, warnings = filledDoc.Warnings
        }, ct);
        return new RenderResult(session.Id, outputName, filledDoc.Filled, filledDoc.LeftBlank);
    }

    public async Task<(Stream Content, string FileName)> OpenOutputAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await MustFindAsync(sessionId, ct);
        if (session.OutputFileKey is null)
            throw new DomainRuleException("NOT_RENDERED", "该会话尚未生成文件");
        var template = await db.Templates.AsNoTracking().FirstAsync(t => t.Id == session.TemplateId, ct);
        return (await storage.OpenAsync(session.OutputFileKey, ct),
            $"{template.Name}-{session.Id.ToString()[..8]}.docx");
    }

    // ── 内部 ─────────────────────────────────────────────

    private static string? ProjectField(Project p, string field) => field switch
    {
        "customer_name" => p.CustomerName,
        "year" => p.Year.ToString(),
        "device_type" => p.DeviceType,
        "device_model" => p.DeviceModel,
        "spec_params" => p.SpecParams,
        "contract_amount" => p.ContractAmount?.ToString("F0"),
        "delivery_status" => p.DeliveryStatus?.ToString(),
        "project_no" => p.ProjectNo,
        _ => null
    };

    private static string LastSegment(string? path)
        => (path ?? "").Split('>', StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private static string StripHeadingPrefix(string text)
    {
        if (text.StartsWith('【'))
        {
            var nl = text.IndexOf('\n');
            if (nl > 0 && text[..nl].EndsWith('】')) return text[(nl + 1)..].Trim();
        }
        return text.Trim();
    }

    /// <summary>短文本建议取值：在命中分块里挑最贴题的一行——含槽位名的优先，其次带数字的；
    /// 表格分块的表头行（「| 型号 | 容积 |…」，无数字）不作取值，否则建议值会是一串列名。</summary>
    private static string BestLine(string text, string slotName)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        static bool IsHeader(string l) => l.StartsWith('|') && !l.Any(char.IsDigit);
        var line = lines.FirstOrDefault(l => l.Contains(slotName) && !IsHeader(l))
                   ?? lines.FirstOrDefault(l => l.Any(char.IsDigit) && !IsHeader(l))
                   ?? lines.FirstOrDefault(l => !IsHeader(l))
                   ?? lines.FirstOrDefault() ?? "";
        return line.Length <= 120 ? line : line[..120];
    }

    internal static List<SlotState> Parse(string json)
        => JsonSerializer.Deserialize<List<SlotState>>(json, SlotJson) ?? [];

    private Task<SessionView> ViewAsync(GenerationSession session, Template template, CancellationToken ct)
        => Task.FromResult(View(session, template));

    private static SessionView View(GenerationSession session, Template template)
    {
        var states = Parse(session.SlotValues).ToDictionary(s => s.Tag);
        var slots = template.Slots.OrderBy(s => s.SortOrder)
            .Select(def => new SlotView(TemplateService.ToDef(def),
                states.GetValueOrDefault(def.Tag) ?? new SlotState(def.Tag, null, null, false, false, null, null)))
            .ToList();
        string? outputName = session.OutputFileKey is null ? null
            : $"{template.Name}-{session.Id.ToString()[..8]}.docx";
        return new SessionView(session.Id, template.Id, template.Name, session.ProjectHint,
            session.BaseProjectNo, session.Status, slots, session.UpdatedAt, outputName);
    }

    private Task<GenerationSession?> FindAsync(Guid id, CancellationToken ct)
        => db.GenerationSessions.FirstOrDefaultAsync(s => s.Id == id && s.CreatedById == me.UserId, ct);

    private async Task<GenerationSession> MustFindAsync(Guid id, CancellationToken ct)
        => await FindAsync(id, ct)
           ?? throw new DomainRuleException("SESSION_NOT_FOUND", "生成会话不存在或不属于你");

    private Task Log(string action, GenerationSession s, object? detail, CancellationToken ct)
        => audit.WriteAsync(new AuditEntry(action, AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "generation_session", TargetId: s.Id.ToString(), Detail: detail), ct);
}
