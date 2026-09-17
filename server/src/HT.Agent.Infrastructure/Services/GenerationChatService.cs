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
using static HT.Agent.Application.Logic.GenChatLogic;

namespace HT.Agent.Infrastructure.Services;

/// <summary>对话式方案生成——总指挥 + 专员：每句话先由总指挥（对话模型；演示档/模型不可用时为规则）
/// 产出一张派工单，服务端按单调度专员执行——填单员（抽值入表）、台账查询员（历史项目）、
/// 参数顾问（基准文档与知识库检索建议，只给带依据的）、答疑员（就某项检索作答，不动表）、
/// 出单员（校验/汇总/渲染）。总指挥只决定找谁做，从不发明数值；抽出的每个值都经校验才落库。
/// 主动性：报出客户/设备后自动翻台账递基准卡；进入新章节先给知识库里有依据的建议再问。
/// 一轮只回一条助手消息，交互件（卡片/按钮）都挂在这条上。</summary>
public class GenerationChatService(
    AppDbContext db,
    IGenerationService gen,
    IChatModelClient chat,
    ITranslationService translation,
    IRetrievalService retrieval,
    IRuntimeConfig config,
    IConfiguration appConfig,
    IAuditWriter audit,
    ICurrentUser me) : IGenerationChatService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions SlotJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>会话内的对话状态（jsonb）。</summary>
    private sealed class ChatState
    {
        public List<string> Skipped { get; set; } = [];
        public List<string> LastAsked { get; set; } = [];
        /// <summary>上一轮列出的历史项目编号，「用第二个」按此解析。</summary>
        public List<string> LastCandidates { get; set; } = [];
        public bool BaseDecided { get; set; }
        /// <summary>主动递过基准卡时依据的客户（换了客户再递一次）。</summary>
        public string? OfferedFor { get; set; }
        public bool BaseOffered { get; set; }
        public string? LastSection { get; set; }
        public List<string> SuggestedSections { get; set; } = [];
        /// <summary>上一轮给过的建议值（tag→值）。已填槽位的建议不落库（5.2.2 不覆盖前序来源），
        /// 但用户说「采纳」时要能按这份记录改过去——那是用户主动修改。</summary>
        public Dictionary<string, string> LastSuggestions { get; set; } = [];
        /// <summary>用户交代过的工况与硬性要求（「做硝化反应」「要过夜连续运行」）。
        /// 记下来后面每次选型都带着——说过一次就该记住，这是「懂行」与「复读机」的分界。</summary>
        public List<string> Conditions { get; set; } = [];
    }

    /// <summary>用户想换个方案而不是要现状：「想改一下」「换一个」「不合适」「有没有别的」。</summary>
    private static readonly System.Text.RegularExpressions.Regex WantsChange = new(
        @"(改一?下|改成|换一?个|换成|不合适|不满意|有没有别的|别的方案|其他方案|重新选|重新推荐)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private sealed record Ask(string Tag, string Name, string Section, string DataType,
        IReadOnlyList<string>? Choices, string? Prompt, string? Unit, bool Required, string? Suggested);

    /// <summary>一轮的回复合成器：各专员往里追加文字段与结构化附件，最后合成一条助手消息。</summary>
    private sealed class Reply
    {
        public readonly List<string> Parts = [];
        public List<Ask> Asks = [];
        public object? BaseCandidates;
        public readonly List<object> Suggestions = [];
        /// <summary>工况顾问给的建议：模型按工艺常识判断，没有文档依据，界面上单独一档。</summary>
        public readonly List<object> Advices = [];
        public object? Summary;
        public bool? CanRender;
        public object? Rendered;
        /// <summary>这一轮译出的整篇译文：文件、回填报告、命中术语。</summary>
        public object? Translated;
        /// <summary>这一轮刚落进去的项：无论后面状态怎么算，都不许在同一轮里再问一遍。</summary>
        public readonly HashSet<string> FilledNow = [];
        /// <summary>槽位有变动：下一批提问重新算。</summary>
        public bool Changed;
        /// <summary>有专员干了活：整句没抽到值时不再提示「没识别到」。</summary>
        public bool Handled;
        /// <summary>汇总/生成之后不再追加提问。</summary>
        public bool SuppressAsks;
        public void Add(string s) { if (!string.IsNullOrWhiteSpace(s)) Parts.Add(s.TrimEnd()); }
        public string Text => string.Join("\n", Parts);
        public object Payload => new
        {
            asks = Asks,
            baseCandidates = BaseCandidates,
            suggestions = Suggestions.Count == 0 ? null : Suggestions,
            advices = Advices.Count == 0 ? null : Advices,
            summary = Summary,
            canRender = CanRender,
            rendered = Rendered,
            translated = Translated
        };
    }

    private sealed record Ctx(GenerationSession Session, Template Template, ChatState State, bool Stub, int Batch, Reply Reply);

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
        var ctx = new Ctx(session, template, state, stub, stub ? 1 : 4, new Reply());
        var reply = ctx.Reply;
        var added = new List<GenChatMessage>();

        var fresh = input.Start && !await db.GenChatMessages.AnyAsync(m => m.SessionId == sessionId, ct);
        if (fresh && !string.IsNullOrWhiteSpace(input.Message))
            added.Add(Assistant(session, Greeting(template), null)); // 开场白单独一条，用户原话排在它后面
        else if (fresh)
            reply.Add(Greeting(template));

        if (input.BaseProjectNo is not null)
        {
            var no = input.BaseProjectNo.Trim();
            added.Add(User(session, no.Length == 0 ? "不用基准项目，逐项填写" : $"选定基准项目 {no}"));
            await PickBaseAsync(ctx, no.Length == 0 ? null : no, ct);
        }

        if (input.OptionFills is { Count: > 0 })
        {
            var defs = template.Slots.ToDictionary(s => s.Tag);
            added.Add(User(session, string.Join("；", input.OptionFills.Select(f =>
                $"{(defs.TryGetValue(f.Tag, out var d) ? d.Name : f.Tag)}：{f.Value}"))));
            ApplyFills(ctx, input.OptionFills.Select(f => new PlanAction("fill", Tag: f.Tag, Value: f.Value)).ToList());
        }

        if (input.AdoptTags is not null)
        {
            added.Add(User(session, input.AdoptTags.Count == 0 ? "采纳全部建议" : "采纳建议"));
            await AdoptAsync(ctx, input.AdoptTags, null, ct);
        }

        if (!string.IsNullOrWhiteSpace(input.Message))
        {
            var text = input.Message.Trim();
            added.Add(User(session, text));
            await DispatchAsync(ctx, text, ct);
        }

        if (input.Render)
        {
            added.Add(User(session, "生成文档"));
            await RenderAsync(ctx, ct);
        }

        await FinalizeAsync(ctx, ct);
        added.Add(Assistant(session, reply.Text, reply.Payload));

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

    // ── 总指挥：派工 ───────────────────────────────────────────

    private async Task DispatchAsync(Ctx ctx, string text, CancellationToken ct)
    {
        var rules = PlanByRules(text);
        var actions = rules.ToList();
        // 「跳过/汇总/生成/采纳/用第几个」是操作不是对话，规则更快更准，不劳模型。
        // 其余一律交给模型：它先正面回答，再顺带派活。
        // 规则在这里只是模型不可用时的退路——判意图这件事本来就不该靠正则穷举。
        var direct = actions.Count == 1 && actions[0].Type is "skip" or "summary" or "render" or "adopt" or "pick_base";
        if (!ctx.Stub && !direct)
        {
            var turn = await PlanByModelAsync(ctx, text, ct);
            if (turn is not null)
            {
                actions = turn.Actions.ToList();
                // 「给我一份英文版」这类规则判得很死的活，模型没派就替它派上。
                // 译文出不出得来由翻译链路说了算，不该由模型在正文里劝退——
                // 它劝退的那段话这轮也就不上屏了，省得跟下面真出来的译文自相矛盾。
                var declined = rules.Count == 1 && rules[0].Type == "translate"
                    && !actions.Any(a => a.Type == "translate");
                if (declined) actions.Add(rules[0]);
                // 模型说的话一定上屏——这是这套东西"像不像个懂行的人"的分界线
                else if (turn.Reply is not null)
                {
                    ctx.Reply.Add(turn.Reply);
                    ctx.Reply.Handled = true;
                }
            }
            // 模型没接上（不可用或输出崩了）才退回规则单，且去掉无 tag 的整句填值，
            // 免得把闲聊或它没读懂的话塞进槽位
            else actions = rules.Where(a => a.Type != "fill" || a.Tag is not null).ToList();
        }

        foreach (var a in actions.Where(a => a.Type == "pick_base")) await PickBaseAsync(ctx, ResolveProject(ctx, a), ct);
        foreach (var a in actions.Where(a => a.Type == "adopt")) await AdoptAsync(ctx, a.Tags, a.Name, ct);

        var fills = actions.Where(a => a.Type == "fill").ToList();
        if (fills.Count > 0) ApplyFills(ctx, fills);

        foreach (var a in actions.Where(a => a.Type == "ledger")) await LedgerAsync(ctx, a, text, ct);
        foreach (var a in actions.Where(a => a.Type == "suggest")) await SuggestAsync(ctx, a, text, ct);
        foreach (var a in actions.Where(a => a.Type == "ask")) await AnswerAsync(ctx, a, text, ct);
        foreach (var a in actions.Where(a => a.Type == "advise")) await AdviseAsync(ctx, a.Question ?? text, a.Name, ct);
        foreach (var a in actions.Where(a => a.Type == "translate")) await TranslateAsync(ctx, a.Value, ct);

        if (actions.Any(a => a.Type == "skip"))
        {
            foreach (var t in ctx.State.LastAsked.Where(t => !ctx.State.Skipped.Contains(t))) ctx.State.Skipped.Add(t);
            ctx.Reply.Add("先跳过。");
            ctx.Reply.Changed = true;
            ctx.Reply.Handled = true;
        }
        if (actions.Any(a => a.Type == "summary")) AddSummary(ctx);
        if (actions.Any(a => a.Type == "render")) await RenderAsync(ctx, ct);

        // 走到这儿还没人说话，只可能是演示档或模型不可用——这才轮到模板话
        if (!ctx.Reply.Handled)
            ctx.Reply.Add(ctx.Stub
                ? "当前是内置演示应答，答疑与按工况推荐要靠对话模型，接上以后我才能真正回答你。咱们先逐项来。"
                : "这句我没接住（对话模型没有返回可用内容）。可以直接给某一项的值、说「推荐一下」，或告诉我工况。");
    }

    /// <summary>把这一轮交给模型：槽位清单、已填值、工况、上一版建议与最近对话都给它，
    /// 要一句回答 + 一张派工单。回答与派工同出，它才既能说人话又能干活——
    /// 早先只让它输出 actions，遇到「设计压力能换吗」这种问题它就交白卷，
    /// 最后由规则兜底回一句模板话，看着像没长脑子。</summary>
    private async Task<ModelTurn?> PlanByModelAsync(Ctx ctx, string text, CancellationToken ct)
    {
        var defs = CurrentSlots(ctx.Template);
        var byTag = Parse(ctx.Session).ToDictionary(s => s.Tag);
        var sb = new StringBuilder();
        sb.AppendLine("你是化工实验设备（反应釜等）的资深工艺工程师，正陪用户填一份技术文档。每轮你做两件事：");
        sb.AppendLine("① 回答：用工程师的口吻正面回答用户这句话。");
        sb.AppendLine("   他问「这个能换吗」「为什么取这个值」「这样选有什么问题」「低一点行不行」，");
        sb.AppendLine("   你就答出取值依据、约束条件与取舍代价（换成什么、要付什么代价、什么工况下不能换），");
        sb.AppendLine("   而不是把问题推回去让他「告诉我要填的值」。拿不准就说拿不准，别编。");
        sb.AppendLine("   他给的是取值、指令或闲聊，就简短应一句。");
        sb.AppendLine("② 动作：这句话同时要求落值、查历史、给建议、出文档时，派对应的活。");
        sb.AppendLine("台账表格、建议清单、汇总表会另行呈现，正文里不要复述它们的内容，一句带过即可。");
        sb.AppendLine();
        sb.AppendLine("【输出格式】先正常写回答，想分段就分段，可以用小标题和要点——这段直接给用户看。");
        sb.AppendLine("如果这轮还需要动表/查历史/给建议/出文档，就在回答末尾追加一个 json 代码块：");
        sb.AppendLine("```json");
        sb.AppendLine("{\"actions\":[...]}");
        sb.AppendLine("```");
        sb.AppendLine("不需要动表就不要加这个块。不要把回答塞进 JSON 里——正文归正文，代码块只放动作。");
        sb.AppendLine("动作类型：");
        sb.AppendLine("- {\"type\":\"fill\",\"tag\":\"槽位tag\",\"value\":\"值\"}：用户明确给出的槽位取值，可多条；用户改口时给新值。" +
            "tag 必须照抄下面【槽位清单】里的 tag 原文（如 contract_no），不要写中文项名，也不要省略——" +
            "你说了「已记录」而 tag 没给对，用户就会被同一项问第二遍");
        sb.AppendLine("- {\"type\":\"ledger\",\"customer\":\"客户名或空\",\"device\":\"设备类型或空\",\"keyword\":\"型号/容积等关键词或空\"}：用户想查历史项目、以前做过的、台账、类似项目");
        sb.AppendLine("- {\"type\":\"pick_base\",\"projectNo\":\"项目编号\"} 或 {\"type\":\"pick_base\",\"index\":2}：用户要用某个历史项目做基准（「用第二个」→index 2）");
        sb.AppendLine("- {\"type\":\"suggest\",\"tags\":[\"tag\"]}：用户要参数推荐/建议/参考；tags 留空表示当前在问的项");
        sb.AppendLine("- {\"type\":\"adopt\",\"tags\":[\"tag\"]}：用户同意采纳建议；tags 留空=全部待确认的建议");
        sb.AppendLine("- {\"type\":\"ask\",\"question\":\"用户原话\",\"tag\":\"相关槽位tag或空\"}：用户在提问/咨询而不是给值");
        sb.AppendLine("- {\"type\":\"advise\",\"question\":\"用户原话\",\"name\":\"项名或空\"}：" +
            "①用户交代了工况、用途、介质或硬性约束（「我要做硝化反应」「介质有强腐蚀」「要过夜无人值守」）；" +
            "②用户对上一版建议不买账要换（「材质换其他的」「这个不合适」「再给一个」）——" +
            "这时 name 填他说的那一项，没点名就留空");
        sb.AppendLine("- {\"type\":\"translate\",\"value\":\"zh2en\"}：用户要这份文档的英文版/中文版（「给我一份英文的」「翻译成英文」）；" +
            "value 取 zh2en（出英文）或 en2zh（出中文）");
        sb.AppendLine("- {\"type\":\"skip\"}、{\"type\":\"summary\"}、{\"type\":\"render\"}");
        sb.AppendLine("规则：只抽取用户明确说出的取值，绝不猜测补全；选择类取值必须是可选值之一；日期 yyyy-MM-dd；");
        sb.AppendLine("要译文就派 translate，不要以「机器翻译不准」「术语会译错」为由拒绝——" +
            "整篇翻译由系统的翻译链路做，已审定术语按标准译法强制注入，材质牌号与安全条款正是靠这个统一的；" +
            "你也不要在回答里自己手工翻译整篇，那样版式会丢。回答里说一句「这就出」即可。");
        sb.AppendLine("带单位的参数只填数值与必要修饰；一句话可以含多个动作；");
        sb.AppendLine("只是在答疑、不需要动表时就只写回答，不要加代码块——但回答一定要有内容，不能交白卷。");
        sb.AppendLine();
        sb.AppendLine("[已交代的工况] " + (ctx.State.Conditions.Count == 0 ? "（无）" : string.Join("；", ctx.State.Conditions)));
        sb.AppendLine("[上一版给过的建议] " + (ctx.State.LastSuggestions.Count == 0 ? "（无）"
            : string.Join("；", ctx.State.LastSuggestions.Take(12)
                .Select(kv => $"{defs.FirstOrDefault(d => d.Tag == kv.Key)?.Name ?? kv.Key}={Truncate(kv.Value, 24)}"))));
        sb.AppendLine("[当前在问的项] " + (ctx.State.LastAsked.Count == 0 ? "（无）"
            : string.Join("、", ctx.State.LastAsked.Select(t => defs.FirstOrDefault(d => d.Tag == t)?.Name ?? t))));
        sb.AppendLine("[上一轮列出的历史项目] " + (ctx.State.LastCandidates.Count == 0 ? "（无）"
            : string.Join("；", ctx.State.LastCandidates.Select((p, i) => $"{i + 1}. {p}"))));
        sb.AppendLine();
        sb.AppendLine("[槽位清单] tag | 名称 | 章节 | 类型 | 可选值 | 当前值");
        foreach (var d in defs)
        {
            byTag.TryGetValue(d.Tag, out var st);
            var choices = ChoiceList(d);
            sb.AppendLine($"{d.Tag} | {d.Name} | {d.Section} | {d.DataType}" +
                $" | {(choices is null ? "-" : string.Join("/", choices))}" +
                $" | {(st?.Value is null ? "未填" : Truncate(st.Value, 40))}");
        }

        var history = await db.GenChatMessages.AsNoTracking()
            .Where(m => m.SessionId == ctx.Session.Id)
            .OrderByDescending(m => m.Id).Take(6).OrderBy(m => m.Id).ToListAsync(ct);
        var turns = new List<ChatTurn> { new("system", sb.ToString()) };
        foreach (var h in history)
            turns.Add(new ChatTurn(h.Role == "user" ? "user" : "assistant", Truncate(h.Content, 400)));
        turns.Add(new ChatTurn("user", text));

        try
        {
            var raw = await chat.CompleteAsync(turns, ct);
            var turn = ParseTurn(raw);
            return turn.Reply is null && turn.Actions.Count == 0 ? null : turn;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ctx.Reply.Add("对话模型暂时不可用，这句按规则处理。");
            return null;
        }
    }

    // ── 专员：填单员 ─────────────────────────────────────────

    /// <summary>抽出的每个值经校验才落库；演示档的整句（无 tag）只能算当前在问那一项的答案。</summary>
    private void ApplyFills(Ctx ctx, List<PlanAction> fills)
    {
        var defs = ctx.Template.Slots.ToDictionary(s => s.Tag);
        var states = Parse(ctx.Session);
        var byTag = states.ToDictionary(s => s.Tag);
        var applied = new List<(string Name, string Value)>();
        var notes = new List<string>();
        var dropped = new List<string>();
        foreach (var f in fills)
        {
            // 落到哪一项：tag 优先；模型把项名写进 tag（「合同编号」）也认；
            // 什么都没写就落到当前在问的那一项——用户刚回答的就是它
            var tag = f.Tag is not null && defs.ContainsKey(f.Tag) ? f.Tag
                : ResolveSlot(ctx.Template, f.Tag ?? f.Name ?? "")?.Tag
                  ?? ctx.State.LastAsked.FirstOrDefault(t => defs.ContainsKey(t) && IsOpen(byTag.GetValueOrDefault(t)));
            if (tag is null || !defs.TryGetValue(tag, out var def) || def.Stage != SlotStage.Current)
            {
                // 认不出该落到哪一项——但用户确实给了值，不能咽下去当没听见：
                // 咽下去的后果就是「我给了合同编号，它还在问合同编号」
                dropped.Add(Truncate(f.Value ?? "", 24));
                continue;
            }
            var (ok, val, note) = Validate(def, f.Value ?? "");
            if (!ok) { notes.Add(note!); ctx.Reply.Handled = true; continue; }
            Apply(states, tag, val!);
            byTag[tag] = states.First(s => s.Tag == tag);
            ctx.Reply.FilledNow.Add(tag);
            applied.Add((def.Name, val!));
        }
        if (applied.Count > 0)
        {
            SaveStates(ctx.Session, states);
            ctx.Reply.Changed = true;
            ctx.Reply.Handled = true;
            ctx.Reply.Add($"已记录 {applied.Count} 项：" +
                string.Join("；", applied.Select(a => $"{a.Name} = {Truncate(a.Value, 24)}")) + "。");
        }
        if (dropped.Count > 0)
        {
            ctx.Reply.Handled = true;
            ctx.Reply.Add($"「{string.Join("」「", dropped)}」我没认出是哪一项的值，没有入表——" +
                "带上项名再说一次（比如「合同编号 HT-2025-C0012」），我就能落进去。");
        }
        foreach (var n in notes) ctx.Reply.Add(n + "。");
    }

    // ── 专员：台账查询员 ───────────────────────────────────────

    /// <summary>按对话里说到的客户/设备/规格查历史项目，列卡片供选；条件来自模型派工单与原话的词表匹配。</summary>
    private async Task LedgerAsync(Ctx ctx, PlanAction a, string text, CancellationToken ct)
    {
        var (customers, devices) = await VocabAsync(ct);
        var probe = string.Join(" ", new[] { text, a.Customer, a.Device }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var f = IntentRouter.ExtractFilters(probe, customers, devices);
        var customer = f.CustomerName;
        var device = f.DeviceType;
        if (customer is null && device is null)
        {
            // 这句没点名：用项目要点与已填的客户/设备槽位补条件
            var ctxText = ContextText(ctx);
            var f2 = IntentRouter.ExtractFilters(ctxText, customers, devices);
            customer = f2.CustomerName;
            device = f2.DeviceType;
        }
        var keyword = a.Keyword ?? System.Text.RegularExpressions.Regex.Match(text, @"\d+(?:\.\d+)?\s*[lL升]|[A-Z]{2,4}-\d+[A-Za-z]*").Value;
        if (string.IsNullOrWhiteSpace(keyword)) keyword = null;

        var found = await gen.FindCandidatesAsync(ctx.Session.Id, customer, device, keyword, 5, ct);
        var cond = string.Join("，", new[]
        {
            customer is null ? null : $"客户={customer}",
            device is null ? null : $"设备={device}",
            keyword is null ? null : $"关键词={keyword}"
        }.Where(s => s != null));
        ctx.Reply.Handled = true;
        if (found.Count == 0)
        {
            ctx.Reply.Add($"台账里没找到匹配的历史项目（条件：{(cond.Length == 0 ? "无" : cond)}）。可以换个条件再查，比如只说设备类型或客户。");
            return;
        }
        ctx.State.LastCandidates = found.Select(c => c.ProjectNo).ToList();
        ctx.State.BaseOffered = true;
        var sb = new StringBuilder();
        sb.Append($"台账里找到 {found.Count} 个历史项目（条件：{(cond.Length == 0 ? "最近项目" : cond)}）：");
        for (var i = 0; i < found.Count; i++)
        {
            var c = found[i];
            sb.Append($"\n{i + 1}. {c.ProjectNo}　{c.CustomerName}　{c.Year}　{c.DeviceType}{(c.DeviceModel is null ? "" : " " + c.DeviceModel)}" +
                      $"{(c.SpecParams is null ? "" : "　" + Truncate(c.SpecParams, 30))}　可继承 {c.InheritableSlots} 项");
        }
        sb.Append("\n说「用第二个做基准」或点卡片；不用基准也可以继续填。");
        ctx.Reply.Add(sb.ToString());
        ctx.Reply.BaseCandidates = Cards(found);
    }

    private async Task PickBaseAsync(Ctx ctx, string? projectNo, CancellationToken ct)
    {
        ctx.Reply.Handled = true;
        if (projectNo == "?")
        {
            ctx.Reply.Add("上一轮没有列出这个序号的项目——先说「查一下历史做过的」，再按序号选。");
            return;
        }
        try
        {
            await gen.SetBaseProjectAsync(ctx.Session.Id, projectNo, ct);
        }
        catch (DomainRuleException ex)
        {
            ctx.Reply.Add(ex.Message + "。");
            return;
        }
        ctx.State.BaseDecided = true;
        ctx.Reply.Changed = true;
        var inherited = Parse(ctx.Session).Count(s => s.Source == SlotFillSource.Inherited);
        ctx.Reply.Add(projectNo is null
            ? "好，不带基准，逐项来。"
            : $"已按基准项目 {projectNo} 预填 {inherited} 项（每项都记了出处，可在「逐项核对」里看，随时可改）。");
    }

    private static string? ResolveProject(Ctx ctx, PlanAction a)
    {
        if (a.ProjectNo is not null) return a.ProjectNo;
        if (a.Index is { } i && i >= 1 && i <= ctx.State.LastCandidates.Count) return ctx.State.LastCandidates[i - 1];
        return "?";
    }

    // ── 专员：参数顾问 ─────────────────────────────────────────

    /// <summary>对指定项（默认当前在问的项）逐项检索基准文档与知识库给建议——只给带依据的，没有就明说。</summary>
    private async Task SuggestAsync(Ctx ctx, PlanAction a, string? text, CancellationToken ct)
    {
        ctx.Reply.Handled = true;
        var tags = (a.Tags ?? []).Select(t => ResolveSlot(ctx.Template, t)?.Tag).Where(t => t != null).Select(t => t!).ToList();
        // 用户在这句话里点了名就按点名的来——「这个材质我想改一下，你推荐一下」要的是材质，
        // 不是当前在问的那几项（那样会答非所问）
        if (tags.Count == 0 && !string.IsNullOrWhiteSpace(text))
            tags = CurrentSlots(ctx.Template)
                .Where(d => d.Name.Length >= 2 && text.Contains(d.Name, StringComparison.Ordinal))
                .OrderByDescending(d => d.Name.Length).Take(5).Select(d => d.Tag).ToList();
        if (tags.Count == 0)
        {
            var states = Parse(ctx.Session).ToDictionary(s => s.Tag);
            tags = ctx.State.LastAsked.Where(t => IsOpen(states.GetValueOrDefault(t))).ToList();
            if (tags.Count == 0) tags = NextAsks(ctx.Template, Parse(ctx.Session), ctx.State, 4).Select(x => x.Tag).ToList();
        }
        // 项目专属信息（客户、编号、日期、金额）不带历史值，也不必刷一屏「不带值」的说明
        var defsByTag = ctx.Template.Slots.ToDictionary(s => s.Tag);
        var skipped = tags.Where(t => defsByTag.TryGetValue(t, out var d) && d.ForbidInherit).ToList();
        tags = tags.Where(t => !skipped.Contains(t)).ToList();
        if (tags.Count == 0)
        {
            ctx.Reply.Add(skipped.Count > 0
                ? $"当前这几项（{string.Join("、", skipped.Select(t => defsByTag[t].Name))}）是本项目专属信息，" +
                  "不从历史资料带值，直接告诉我就行。"
                : "没有待填的项可推荐了。");
            return;
        }
        var results = await RunSuggestionsAsync(ctx, tags.Take(5).ToList(), ct, text);
        var sb = new StringBuilder("参数顾问查了基准项目文档与知识库：");
        foreach (var (def, sug) in results)
            sb.Append('\n').Append(sug.HasEvidence
                ? $"· {def.Name}：建议「{Truncate(sug.Value ?? "", 40)}」——依据 {EvidenceText(sug)}"
                : $"· {def.Name}：暂无可参考数据（知识库里没有足以支撑的内容，不凭常识编）");
        if (results.Any(r => r.Sug.HasEvidence)) sb.Append("\n说「都采纳」或点采纳；不合适直接说正确的值。");
        ctx.Reply.Add(sb.ToString());

        // 用户说的是「改一下」，而查到的正是现在这个值：把原值还回去等于没答，
        // 转交工况顾问按工艺给替代方案
        var statesNow = Parse(ctx.Session).ToDictionary(s => s.Tag);
        var sameAsCurrent = results.Any(r => r.Sug.Value is not null &&
            string.Equals(statesNow.GetValueOrDefault(r.Def.Tag)?.Value, r.Sug.Value, StringComparison.Ordinal));
        var wantsChange = text is not null && WantsChange.IsMatch(text);
        if (!ctx.Stub && wantsChange && (sameAsCurrent || results.All(r => !r.Sug.HasEvidence)))
            await AdviseAsync(ctx, text, null, ct);
    }

    /// <summary>逐项调既有建议能力（FR-5.9/5.10）；有依据的建议会以「待确认」落到空槽位。</summary>
    private async Task<List<(TemplateSlot Def, SlotSuggestion Sug)>> RunSuggestionsAsync(Ctx ctx, List<string> tags,
        CancellationToken ct, string? hint = null)
    {
        var defs = ctx.Template.Slots.ToDictionary(s => s.Tag);
        var results = new List<(TemplateSlot, SlotSuggestion)>();
        foreach (var tag in tags)
        {
            if (!defs.TryGetValue(tag, out var def) || def.Stage != SlotStage.Current) continue;
            SlotSuggestion sug;
            try { sug = await gen.SuggestAsync(ctx.Session.Id, tag, hint, ct); }
            catch (DomainRuleException) { continue; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                ctx.Reply.Add("检索服务暂时不可用，建议这一步先跳过。");
                break;
            }
            results.Add((def, sug));
            if (sug.HasEvidence)
            {
                ctx.Reply.Changed = true;
                if (sug.Value is not null) ctx.State.LastSuggestions[tag] = sug.Value;
                ctx.Reply.Suggestions.Add(new
                {
                    tag,
                    name = def.Name,
                    value = sug.Value,
                    hasEvidence = true,
                    note = sug.Note,
                    evidence = sug.Evidence.Take(3).Select(e => new { e.SourceTitle, e.Section, e.PageNo }).ToList()
                });
            }
        }
        return results;
    }

    private async Task AdoptAsync(Ctx ctx, IReadOnlyList<string>? tags, string? name, CancellationToken ct)
    {
        ctx.Reply.Handled = true;
        var states = Parse(ctx.Session);
        var pending = states.Where(s => s is { Source: SlotFillSource.AiSuggested, Confirmed: false, Value: not null })
            .Select(s => s.Tag).ToList();
        List<string> targets;
        if (name is not null)
        {
            var def = ResolveSlot(ctx.Template, name);
            targets = def is null ? [] : [def.Tag];
        }
        else targets = tags is { Count: > 0 }
            ? tags.ToList()
            : pending.Concat(ctx.State.LastSuggestions.Keys).Distinct().ToList();
        // 已填过的槽位：建议不会落库成「待采纳」，但用户说了采纳就是要改过去（5.2.2 的用户主动修改）
        var fromLast = targets.Where(x => !pending.Contains(x) && ctx.State.LastSuggestions.ContainsKey(x)).ToList();
        targets = targets.Where(x => pending.Contains(x) || fromLast.Contains(x)).ToList();
        if (targets.Count == 0)
        {
            ctx.Reply.Add(name is not null ? $"「{name}」当前没有待采纳的建议。" : "当前没有待采纳的建议——先说「推荐一下」。");
            return;
        }
        var defs = ctx.Template.Slots.ToDictionary(s => s.Tag);
        foreach (var t in targets)
            await gen.PutSlotAsync(ctx.Session.Id, t,
                fromLast.Contains(t)
                    ? new SlotPut(ctx.State.LastSuggestions[t], true)
                    : new SlotPut(null, true, AdoptSuggestion: true), ct);
        await db.Entry(ctx.Session).ReloadAsync(ct);
        var after = Parse(ctx.Session).ToDictionary(s => s.Tag);
        ctx.Reply.Changed = true;
        ctx.Reply.Add($"已采纳 {targets.Count} 项建议：" +
            string.Join("；", targets.Select(t => $"{defs[t].Name} = {Truncate(after[t].Value ?? "", 24)}")) + "。");
    }

    // ── 专员：工况顾问 ───────────────────────────────────────

    /// <summary>用户交代了工况（「我要做硝化反应」）：按工艺常识判断这会影响哪些选型，
    /// 逐项给建议值 + 理由 + 风险。这是模型的领域知识，不是文档依据——单独一档呈现、
    /// 明确标注需工程师确认、采纳才落表；已有取值与建议不同的会点出来让人对比。
    /// 演示档没有这份知识，如实说明而不是假装。</summary>
    private async Task AdviseAsync(Ctx ctx, string? text, string? focusName, CancellationToken ct)
    {
        ctx.Reply.Handled = true;
        var message = (text ?? "").Trim();
        if (message.Length == 0) return;

        // 「材质换其他的」是对上一轮建议不买账，不是新工况——别把它记成工况
        var (revise, _) = ParseRevise(message);
        if (!revise && !ctx.State.Conditions.Contains(message)) ctx.State.Conditions.Add(message);

        // 点名的那一项（「材质换其他的」→ 材质）；没点名就对上一轮给过的那批重来
        var focus = focusName is null ? null : ResolveSlot(ctx.Template, focusName);
        var redo = revise
            ? (focus is not null ? [focus.Tag] : ctx.State.LastSuggestions.Keys.ToList())
            : new List<string>();

        if (ctx.Stub)
        {
            ctx.Reply.Add(revise
                ? "想换个方案得靠对话模型的工艺知识，当前是内置演示应答给不了。接入对话模型后再说这句我就能换。"
                : $"记下了工况：{Truncate(message, 60)}。" +
                  "按工况推荐选型要靠对话模型的工艺知识，当前是内置演示应答给不了——" +
                  "接入对话模型后我会据此逐项给建议。这条工况已记住，后面问到相关项时会带上。");
            return;
        }

        var defs = CurrentSlots(ctx.Template);
        var byTag = Parse(ctx.Session).ToDictionary(s => s.Tag);
        var sb = new StringBuilder();
        sb.AppendLine(redo.Count > 0
            ? "你是化工实验设备（反应釜等）的资深工艺工程师。用户对你上一版的选型建议不满意，要换方案。"
            : "你是化工实验设备（反应釜等）的资深工艺工程师。用户交代了工况，请判断这个工况对下列参数的选型有什么影响，逐项给建议。");
        sb.AppendLine("只输出一个 JSON：{\"notes\":\"一句话点出这个工况的关键风险\",\"advices\":[" +
            "{\"tag\":\"槽位tag\",\"value\":\"建议取值\",\"level\":\"high 或 normal\",\"reason\":\"为什么\",\"risk\":\"不这样做的风险，没有就留空\"}]}");
        sb.AppendLine("规则：");
        if (redo.Count == 0)
            sb.AppendLine("- 只对这个工况**确实影响**的项给建议，通常 3～8 项；无关的项不要凑数");
        sb.AppendLine("- 选择类槽位的 value 必须是可选值之一；带单位的只给数值与必要修饰");
        sb.AppendLine("- 已有取值若在该工况下不合适，务必给出建议并在 reason 里说明为什么要改");
        sb.AppendLine("- 安全相关（材质耐蚀、压力等级、防爆、连锁、泄压）要重点覆盖，risk 写清楚");
        sb.AppendLine("- level：安全相关（耐蚀、压力等级、防爆、连锁、泄压）或不改就会出事故的写 high，其余写 normal");
        sb.AppendLine("- notes 一句话说完；reason 一句话（40 字以内），risk 一句话（30 字以内）——界面上是可展开的短注解，不是长篇");
        sb.AppendLine("- 你给的是工程判断不是文献结论，不要编造具体标准号或文献出处");
        sb.AppendLine("- value 给一个确定取值，不要在一个值里塞「A（若X则选B）」这种分支；" +
            "有条件差别就写进 reason，让人看得懂为什么是这个值");
        if (redo.Count > 0)
        {
            var names = redo.Select(t => defs.FirstOrDefault(d => d.Tag == t)?.Name ?? t).ToList();
            sb.AppendLine($"- 【这次只重做这几项】{string.Join("、", names)}。" +
                "用户对上一版不满意，要给**明显不同的**方案，不能把上次那条换个说法再来一遍；" +
                "reason 里点明与上一版的取舍差别（贵一些但更耐蚀、加工周期长但扭矩裕度大，诸如此类）");
        }
        sb.AppendLine();
        sb.AppendLine("[已知工况] " + (ctx.State.Conditions.Count == 0 ? "（用户还没明说，从下面的对话里推断）"
            : string.Join("；", ctx.State.Conditions)));
        sb.AppendLine("[项目要点] " + (ctx.Session.ProjectHint ?? "（未提供）"));
        if (ctx.State.LastSuggestions.Count > 0)
        {
            sb.AppendLine("[上一版已经给过的建议]（用户看到的就是这些）");
            foreach (var (tag, val) in ctx.State.LastSuggestions.Take(20))
                sb.AppendLine($"{defs.FirstOrDefault(d => d.Tag == tag)?.Name ?? tag} = {Truncate(val, 60)}");
        }
        sb.AppendLine();
        sb.AppendLine("[参数清单] tag | 名称 | 章节 | 可选值 | 当前值");
        foreach (var d in defs)
        {
            var choices = ChoiceList(d);
            byTag.TryGetValue(d.Tag, out var st);
            sb.AppendLine($"{d.Tag} | {d.Name}{(d.Unit is null ? "" : $"（{d.Unit}）")} | {d.Section}" +
                $" | {(choices is null ? "-" : string.Join("/", choices))}" +
                $" | {(st?.Value is null ? "未填" : Truncate(st.Value, 40))}");
        }

        // 把最近几轮原话带上：用户说「材质换其他的」时，得知道「材质」指的是上一条里那个建议
        var recent = await db.GenChatMessages.AsNoTracking()
            .Where(x => x.SessionId == ctx.Session.Id)
            .OrderByDescending(x => x.Id).Take(6).OrderBy(x => x.Id).ToListAsync(ct);
        var turns = new List<ChatTurn> { new("system", sb.ToString()) };
        foreach (var h in recent)
            turns.Add(new ChatTurn(h.Role == "user" ? "user" : "assistant", Truncate(h.Content, 400)));
        turns.Add(new ChatTurn("user", message));

        string reply;
        try
        {
            reply = await chat.CompleteAsync(turns, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ctx.Reply.Add("工况记下了，但对话模型暂时不可用，按工况的选型建议稍后再给。");
            return;
        }

        var (notes, advices) = ParseAdvice(reply);
        var defMap = defs.ToDictionary(d => d.Tag);
        var accepted = new List<(TemplateSlot Def, EngineeringAdvice Advice, string Value, string? Current)>();
        foreach (var adv in advices)
        {
            if (!defMap.TryGetValue(adv.Tag, out var def)) continue;      // 模型编的 tag 直接丢
            if (redo.Count > 0 && !redo.Contains(adv.Tag)) continue;      // 说了只换这几项就只换这几项
            var (ok, value, _) = Validate(def, adv.Value);               // 选项/日期照样过校验
            if (!ok || value is null) continue;
            // 要求换方案却原样端回来的不算数——那不是换，是复读
            if (redo.Count > 0 && ctx.State.LastSuggestions.TryGetValue(adv.Tag, out var prev)
                && string.Equals(prev.Trim(), value.Trim(), StringComparison.Ordinal)) continue;
            var current = byTag.GetValueOrDefault(adv.Tag)?.Value;
            accepted.Add((def, adv, value, current));
        }

        if (accepted.Count == 0)
        {
            var which = focus?.Name ?? (redo.Count > 0 ? "这几项" : null);
            ctx.Reply.Add(revise
                ? $"没能给出与上一版明显不同的{which ?? "方案"}。说说是哪儿不合适——太贵、加工周期长、还是耐蚀不够——我照着这个方向再选。"
                : string.IsNullOrWhiteSpace(notes)
                    ? "这个工况我记下了，但没得出明确要改的选型项。你也可以直接问某一项该怎么选。"
                    : $"{notes}\n工况已记下；没有得出需要改动的具体选型项，有疑问可以就某一项问我。");
            return;
        }

        // 要紧的排前面：安全相关 > 与现值冲突（要改）> 待补。明细一律交给卡片，正文只留一句摘要——
        // 同一份内容正文复述一遍、卡片再排一遍，读起来最累。
        accepted = accepted
            .OrderByDescending(x => x.Advice.Level == "high")
            .ThenByDescending(x => Kind(x.Value, x.Current) == "change")
            .ToList();
        foreach (var (def, adv, value, current) in accepted)
        {
            ctx.State.LastSuggestions[def.Tag] = value;                   // 说「采纳」时按这份改
            ctx.Reply.Advices.Add(new
            {
                tag = def.Tag, name = def.Name, value, current,
                reason = adv.Reason, risk = adv.Risk,
                level = adv.Level, kind = Kind(value, current)
            });
        }
        var changes = accepted.Count(x => Kind(x.Value, x.Current) == "change");
        var highs = accepted.Count(x => x.Advice.Level == "high");
        ctx.Reply.Add(revise
            ? $"换了个方向，{(focus is null ? "这几项" : focus.Name)}给你重选如下。" +
              (string.IsNullOrWhiteSpace(notes) ? "" : $"\n{notes}")
            : (string.IsNullOrWhiteSpace(notes) ? "按这个工况，有几项要调整。" : notes!) +
              $"\n梳理出 {accepted.Count} 项：{(changes > 0 ? $"{changes} 项与当前填写冲突、" : "")}" +
              $"{(highs > 0 ? $"{highs} 项关系到安全，" : "")}逐条可展开看理由，确认后点采纳。");
        // 刚给了一屏建议就再追问基本信息，注意力会被撕开——这一轮到此为止，等他处理完
        ctx.Reply.SuppressAsks = true;

        // 建议相对现值是「要改」「待补」还是「一致」——界面据此分色，工程师一眼看出哪几项动了
        static string Kind(string value, string? current)
            => string.IsNullOrWhiteSpace(current) ? "fill"
             : string.Equals(current.Trim(), value.Trim(), StringComparison.Ordinal) ? "keep" : "change";
    }

    // ── 专员：答疑员 ─────────────────────────────────────────

    /// <summary>就某一项检索作答，不动表；点不出是哪一项时按整句检索。</summary>
    private async Task AnswerAsync(Ctx ctx, PlanAction a, string text, CancellationToken ct)
    {
        ctx.Reply.Handled = true;
        var question = a.Question ?? text;
        var def = (a.Tag is null ? null : ResolveSlot(ctx.Template, a.Tag))
                  ?? CurrentSlots(ctx.Template).Where(d => question.Contains(d.Name)).OrderByDescending(d => d.Name.Length).FirstOrDefault()
                  ?? ctx.State.LastAsked.Select(t => ctx.Template.Slots.FirstOrDefault(s => s.Tag == t)).FirstOrDefault(s => s != null);
        string answer;
        try
        {
            if (def is not null)
                answer = await gen.SlotChatAsync(ctx.Session.Id, def.Tag, question, ct);
            else
            {
                var result = await retrieval.RetrieveAsync(new RetrievalRequest($"{question} {ctx.Session.ProjectHint ?? ""}"), ct);
                if (!result.AboveThreshold) answer = "知识库中没有找到相关内容，回答不了这个问题。";
                else
                {
                    var sb = new StringBuilder("仅依据[参考内容]回答用户问题，句末标注来源编号；参考内容不足以回答的部分直说。\n[参考内容]\n");
                    for (var i = 0; i < Math.Min(4, result.Chunks.Count); i++)
                        sb.AppendLine($"[{i + 1}]《{result.Chunks[i].DocTitle}》：{result.Chunks[i].Text}");
                    answer = await chat.CompleteAsync([new ChatTurn("system", sb.ToString()), new ChatTurn("user", question)], ct);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            answer = "对话模型暂时不可用，这个问题稍后再问。";
        }
        ctx.Reply.Add((def is null ? "" : $"关于「{def.Name}」：") + answer.Trim());
    }

    // ── 专员：出单员 ─────────────────────────────────────────

    private void AddSummary(Ctx ctx)
    {
        var states = Parse(ctx.Session);
        var check = GenerationService.Completeness(ctx.Template, states);
        ctx.Reply.Handled = true;
        ctx.Reply.SuppressAsks = true;
        ctx.Reply.Add(check.CanRender
            ? $"当前进度 {check.Done}/{check.Total}，必填项已齐，可以生成文档。"
            : $"当前进度 {check.Done}/{check.Total}，还差：{string.Join("、", check.Incomplete.Take(8).Select(i => i.Name))}"
              + (check.Incomplete.Count > 8 ? " 等" : "") + "。");
        ctx.Reply.Summary = SummaryPayload(ctx.Template, states);
        ctx.Reply.CanRender = check.CanRender;
    }

    private async Task RenderAsync(Ctx ctx, CancellationToken ct)
    {
        var states = Parse(ctx.Session);
        var check = GenerationService.Completeness(ctx.Template, states);
        ctx.Reply.Handled = true;
        ctx.Reply.SuppressAsks = true;
        if (!check.CanRender)
        {
            ctx.Reply.Add($"还不能生成：{check.Incomplete.Count} 个必填项未完成——" +
                string.Join("、", check.Incomplete.Take(8).Select(i => i.Name)) +
                (check.Incomplete.Count > 8 ? " 等" : "") + "。补齐后再来。");
            ctx.Reply.Summary = SummaryPayload(ctx.Template, states);
            ctx.Reply.CanRender = false;
            return;
        }
        var r = await gen.RenderAsync(ctx.Session.Id, ct);
        ctx.Reply.Add($"已生成《{r.OutputFileName}》：回填 {r.SlotsFilled} 项，留空 {r.LeftBlank} 项。文件页眉带「待复核」标注，请下载核对后再对外使用。");
        ctx.Reply.Rendered = new { fileName = r.OutputFileName, filled = r.SlotsFilled, blank = r.LeftBlank };
    }

    // ── 专员：译员 ───────────────────────────────────────────

    /// <summary>出整篇译文：拿当前草稿（与最终产出同一条填充路径）整篇翻译并按原版式回填。
    ///
    /// 这件事以前是被模型一句「机翻会把材质牌号和安全条款译错」挡回去的——挡错了：
    /// 术语一致本来就不靠模型自觉，而是靠已审定术语表在提示词里强制对照（FR-6.1），
    /// 这套东西存在的理由就是解决牌号与条款的译法。所以这里只管把译文出出来，
    /// 同时把「哪些段没能回填」「命中了哪些术语」「合同类要人工复核」如实摆在明处（FR-6.3、FR-6.6）。</summary>
    private async Task TranslateAsync(Ctx ctx, string? direction, CancellationToken ct)
    {
        ctx.Reply.Handled = true;
        ctx.Reply.SuppressAsks = true;   // 译文是一件产出，别在它后面又接着追问槽位
        var dir = direction?.Trim().ToLowerInvariant() == "en2zh" ? "en2zh" : "zh2en";
        var lang = dir == "zh2en" ? "英文" : "中文";
        if (!me.Permissions.Contains(PermissionKeys.Translate))
        {
            ctx.Reply.Add($"{lang}版我这边出不了：你的角色没有开通翻译权限。找管理员在角色里加上就行，文档本身不用重做。");
            return;
        }
        if (ctx.Stub)
        {
            // 演示应答下硬出一份，得到的是一篇假译文——那比不出更糟
            ctx.Reply.Add($"{lang}版要真译才有意义，当前是内置演示应答，出来的只会是占位文本。" +
                "在设置里把对话模型接上（在线接口或本地模型都行），这份文档不用重做，说一声就能出。");
            return;
        }

        var draft = await gen.RenderDraftAsync(ctx.Session.Id, ct);
        FileTranslationResult r;
        try
        {
            using var ms = new MemoryStream(draft.Content);
            r = await translation.TranslateDocxAsync(ms, $"{ctx.Template.Name}.docx", dir, null, ct);
        }
        catch (DomainRuleException ex)
        {
            // 模型不可用一类：说清楚是哪一步没成，别含糊成「译不了」
            ctx.Reply.Add($"{lang}版这次没出来——{ex.Message}");
            return;
        }

        var parts = new List<string>
        {
            $"{lang}版出好了：《{r.OutputFileName}》，全篇 {r.Report.Paragraphs} 段译出 {r.Report.Translated} 段，版式按原件回填。"
        };
        if (r.TermsApplied.Count > 0)
            parts.Add($"其中 {r.TermsApplied.Count} 条已审定术语按标准译法统一，全篇一个写法。");
        if (r.Report.Unfillable.Count > 0)
            parts.Add($"有 {r.Report.Unfillable.Count} 处没能回填（清单在下面），这些位置还是原文，得人工补。");
        if (draft.Blank > 0)
            parts.Add($"中文稿里留空的 {draft.Blank} 项，{lang}版同样是空的——先把中文补齐再出一版更省事。");
        parts.Add("机器译文须人工复核后再对外。");
        ctx.Reply.Add(string.Join("", parts));

        ctx.Reply.Translated = new
        {
            taskId = r.TaskId,
            fileName = r.OutputFileName,
            direction = dir,
            paragraphs = r.Report.Paragraphs,
            translated = r.Report.Translated,
            unfillable = r.Report.Unfillable,
            terms = r.TermsApplied,
            notice = r.ContractNotice,
            draftBlank = draft.Blank
        };
    }

    // ── 收尾：下一批提问 + 主动性 ──────────────────────────────

    private async Task FinalizeAsync(Ctx ctx, CancellationToken ct)
    {
        var reply = ctx.Reply;
        var state = ctx.State;
        if (!reply.SuppressAsks)
        {
            var states = Parse(ctx.Session);
            var byTag = states.ToDictionary(s => s.Tag);
            var keep = !reply.Changed && state.LastAsked.Any(t => IsOpen(byTag.GetValueOrDefault(t)));
            var asks = keep ? AskDefs(ctx.Template, state.LastAsked, byTag) : NextAsks(ctx.Template, states, state, ctx.Batch);
            // 这一轮刚落进去的项不再问第二遍。状态算错也好、值被别处盖掉也好，
            // 「我刚给了你还问」是最让人火大的一种错，这里堵死
            if (reply.FilledNow.Count > 0)
            {
                asks = asks.Where(a => !reply.FilledNow.Contains(a.Tag)).ToList();
                if (asks.Count == 0 && !keep)
                    asks = NextAsks(ctx.Template, states, state, ctx.Batch)
                        .Where(a => !reply.FilledNow.Contains(a.Tag)).ToList();
            }
            if (asks.Count == 0)
            {
                var check = GenerationService.Completeness(ctx.Template, states);
                reply.Add(check.CanRender
                    ? $"全部必填项已完成（{check.Done}/{check.Total}）。确认汇总无误后就可以生成文档。"
                    : $"待问的问完了，但还有 {check.Incomplete.Count} 项未完成：" +
                      string.Join("、", check.Incomplete.Take(6).Select(i => i.Name)) + "。补上才能生成。");
                reply.Summary = SummaryPayload(ctx.Template, states);
                reply.CanRender = check.CanRender;
                state.LastAsked = [];
            }
            else
            {
                // 进入新章节：先看基准文档与知识库里有没有依据充分的建议，有就先给再问（每章节一次）
                var section = asks[0].Section;
                if (section != state.LastSection && !state.SuggestedSections.Contains(section) && reply.Suggestions.Count == 0)
                {
                    state.SuggestedSections.Add(section);
                    var forbid = ctx.Template.Slots.Where(s => s.ForbidInherit).Select(s => s.Tag).ToHashSet();
                    var openTags = asks.Where(a => byTag.GetValueOrDefault(a.Tag)?.Value is null && !forbid.Contains(a.Tag))
                        .Select(a => a.Tag).ToList();
                    var results = openTags.Count == 0
                        ? new List<(TemplateSlot Def, SlotSuggestion Sug)>()
                        : await RunSuggestionsAsync(ctx, openTags, ct);
                    var hits = results.Where(r => r.Sug.HasEvidence).ToList();
                    if (hits.Count > 0)
                    {
                        var sb = new StringBuilder($"进入【{section}】前，知识库里有参考的先给你（说「都采纳」或直接给正确的值）：");
                        foreach (var (def, sug) in hits)
                            sb.Append($"\n· {def.Name}：建议「{Truncate(sug.Value ?? "", 40)}」——依据 {EvidenceText(sug)}");
                        reply.Add(sb.ToString());
                        states = Parse(ctx.Session);
                        byTag = states.ToDictionary(s => s.Tag);
                        asks = AskDefs(ctx.Template, asks.Select(a => a.Tag).ToList(), byTag);
                    }
                }
                state.LastSection = section;
                state.LastAsked = asks.Select(a => a.Tag).ToList();
                reply.Asks = asks;
                reply.Add(AskText(asks));
            }
        }

        // 主动翻台账：还没定基准，且（还没递过卡 / 换了客户）——报出客户或设备就自动查。
        // 基准可能是在工作台里选的（没经过对话），所以要看会话本身有没有基准，不能只看对话状态
        if (ctx.Session.BaseProjectNo is not null) state.BaseDecided = true;
        if (!state.BaseDecided && reply.BaseCandidates is null)
        {
            var (customers, devices) = await VocabAsync(ct);
            var f = IntentRouter.ExtractFilters(ContextText(ctx), customers, devices);
            var key = f.CustomerName ?? f.DeviceType;
            // 没有任何线索（没要点、没报客户/设备）时不递卡——随手列几个最近项目冒充「相近」是误导
            var hasClue = key is not null || !string.IsNullOrWhiteSpace(ctx.Session.ProjectHint);
            if (hasClue && (!state.BaseOffered || (f.CustomerName is not null && f.CustomerName != state.OfferedFor)))
            {
                var found = f.CustomerName is null && f.DeviceType is null
                    ? await gen.GetCandidatesAsync(ctx.Session.Id, ct)
                    : await gen.FindCandidatesAsync(ctx.Session.Id, f.CustomerName, f.DeviceType, null, 3, ct);
                state.BaseOffered = true;
                state.OfferedFor = f.CustomerName ?? state.OfferedFor;
                if (found.Count > 0)
                {
                    state.LastCandidates = found.Select(c => c.ProjectNo).ToList();
                    reply.Add((key is null ? "顺便翻了台账，有相近的历史项目" : $"顺便翻了台账，{key}有 {found.Count} 个历史项目") +
                              "——要不要拿一个做基准把能继承的参数带进来？点卡片或说「用第一个」，也可以不用。");
                    reply.BaseCandidates = Cards(found);
                }
            }
        }
    }

    // ── 槽位与提问 ────────────────────────────────────────────

    private static bool IsOpen(SlotState? s)
        => s?.Value is null || s is { Source: SlotFillSource.AiSuggested, Confirmed: false };

    /// <summary>下一批提问：按模板顺序取未填（或有建议待确认）的当前阶段槽位，同章节成批；跳过的排最后。</summary>
    private static List<Ask> NextAsks(Template template, List<SlotState> states, ChatState state, int batch)
    {
        var byTag = states.ToDictionary(s => s.Tag);
        var open = CurrentSlots(template).Where(s => IsOpen(byTag.GetValueOrDefault(s.Tag))).ToList();
        var pool = open.Where(s => !state.Skipped.Contains(s.Tag)).ToList();
        if (pool.Count == 0) pool = open;
        if (pool.Count == 0) return [];
        var section = pool[0].Section;
        return pool.Where(s => s.Section == section).Take(batch).Select(s => ToAsk(s, byTag.GetValueOrDefault(s.Tag))).ToList();
    }

    private static List<Ask> AskDefs(Template template, List<string> tags, Dictionary<string, SlotState> byTag)
        => template.Slots.Where(s => tags.Contains(s.Tag) && IsOpen(byTag.GetValueOrDefault(s.Tag)))
            .OrderBy(s => s.SortOrder).Select(s => ToAsk(s, byTag.GetValueOrDefault(s.Tag))).ToList();

    private static Ask ToAsk(TemplateSlot s, SlotState? st)
        => new(s.Tag, s.Name, s.Section, s.DataType.ToString(), ChoiceList(s), s.Prompt, s.Unit, s.Required,
            st is { Source: SlotFillSource.AiSuggested, Confirmed: false } ? st.Value : null);

    private static string AskText(List<Ask> asks)
    {
        var sb = new StringBuilder($"【{asks[0].Section}】");
        foreach (var a in asks)
        {
            sb.Append("\n· ").Append(a.Name);
            if (a.Unit is not null) sb.Append($"（{a.Unit}）");
            if (a.Suggested is not null) sb.Append($"（建议「{Truncate(a.Suggested, 24)}」待确认）");
            if (a.Choices is not null) sb.Append("：").Append(string.Join(" / ", a.Choices));
            else if (a.Prompt is not null && a.Suggested is null) sb.Append("——").Append(a.Prompt);
        }
        return sb.ToString();
    }

    private static string EvidenceText(SlotSuggestion sug)
        => string.Join("；", sug.Evidence.Take(2).Select(e =>
            $"《{e.SourceTitle}》{(e.Section is null ? "" : " › " + e.Section)}{(e.PageNo is null ? "" : $" 第 {e.PageNo} 页")}"));

    private static object Cards(IReadOnlyList<BaseCandidate> found)
        => found.Select(c => new { c.ProjectNo, c.CustomerName, c.Year, c.DeviceType, c.DeviceModel, c.InheritableSlots }).ToList();

    private static List<TemplateSlot> CurrentSlots(Template t)
        => t.Slots.Where(s => s.Stage == SlotStage.Current).OrderBy(s => s.SortOrder).ToList();

    /// <summary>按 tag 或显示名称找槽位（精确优先，其次互相包含且最长）。</summary>
    private static TemplateSlot? ResolveSlot(Template t, string key)
    {
        key = key.Trim();
        // 空串不能往下走：Contains("") 恒真，会「认出」名字最长的那一项，把值落到八竿子打不着的槽位
        if (key.Length == 0) return null;
        var current = CurrentSlots(t);
        return current.FirstOrDefault(s => s.Tag == key || s.Name == key)
               ?? (key.Length < 2 ? null
                   : current.Where(s => s.Name.Length >= 2 && (key.Contains(s.Name) || s.Name.Contains(key)))
                       .OrderByDescending(s => s.Name.Length).FirstOrDefault());
    }

    /// <summary>主动查台账的依据：项目要点 + 已填的客户/设备类槽位值。</summary>
    private static string ContextText(Ctx ctx)
    {
        var byTag = Parse(ctx.Session).ToDictionary(s => s.Tag);
        var parts = new List<string> { ctx.Session.ProjectHint ?? "" };
        foreach (var s in ctx.Template.Slots)
        {
            var hint = s.Tag.Contains("customer", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("客户") ||
                       s.Tag.Contains("device", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("设备");
            if (hint && byTag.GetValueOrDefault(s.Tag)?.Value is { } v) parts.Add(v);
        }
        return string.Join(" ", parts);
    }

    private string Greeting(Template template)
    {
        var current = CurrentSlots(template);
        var sections = current.GroupBy(s => s.Section).Select(g => $"{g.Key}（{g.Count()} 项）");
        return $"开始填《{template.Name}》，共 {current.Count} 项：{string.Join("、", sections)}。\n" +
               "可以整句描述项目（如「华东理工的 10L 反应釜，316L 材质，法兰结构，电加热」），能确定的项自动填入；" +
               "也可以让我查历史做过的项目拿来做基准、推荐参数、或就某一项提问。随时可以说「跳过」「汇总」「生成文档」。";
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
            var hit = MatchChoice(v, choices);
            return hit is null
                ? (false, null, $"「{def.Name}」的取值「{Truncate(v, 20)}」不在可选项里（可选：{string.Join(" / ", choices)}）")
                : (true, hit, null);
        }
        if (def.DataType == SlotDataType.Date)
        {
            var d = NormalizeDate(v, DateTimeOffset.Now.Year);
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
            Value = value, Source = SlotFillSource.Confirmed, Confirmed = true, UserTouched = true,
            Origin = "对话填写", UpdatedAt = DateTimeOffset.UtcNow
        };
        if (idx >= 0) states[idx] = updated; else states.Add(updated);
    }

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

    private static object SummaryPayload(Template template, List<SlotState> states)
    {
        var byTag = states.ToDictionary(s => s.Tag);
        return CurrentSlots(template).GroupBy(s => s.Section)
            .Select(g => new
            {
                section = g.Key,
                items = g.Select(s => new { name = s.Name, value = byTag.GetValueOrDefault(s.Tag)?.Value }).ToList()
            }).ToList();
    }

    // ── 基建 ────────────────────────────────────────────────

    private async Task<(List<string> Customers, List<string> Devices)> VocabAsync(CancellationToken ct)
    {
        var customers = await db.VocabTerms.AsNoTracking()
            .Where(v => v.VocabKey == VocabKeys.CustomerName && v.IsActive).Select(v => v.Value).ToListAsync(ct);
        var devices = await db.VocabTerms.AsNoTracking()
            .Where(v => v.VocabKey == VocabKeys.DeviceType && v.IsActive).Select(v => v.Value).ToListAsync(ct);
        return (customers, devices);
    }

    private async Task<bool> IsStubAsync(CancellationToken ct)
    {
        var fallback = ModelSlotDefaults.UseStubs(appConfig) ? "stub" : "openai";
        var provider = await config.GetStringAsync(ConfigKeys.ChatProvider, fallback, ct);
        return provider.Trim().Equals("stub", StringComparison.OrdinalIgnoreCase);
    }

    private static List<SlotState> Parse(GenerationSession s) => GenerationService.Parse(s.SlotValues);

    private static GenChatMessage User(GenerationSession s, string content)
        => new() { SessionId = s.Id, Role = "user", Content = content, At = DateTimeOffset.UtcNow };

    private static GenChatMessage Assistant(GenerationSession s, string content, object? payload)
        => new()
        {
            SessionId = s.Id, Role = "assistant", Content = content,
            Payload = payload is null ? null : JsonSerializer.Serialize(payload, Json),
            At = DateTimeOffset.UtcNow
        };

    private static GenChatMessageView ToView(GenChatMessage m) => new(m.Id, m.Role, m.Content, m.Payload, m.At);

    private static GenChatProgress Progress(GenerationSession session, Template template)
    {
        var check = GenerationService.Completeness(template, Parse(session));
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
