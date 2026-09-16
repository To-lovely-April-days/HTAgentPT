using System.Text;
using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Clients;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HT.Agent.Infrastructure.Services;

/// <summary>对话式方案生成：以聊天回合驱动既有槽位机制（预填/校验/渲染全部复用 GenerationService）。
/// 模型只做一件事——从自然语言里抽槽位值，抽出的每个值经 GenChatLogic 校验后才落库；
/// 对话模型是演示档时退化为按话术逐项问答（一次一项，整句即答案），链路不断。</summary>
public class GenerationChatService(
    AppDbContext db,
    IGenerationService gen,
    IChatModelClient chat,
    IRuntimeConfig config,
    IConfiguration appConfig,
    IAuditWriter audit,
    ICurrentUser me) : IGenerationChatService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>会话内的对话状态（jsonb）：跳过的槽位排到最后再问；上一轮在问谁（演示档整句即其答案）；
    /// 基准项目是否已决定（决定过就不再重复推荐）。</summary>
    private sealed class ChatState
    {
        public List<string> Skipped { get; set; } = [];
        public List<string> LastAsked { get; set; } = [];
        public bool BaseDecided { get; set; }
    }

    public async Task<GenChatStateView> GetAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await MustFindAsync(sessionId, ct);
        var template = await TemplateAsync(session.TemplateId, ct);
        var messages = await db.GenChatMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId).OrderBy(m => m.Id).ToListAsync(ct);
        return new GenChatStateView(messages.Select(ToView).ToList(), Progress(session, template));
    }

    public async Task<GenChatTurnResult> TurnAsync(Guid sessionId, GenChatTurnInput input, CancellationToken ct = default)
    {
        var session = await MustFindAsync(sessionId, ct);
        var template = await TemplateAsync(session.TemplateId, ct);
        var state = session.ChatState is null ? new ChatState()
            : JsonSerializer.Deserialize<ChatState>(session.ChatState, Json) ?? new ChatState();
        var stub = await IsStubAsync(ct);
        var batch = stub ? 1 : 4;
        var added = new List<GenChatMessage>();

        // 开场白：只在会话还没有任何消息时补一次
        if (input.Start && !await db.GenChatMessages.AnyAsync(m => m.SessionId == sessionId, ct))
            added.Add(await OpeningAsync(session, template, state, batch, ct));

        if (input.BaseProjectNo is not null)
        {
            var no = input.BaseProjectNo.Trim();
            added.Add(User(session, no.Length == 0 ? "不用基准项目，逐项填写" : $"选定基准项目 {no}"));
            await gen.SetBaseProjectAsync(sessionId, no.Length == 0 ? null : no, ct);
            await db.Entry(session).ReloadAsync(ct);
            state.BaseDecided = true;
            var states = GenerationService.Parse(session.SlotValues);
            var inherited = states.Count(s => s.Source == SlotFillSource.Inherited);
            var head = no.Length == 0
                ? "好，不带基准，逐项来。"
                : $"已按基准项目 {no} 预填 {inherited} 项（每项都记了出处，之后随时可改）。";
            added.Add(AskMessage(session, template, state, batch, head));
        }

        if (input.OptionFills is { Count: > 0 })
        {
            var defs = template.Slots.ToDictionary(s => s.Tag);
            var states = GenerationService.Parse(session.SlotValues);
            var applied = new List<(string Name, string Value)>();
            var notes = new List<string>();
            foreach (var f in input.OptionFills)
            {
                if (!defs.TryGetValue(f.Tag, out var def)) { notes.Add($"槽位 {f.Tag} 不存在"); continue; }
                var (ok, val, note) = Validate(def, f.Value);
                if (!ok) { notes.Add(note!); continue; }
                Apply(states, f.Tag, val!);
                applied.Add((def.Name, val!));
            }
            SaveStates(session, states);
            added.Add(User(session, string.Join("；", input.OptionFills.Select(f =>
                $"{(defs.TryGetValue(f.Tag, out var d) ? d.Name : f.Tag)}：{f.Value}"))));
            added.Add(AskMessage(session, template, state, batch, EchoHead(applied, notes)));
        }

        if (!string.IsNullOrWhiteSpace(input.Message))
        {
            var text = input.Message.Trim();
            added.Add(User(session, text));
            var command = GenChatLogic.DetectCommand(text);
            switch (command)
            {
                case GenChatLogic.ChatCommand.Skip:
                {
                    foreach (var t in state.LastAsked.Where(t => !state.Skipped.Contains(t)))
                        state.Skipped.Add(t);
                    added.Add(AskMessage(session, template, state, batch, "先跳过。"));
                    break;
                }
                case GenChatLogic.ChatCommand.Summary:
                    added.Add(SummaryMessage(session, template));
                    break;
                case GenChatLogic.ChatCommand.Render:
                    added.Add(await RenderMessageAsync(session, template, ct));
                    break;
                default:
                    added.Add(stub
                        ? SequentialFill(session, template, state, text)
                        : await ExtractFillAsync(session, template, state, text, batch, ct));
                    break;
            }
        }

        if (input.Render)
        {
            added.Add(User(session, "生成文档"));
            added.Add(await RenderMessageAsync(session, template, ct));
        }

        session.ChatState = JsonSerializer.Serialize(state, Json);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        db.GenChatMessages.AddRange(added);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("gen.chat", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "generation_session", TargetId: session.Id.ToString(),
            Detail: new { turns = added.Count, stub }), ct);
        return new GenChatTurnResult(added.Select(ToView).ToList(), Progress(session, template));
    }

    // ── 回合构造 ─────────────────────────────────────────────

    private async Task<GenChatMessage> OpeningAsync(GenerationSession session, Template template,
        ChatState state, int batch, CancellationToken ct)
    {
        var current = template.Slots.Where(s => s.Stage == SlotStage.Current).OrderBy(s => s.SortOrder).ToList();
        var sections = current.GroupBy(s => s.Section).Select(g => $"{g.Key}（{g.Count()} 项）").ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"开始填《{template.Name}》，共 {current.Count} 项：{string.Join("、", sections)}。");
        sb.AppendLine("可以整句描述项目（如「华东理工的 10L 反应釜，316L 材质，法兰结构，电加热」），我会把能确定的项自动填入；也可以逐项回答。");
        sb.Append("随时可以说「跳过」「汇总」「生成文档」，或改口修正已填的值。");

        object? candidates = null;
        if (session.BaseProjectNo is null && !state.BaseDecided)
        {
            var found = await gen.GetCandidatesAsync(session.Id, ct);
            if (found.Count > 0)
            {
                candidates = found.Select(c => new
                {
                    c.ProjectNo, c.CustomerName, c.Year, c.DeviceType, c.DeviceModel, c.InheritableSlots
                }).ToList();
                sb.Insert(0, "先说基准：找到几个相近的历史项目，选一个可以把能继承的参数直接带进来（客户、日期、单价这类默认不继承）。也可以不用基准。\n\n");
            }
            else state.BaseDecided = true; // 没有候选就不再问
        }

        // 有基准候选时先聊基准，槽位问题等基准定了再问（否则一次抛两个决策）
        var asks = candidates is null
            ? NextAsks(template, GenerationService.Parse(session.SlotValues), state, batch)
            : [];
        state.LastAsked = asks.Select(a => a.Tag).ToList();
        if (asks.Count > 0)
            sb.Append("\n\n").Append(AskText(asks));
        return Assistant(session, sb.ToString(), new { asks, baseCandidates = candidates });
    }

    /// <summary>演示档回合：一次只问一项，整句即当前问题的答案（选择/日期仍过校验）。
    /// 上一轮没在问任何问题时，整句不能当答案——演示档不会抽取，只能如实说并把问题问出来。</summary>
    private GenChatMessage SequentialFill(GenerationSession session, Template template, ChatState state, string text)
    {
        var defs = template.Slots.ToDictionary(s => s.Tag);
        var states = GenerationService.Parse(session.SlotValues);
        var byTag = states.ToDictionary(s => s.Tag);
        var targetTag = state.LastAsked.FirstOrDefault(t =>
            defs.ContainsKey(t) && byTag.GetValueOrDefault(t)?.Value is null);
        if (targetTag is null)
        {
            if (NextAsks(template, states, state, 1).Count == 0)
                return SummaryMessage(session, template);
            return AskMessage(session, template, state, 1,
                "当前是内置演示应答，不能从整句里自动抽取（接入对话模型后可以）。咱们逐项来。");
        }
        var def = defs[targetTag];
        var (ok, val, note) = Validate(def, text);
        if (!ok)
            return AskMessage(session, template, state, 1, note + "。", keepAsked: true);
        Apply(states, targetTag, val!);
        SaveStates(session, states);
        return AskMessage(session, template, state, 1, $"已记录：{def.Name} = {val}。");
    }

    /// <summary>模型档回合：把槽位清单与最近对话交给对话模型抽取多槽位取值，逐项校验后落库。
    /// 模型不可用或输出解析不出来时如实说，不猜、不硬填。</summary>
    private async Task<GenChatMessage> ExtractFillAsync(GenerationSession session, Template template,
        ChatState state, string text, int batch, CancellationToken ct)
    {
        var defs = template.Slots.Where(s => s.Stage == SlotStage.Current).OrderBy(s => s.SortOrder).ToList();
        var states = GenerationService.Parse(session.SlotValues);
        var byTag = states.ToDictionary(s => s.Tag);

        var sb = new StringBuilder();
        sb.AppendLine("你是工业设备方案的填单助手。任务：从用户最新发言中抽取下列槽位的取值。");
        sb.AppendLine("只输出一个 JSON 对象，形如 {\"fills\":[{\"tag\":\"...\",\"value\":\"...\"}]}，不要输出任何其他文字。");
        sb.AppendLine("规则：只抽取用户明确说出的信息，绝不猜测补全；选择类槽位的 value 必须取可选值之一；");
        sb.AppendLine("日期输出 yyyy-MM-dd；带单位的参数只填数值与必要修饰，单位表格里已有；用户改口时输出该项新值。");
        sb.AppendLine("没有可抽取的信息就输出 {\"fills\":[]}。");
        sb.AppendLine();
        sb.AppendLine("[槽位清单] tag | 名称 | 章节 | 类型 | 可选值 | 当前值");
        foreach (var d in defs)
        {
            var choices = ChoiceList(d);
            byTag.TryGetValue(d.Tag, out var st);
            sb.AppendLine($"{d.Tag} | {d.Name} | {d.Section} | {d.DataType}" +
                $" | {(choices is null ? "-" : string.Join("/", choices))}" +
                $" | {(st?.Value is null ? "未填" : Truncate(st.Value, 40))}");
        }

        var history = await db.GenChatMessages.AsNoTracking()
            .Where(m => m.SessionId == session.Id)
            .OrderByDescending(m => m.Id).Take(6).OrderBy(m => m.Id).ToListAsync(ct);
        var turns = new List<ChatTurn> { new("system", sb.ToString()) };
        foreach (var h in history)
            turns.Add(new ChatTurn(h.Role == "user" ? "user" : "assistant", Truncate(h.Content, 400)));
        turns.Add(new ChatTurn("user", text));

        string reply;
        try { reply = await chat.CompleteAsync(turns, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return AskMessage(session, template, state, batch,
                "对话模型暂时不可用，这句我没能解析。可以稍后重试，或用下方选项/逐项回答。", keepAsked: true);
        }

        var fills = GenChatLogic.ParseModelFills(reply);
        var applied = new List<(string Name, string Value)>();
        var notes = new List<string>();
        var defMap = defs.ToDictionary(d => d.Tag);
        foreach (var (tag, raw) in fills)
        {
            if (!defMap.TryGetValue(tag, out var def)) continue; // 模型编出来的 tag 直接丢弃
            var (ok, val, note) = Validate(def, raw);
            if (!ok) { notes.Add(note!); continue; }
            Apply(states, tag, val!);
            applied.Add((def.Name, val!));
        }
        if (applied.Count > 0) SaveStates(session, states);

        if (applied.Count == 0 && notes.Count == 0)
            return AskMessage(session, template, state, batch,
                "这句里没有识别到可入表的信息。可以直接回答当前问题，或换个说法。", keepAsked: true);
        return AskMessage(session, template, state, batch, EchoHead(applied, notes));
    }

    /// <summary>「已记录…」开头 + 下一批提问（或收尾汇总）的标准助手消息。</summary>
    private GenChatMessage AskMessage(GenerationSession session, Template template, ChatState state,
        int batch, string head, bool keepAsked = false)
    {
        var states = GenerationService.Parse(session.SlotValues);
        var asks = keepAsked && state.LastAsked.Count > 0
            ? AskDefs(template, state.LastAsked)
            : NextAsks(template, states, state, batch);
        if (!keepAsked) state.LastAsked = asks.Select(a => a.Tag).ToList();

        if (asks.Count == 0)
        {
            var check = GenerationService.Completeness(template, states);
            var content = head + "\n" + (check.CanRender
                ? $"全部必填项已完成（{check.Done}/{check.Total}）。确认汇总无误后就可以生成文档。"
                : $"待问的问完了，但还有 {check.Incomplete.Count} 项未完成（多为跳过的必填项）：" +
                  string.Join("、", check.Incomplete.Take(6).Select(i => i.Name)) + "。补上才能生成。");
            return Assistant(session, content, SummaryPayload(template, states, check));
        }
        return Assistant(session, head + "\n" + AskText(asks), new { asks });
    }

    private GenChatMessage SummaryMessage(GenerationSession session, Template template)
    {
        var states = GenerationService.Parse(session.SlotValues);
        var check = GenerationService.Completeness(template, states);
        var content = check.CanRender
            ? $"当前进度 {check.Done}/{check.Total}，必填项已齐，可以生成文档。"
            : $"当前进度 {check.Done}/{check.Total}，还差：{string.Join("、", check.Incomplete.Take(8).Select(i => i.Name))}"
              + (check.Incomplete.Count > 8 ? " 等" : "") + "。";
        return Assistant(session, content, SummaryPayload(template, states, check));
    }

    private async Task<GenChatMessage> RenderMessageAsync(GenerationSession session, Template template, CancellationToken ct)
    {
        var states = GenerationService.Parse(session.SlotValues);
        var check = GenerationService.Completeness(template, states);
        if (!check.CanRender)
            return Assistant(session,
                $"还不能生成：{check.Incomplete.Count} 个必填项未完成——" +
                string.Join("、", check.Incomplete.Take(8).Select(i => i.Name)) +
                (check.Incomplete.Count > 8 ? " 等" : "") + "。补齐后再来。",
                SummaryPayload(template, states, check));
        var r = await gen.RenderAsync(session.Id, ct);
        await db.Entry(session).ReloadAsync(ct);
        return Assistant(session,
            $"已生成《{r.OutputFileName}》：回填 {r.SlotsFilled} 项，留空 {r.LeftBlank} 项。" +
            "文件页眉带「待复核」标注，请下载核对后再对外使用。",
            new { rendered = new { fileName = r.OutputFileName, filled = r.SlotsFilled, blank = r.LeftBlank } });
    }

    // ── 槽位与提问 ────────────────────────────────────────────

    private sealed record Ask(string Tag, string Name, string Section, string DataType,
        IReadOnlyList<string>? Choices, string? Prompt, string? Unit, bool Required);

    /// <summary>下一批提问：按模板顺序取未填的当前阶段槽位，同章节成批；
    /// 跳过的排到最后（必填不会因跳过而消失，只是先不烦你）。</summary>
    private static List<Ask> NextAsks(Template template, List<SlotState> states, ChatState state, int batch)
    {
        var byTag = states.ToDictionary(s => s.Tag);
        var open = template.Slots.Where(s => s.Stage == SlotStage.Current)
            .OrderBy(s => s.SortOrder)
            .Where(s => byTag.GetValueOrDefault(s.Tag)?.Value is null)
            .ToList();
        var pool = open.Where(s => !state.Skipped.Contains(s.Tag)).ToList();
        if (pool.Count == 0) pool = open; // 只剩跳过的了，回头再问
        if (pool.Count == 0) return [];
        var section = pool[0].Section;
        return pool.Where(s => s.Section == section).Take(batch).Select(ToAsk).ToList();
    }

    private static List<Ask> AskDefs(Template template, List<string> tags)
        => template.Slots.Where(s => tags.Contains(s.Tag)).OrderBy(s => s.SortOrder).Select(ToAsk).ToList();

    private static Ask ToAsk(TemplateSlot s)
        => new(s.Tag, s.Name, s.Section, s.DataType.ToString(), ChoiceList(s), s.Prompt, s.Unit, s.Required);

    private static string AskText(List<Ask> asks)
    {
        var sb = new StringBuilder();
        sb.Append($"【{asks[0].Section}】");
        foreach (var a in asks)
        {
            sb.Append('\n').Append("· ").Append(a.Name);
            if (a.Unit is not null) sb.Append($"（{a.Unit}）");
            if (a.Choices is not null) sb.Append("：").Append(string.Join(" / ", a.Choices));
            else if (a.Prompt is not null) sb.Append("——").Append(a.Prompt);
        }
        return sb.ToString();
    }

    private static string EchoHead(List<(string Name, string Value)> applied, List<string> notes)
    {
        var sb = new StringBuilder();
        if (applied.Count > 0)
            sb.Append($"已记录 {applied.Count} 项：")
              .Append(string.Join("；", applied.Select(a => $"{a.Name} = {Truncate(a.Value, 24)}")))
              .Append('。');
        foreach (var n in notes) sb.Append('\n').Append(n).Append('。');
        return sb.Length == 0 ? "" : sb.ToString();
    }

    /// <summary>取值校验：选择类必须落在可选值内（同义规整）、日期规整为 yyyy-MM-dd、长度设上限。</summary>
    private static (bool Ok, string? Value, string? Note) Validate(TemplateSlot def, string raw)
    {
        var v = raw.Trim();
        if (v.Length == 0) return (false, null, $"「{def.Name}」没有给出内容");
        if (v.Length > 2000) v = v[..2000];
        var choices = ChoiceList(def);
        if (choices is not null)
        {
            var hit = GenChatLogic.MatchChoice(v, choices);
            return hit is null
                ? (false, null, $"「{def.Name}」的取值「{Truncate(v, 20)}」不在可选项里（可选：{string.Join(" / ", choices)}）")
                : (true, hit, null);
        }
        if (def.DataType == SlotDataType.Date)
        {
            var d = GenChatLogic.NormalizeDate(v, DateTimeOffset.Now.Year);
            return d is null
                ? (false, null, $"「{def.Name}」的日期没看懂（用 2026-10-01 或 10月1日 这类写法）")
                : (true, d, null);
        }
        return (true, v, null);
    }

    /// <summary>对话里落值等同用户在工作台填写并确认（5.2.2 的用户主动修改分支）。</summary>
    private static void Apply(List<SlotState> states, string tag, string value)
    {
        var idx = states.FindIndex(s => s.Tag == tag);
        var s = idx >= 0 ? states[idx] : new SlotState(tag, null, null, false, false, null, null);
        var updated = s with
        {
            Value = value,
            Source = SlotFillSource.Confirmed,
            Confirmed = true,
            UserTouched = true,
            Origin = "对话填写",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (idx >= 0) states[idx] = updated; else states.Add(updated);
    }

    private static readonly JsonSerializerOptions SlotJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static void SaveStates(GenerationSession session, List<SlotState> states)
    {
        session.SlotValues = JsonSerializer.Serialize(states, SlotJson);
        session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static IReadOnlyList<string>? ChoiceList(TemplateSlot def)
    {
        if (def.DataType != SlotDataType.SingleChoice || string.IsNullOrWhiteSpace(def.Choices)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(def.Choices);
            if (parsed is { Count: > 0 }) return parsed;
        }
        catch (JsonException) { /* 非 JSON 存法退回分隔符解析 */ }
        var split = def.Choices.Split(new[] { '|', ',', '，', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return split.Length > 0 ? split : null;
    }

    private static object SummaryPayload(Template template, List<SlotState> states, CompletenessView check)
    {
        var byTag = states.ToDictionary(s => s.Tag);
        return new
        {
            summary = template.Slots.Where(s => s.Stage == SlotStage.Current)
                .OrderBy(s => s.SortOrder)
                .GroupBy(s => s.Section)
                .Select(g => new
                {
                    section = g.Key,
                    items = g.Select(s => new { name = s.Name, value = byTag.GetValueOrDefault(s.Tag)?.Value }).ToList()
                }).ToList(),
            canRender = check.CanRender
        };
    }

    // ── 基建 ────────────────────────────────────────────────

    private async Task<bool> IsStubAsync(CancellationToken ct)
    {
        var fallback = ModelSlotDefaults.UseStubs(appConfig) ? "stub" : "deepseek";
        var provider = await config.GetStringAsync(ConfigKeys.ChatProvider, fallback, ct);
        return provider.Trim().Equals("stub", StringComparison.OrdinalIgnoreCase);
    }

    private GenChatMessage User(GenerationSession s, string content)
        => new() { SessionId = s.Id, Role = "user", Content = content, At = DateTimeOffset.UtcNow };

    private GenChatMessage Assistant(GenerationSession s, string content, object? payload)
        => new()
        {
            SessionId = s.Id,
            Role = "assistant",
            Content = content,
            Payload = payload is null ? null : JsonSerializer.Serialize(payload, Json),
            At = DateTimeOffset.UtcNow
        };

    private static GenChatMessageView ToView(GenChatMessage m)
        => new(m.Id, m.Role, m.Content, m.Payload, m.At);

    private GenChatProgress Progress(GenerationSession session, Template template)
    {
        var check = GenerationService.Completeness(template, GenerationService.Parse(session.SlotValues));
        string? output = session.OutputFileKey is null ? null : $"{template.Name}-{session.Id.ToString()[..8]}.docx";
        return new GenChatProgress(check.Total, check.Done, check.CanRender, output);
    }

    private Task<Template> TemplateAsync(Guid templateId, CancellationToken ct)
        => db.Templates.AsNoTracking().Include(t => t.Slots).FirstAsync(t => t.Id == templateId, ct);

    private async Task<GenerationSession> MustFindAsync(Guid id, CancellationToken ct)
        => await db.GenerationSessions.FirstOrDefaultAsync(s => s.Id == id && s.CreatedById == me.UserId, ct)
           ?? throw new DomainRuleException("SESSION_NOT_FOUND", "生成会话不存在或不属于你");

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
