using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HT.Agent.Application.Logic;

/// <summary>首页每天的内容：不同时段的一句问候（只说生活）、最要紧的几件正事。
/// 正事的候选和上面的数字由系统从价格库、资料库、台账等取出、算好；模型只挑重点、写那一句话，不编数字。
/// 这里是纯逻辑：拼提示词、解析模型输出、模型不可用时的兜底。</summary>
public static class HomeBrief
{
    /// <summary>一件可能跟这个人有关的事。Badge 是系统算出的数字标记，Facts 是交给模型看的事实。</summary>
    public record Candidate(string Id, string Kind, string Emoji, string Badge, string Tone, string Facts,
        string Title, string Detail, string Action, string Prompt);

    public record Line(string Title, string Sub);
    public record Work(string Id, string Emoji, string Title, string Badge, string Tone, string Detail, string Action, string Prompt);

    public record Result(Dictionary<string, Line> Lines, List<Work> Work, bool Generated);

    public static readonly string[] Slots = ["morning", "noon", "afternoon", "evening", "late", "friday"];

    public record Context(string Name, string Role, string? Department, DateTimeOffset Now, IReadOnlyList<string> Memories,
        IReadOnlyList<string> RecentQuestions, IReadOnlyList<Candidate> Candidates, int Drafts);

    public static string Prompt(Context c)
    {
        var week = "日一二三四五六"[(int)c.Now.DayOfWeek];
        var sb = new StringBuilder();
        sb.AppendLine($"""
            你是公司内部助手（化工实验设备公司：反应釜、高压釜、合成装置等）的吉祥物 HTLAB，要给同事写今天首页上的内容。
            同事：{c.Name}，{c.Role}{(string.IsNullOrWhiteSpace(c.Department) ? "" : $"，{c.Department}")}。今天是 {c.Now:M 月 d 日} 星期{week}。
            语气：像一个靠谱、温和的同事，口语、简短、真诚，可以用 1 个表情符号点缀；不要鸡汤，不要说「作为 AI」，不要编造没给你的事实和数字。
            「lines」里的问候（大标题和小字）是首页最显眼的一句，老板、客户都可能看到，要得体：
            - 只说生活：天气和季节、吃饭喝水、休息、身体、周末、节气节日、通勤、家人、小小的好心情；
            - 不提工作和行业：不说设备、反应釜、报价、客户、项目、加班、截止、「在等你」这类催促的话；
            - 不开任何负面的玩笑：不说爆炸、着火、泄漏、事故、出错、挨骂、背锅、扣钱、熬夜、累，不调侃人。
            """);
        if (c.Drafts > 0) sb.AppendLine($"他还有 {c.Drafts} 份没填完的文档。");
        if (c.Candidates.Count > 0)
        {
            sb.AppendLine("今天可能跟他有关的事（编号 + 事实，数字已由系统算好）：");
            foreach (var x in c.Candidates) sb.AppendLine($"- {x.Id}：{x.Facts}");
        }
        else sb.AppendLine("今天没有跟他有关的新变动。");
        sb.AppendLine("""
            只输出一个 JSON 对象，不要别的文字：
            {
              "lines": {
                "morning":   {"title": "刚上班时的大标题（≤16字）", "sub": "一句小字（≤30字），说生活，不提工作"},
                "noon":      {"title": "...", "sub": "..."},
                "afternoon": {"title": "下午犯困时", "sub": "..."},
                "evening":   {"title": "快下班时", "sub": "..."},
                "late":      {"title": "晚上还在加班时", "sub": "..."},
                "friday":    {"title": "周五下午", "sub": "..."}
              },
              "work": [{"id": "上面的编号", "title": "一句话标题（≤24字）", "detail": "对他的影响或细节（≤36字）", "action": "按钮上的 2–4 个字", "prompt": "点按钮后替他发给助手的完整问题"}]
            }
            work 只从上面的编号里挑最要紧的，最多 3 件，没有就给空数组。
            """);
        return sb.ToString();
    }

    /// <summary>模型输出 → 结果。缺的部分用兜底补上；work 只认候选里的编号，数字一律用系统的。</summary>
    public static Result Parse(string? raw, Context c)
    {
        var fb = Fallback(c);
        if (string.IsNullOrWhiteSpace(raw)) return fb;
        var text = raw;
        var think = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (think >= 0) text = text[(think + 8)..];
        var a = text.IndexOf('{'); var b = text.LastIndexOf('}');
        if (a < 0 || b <= a) return fb;
        JsonObject? o;
        try { o = JsonNode.Parse(text[a..(b + 1)]) as JsonObject; }
        catch (JsonException) { return fb; }
        if (o is null) return fb;

        static string Clip(string? s, int n) { s = (s ?? "").Trim(); return s.Length > n ? s[..n] : s; }
        var lines = new Dictionary<string, Line>(fb.Lines);
        if (o["lines"] is JsonObject lo)
            foreach (var k in Slots)
                if (lo[k] is JsonObject l && Clip(l["title"]?.ToString(), 30) is { Length: > 0 } t)
                {
                    var sub = Clip(l["sub"]?.ToString(), 60);
                    // 模型没守住（开了事故玩笑、提了工作行业）：这一段用系统写的平实台词，不让不得体的话上首页
                    if (!Unfit(t) && !Unfit(sub)) lines[k] = new Line(t, sub);
                }
        var work = new List<Work>();
        var byId = c.Candidates.ToDictionary(x => x.Id);
        if (o["work"] is JsonArray wa)
            foreach (var w in wa.OfType<JsonObject>())
            {
                if (work.Count >= 3) break;
                var id = w["id"]?.ToString() ?? "";
                if (!byId.TryGetValue(id, out var cand) || work.Any(x => x.Id == id)) continue;
                work.Add(new Work(id, cand.Emoji,
                    Clip(w["title"]?.ToString(), 40) is { Length: > 0 } t ? t : cand.Title, cand.Badge, cand.Tone,
                    Clip(w["detail"]?.ToString(), 60) is { Length: > 0 } d ? d : cand.Detail,
                    Clip(w["action"]?.ToString(), 6) is { Length: > 0 } act ? act : cand.Action,
                    Clip(w["prompt"]?.ToString(), 200) is { Length: > 0 } p ? p : cand.Prompt));
            }
        if (work.Count == 0) work = fb.Work;
        return new Result(lines, work, true);
    }

    /// <summary>首页问候不该出现的：事故、负面、催促的说法，以及工作和行业的词（问候只说生活）。</summary>
    private static readonly System.Text.RegularExpressions.Regex Taboo = new(
        "炸|爆|着火|失火|起火|泄漏|漏了|事故|出事|中毒|受伤|死|翻车|崩|挂了|背锅|挨骂|骂|扣钱|罚|熬夜|通宵|累死|加班|截止|deadline|催|在等你|等着你|别偷懒|摸鱼|"
        + "反应釜|釜|设备|压力容器|报价|客户|项目|订单|合同|图纸|标准|老板|领导|KPI|绩效",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>这句问候不得体（有不该出现的词）。</summary>
    public static bool Unfit(string? text) => !string.IsNullOrEmpty(text) && Taboo.IsMatch(text);

    /// <summary>模型连不上时：系统写的平实台词（只说生活），正事按候选顺序取前 3 件。</summary>
    public static Result Fallback(Context c)
    {
        var lines = new Dictionary<string, Line>
        {
            ["morning"] = new($"早，{c.Name} ☀️", "新的一天，先喝杯水，慢慢进入状态。"),
            ["noon"] = new("先去吃饭 🍜", "吃好饭，下午才有精神。"),
            ["afternoon"] = new("下午了，喝口水 ☕", "起来走两步，活动一下肩颈。"),
            ["evening"] = new("快下班了", "今天辛苦了，路上注意安全。"),
            ["late"] = new("这么晚还在，辛苦了 🌙", "早点休息。"),
            ["friday"] = new("周五啦 🎉", "周末好好歇歇，陪陪家人。"),
        };
        var work = c.Candidates.Take(3).Select(x => new Work(x.Id, x.Emoji, x.Title, x.Badge, x.Tone, x.Detail, x.Action, x.Prompt)).ToList();
        return new Result(lines, work, false);
    }

    /// <summary>按当前时间挑哪一段台词：周五下午优先，其次按钟点。</summary>
    public static string SlotOf(DateTimeOffset now)
    {
        var h = now.Hour;
        if (now.DayOfWeek == DayOfWeek.Friday && h >= 14 && h < 20) return "friday";
        return h switch
        {
            < 5 => "late",
            < 11 => "morning",
            < 14 => "noon",
            < 17 => "afternoon",
            < 20 => "evening",
            _ => "late",
        };
    }
}
