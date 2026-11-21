using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HT.Agent.Application.Logic;

/// <summary>首页每天的内容：不同时段的一句问候、「今天有点意思」（今日知识、猜一猜、化工史上的一件事、一个数字）、
/// 最要紧的几件正事、没事时可以问的问题。
/// 正事的候选和上面的数字由系统从价格库、资料库、台账等取出、算好；模型只挑重点、写那一句话，不编数字。
/// 这里是纯逻辑：拼提示词、解析模型输出、模型不可用时的兜底。</summary>
public static class HomeBrief
{
    /// <summary>一件可能跟这个人有关的事。Badge 是系统算出的数字标记，Facts 是交给模型看的事实。</summary>
    public record Candidate(string Id, string Kind, string Emoji, string Badge, string Tone, string Facts,
        string Title, string Detail, string Action, string Prompt);

    public record Line(string Title, string Sub);
    public record Work(string Id, string Emoji, string Title, string Badge, string Tone, string Detail, string Action, string Prompt);

    public record Knowledge(string Title, string Body);
    public record Quiz(string Question, List<string> Options, int Answer, string Explain);
    public record History(string Year, string Text);
    public record Number(string Value, string Text);
    public record Fun(Knowledge? Knowledge, Quiz? Quiz, History? History, Number? Number);

    public record Result(Dictionary<string, Line> Lines, Fun Fun, List<Work> Work, List<string> Quiet, bool Generated);

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
            语气：像一个靠谱又有点幽默的同事，口语、简短、真诚，可以用 1 个表情符号点缀；不要鸡汤，不要说「作为 AI」，不要编造没给你的事实和数字。
            「今天有点意思」是让人放松一下、看了觉得有意思的：跟化工、材料、设备、实验有关的冷知识和小故事，最好贴近他常碰的东西；必须是你非常确定的真实知识，拿不准的宁可换一个。
            """);
        if (c.Memories.Count > 0) sb.AppendLine("他的长期记忆：" + string.Join("；", c.Memories.Take(8)));
        if (c.RecentQuestions.Count > 0) sb.AppendLine("他最近问过：" + string.Join("；", c.RecentQuestions.Take(8)));
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
                "morning":   {"title": "刚上班时的大标题（≤16字）", "sub": "一句小字（≤30字），可以提今天有几件正事"},
                "noon":      {"title": "...", "sub": "..."},
                "afternoon": {"title": "下午犯困时", "sub": "..."},
                "evening":   {"title": "快下班时", "sub": "..."},
                "late":      {"title": "晚上还在加班时", "sub": "..."},
                "friday":    {"title": "周五下午", "sub": "..."}
              },
              "knowledge": {"title": "今日知识的一句话标题（≤22字，有点反常识最好）", "body": "两三句讲清楚（≤90字）"},
              "quiz": {"question": "一道三选一的小题（≤26字）", "options": ["A", "B", "C"], "answer": 正确选项的序号 0/1/2, "explain": "揭晓时的一句解释（≤50字）"},
              "history": {"year": "年份，如 1913", "text": "化工史上的一件事（≤40字）"},
              "number": {"value": "一个数字，如 1.25×、6.022×10²³、1450 ℃", "text": "这个数字是什么（≤40字）"},
              "work": [{"id": "上面的编号", "title": "一句话标题（≤24字）", "detail": "对他的影响或细节（≤36字）", "action": "按钮上的 2–4 个字", "prompt": "点按钮后替他发给助手的完整问题"}],
              "quiet": ["没有正事时，可以问的 3 个问题（按他的角色和最近的工作写，每个 ≤30 字）"]
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
                    lines[k] = new Line(t, Clip(l["sub"]?.ToString(), 60));
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
        var quiet = (o["quiet"] as JsonArray ?? []).Select(x => Clip(x?.ToString(), 40)).Where(x => x.Length > 0).Take(3).ToList();
        Knowledge? kn = o["knowledge"] is JsonObject ko && Clip(ko["title"]?.ToString(), 40) is { Length: > 0 } kt
            ? new Knowledge(kt, Clip(ko["body"]?.ToString(), 160)) : null;
        Quiz? quiz = null;
        if (o["quiz"] is JsonObject qo && Clip(qo["question"]?.ToString(), 50) is { Length: > 0 } qq)
        {
            var opts = (qo["options"] as JsonArray ?? []).Select(x => Clip(x?.ToString(), 30)).Where(x => x.Length > 0).Take(4).ToList();
            var ans = qo["answer"] is JsonValue av && av.TryGetValue<int>(out var ai) ? ai : int.TryParse(qo["answer"]?.ToString(), out var ai2) ? ai2 : -1;
            if (opts.Count >= 2 && ans >= 0 && ans < opts.Count) quiz = new Quiz(qq, opts, ans, Clip(qo["explain"]?.ToString(), 90));
        }
        History? hist = o["history"] is JsonObject ho && Clip(ho["text"]?.ToString(), 70) is { Length: > 0 } ht
            ? new History(Clip(ho["year"]?.ToString(), 8), ht) : null;
        Number? num = o["number"] is JsonObject no && Clip(no["value"]?.ToString(), 16) is { Length: > 0 } nv
            ? new Number(nv, Clip(no["text"]?.ToString(), 70)) : null;
        return new Result(lines, new Fun(kn, quiz, hist, num), work, quiet.Count > 0 ? quiet : fb.Quiet, true);
    }

    /// <summary>模型连不上时：系统写的平实台词，正事按候选顺序取前 3 件。不放假的示例问题。</summary>
    public static Result Fallback(Context c)
    {
        var n = c.Candidates.Count;
        var todo = n > 0 ? $"今天有 {Math.Min(n, 3)} 件事可能跟你有关。" : "今天没有跟你有关的新变动。";
        var lines = new Dictionary<string, Line>
        {
            ["morning"] = new($"早，{c.Name} ☀️", todo),
            ["noon"] = new("先去吃饭 🍜", "回来再说，事情跑不掉。"),
            ["afternoon"] = new("下午了，喝口水 ☕", todo),
            ["evening"] = new("快下班了", "今天辛苦了，剩下的明天接着来。"),
            ["late"] = new("这么晚还在，辛苦了 🌙", "早点休息。"),
            ["friday"] = new("周五啦 🎉", "收个尾，周末好好歇。"),
        };
        var work = c.Candidates.Take(3).Select(x => new Work(x.Id, x.Emoji, x.Title, x.Badge, x.Tone, x.Detail, x.Action, x.Prompt)).ToList();
        return new Result(lines, new Fun(null, null, null, null), work, [], false);
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
