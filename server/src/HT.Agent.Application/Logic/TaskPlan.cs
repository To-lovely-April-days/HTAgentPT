using System.Text.Json.Nodes;

namespace HT.Agent.Application.Logic;

/// <summary>回答里的任务清单：模型用「列计划」工具列出、更新；系统兜底（下一步开始时上一步算完成、答完时没做的标出来），
/// 把这一步进行中调的工具挂到这一步下面。状态是 todo（没开始）、now（正在做）、done（做完）、skip（没做 / 不做了）。</summary>
public sealed class TaskPlan
{
    public sealed class Item
    {
        public required string Title { get; set; }
        public string Status { get; set; } = "todo";
        public string? Note { get; set; }
        public DateTimeOffset? Started { get; set; }
        public DateTimeOffset? Ended { get; set; }
        public List<string> Steps { get; } = [];
    }

    public const int MaxItems = 7;
    public List<Item> Items { get; private set; } = [];
    /// <summary>改过几次计划（标题变了才算，只改状态不算）。</summary>
    public int Revisions { get; private set; }
    /// <summary>每次改计划的原因。</summary>
    public List<string> Reasons { get; } = [];
    public DateTimeOffset? Started { get; private set; }
    public bool Finished { get; private set; }
    /// <summary>这次按什么假设做（用户没说清、按常见做法定下来的）：开工前判断时列出，界面上跟计划一起显示。</summary>
    public List<string> Assumptions { get; } = [];
    /// <summary>计划是系统开工前判断时列的（不是模型自己列的）。</summary>
    public bool Seeded { get; private set; }

    /// <summary>开工前系统判断出来的计划：第一步就开始做，其余没开始。</summary>
    public void Seed(IEnumerable<string> steps, IEnumerable<string> assumptions, DateTimeOffset now)
    {
        Items = steps.Select(t => t.Trim()).Where(t => t.Length > 0).Take(MaxItems)
            .Select((t, i) => new Item { Title = t.Length > 60 ? t[..60] : t, Status = i == 0 ? "now" : "todo", Started = i == 0 ? now : null }).ToList();
        Assumptions.Clear();
        Assumptions.AddRange(assumptions.Select(a => a.Trim()).Where(a => a.Length > 0).Take(5));
        Started = now;
        Seeded = Items.Count > 0;
    }

    /// <summary>存档（停下来等用户回话时随现场存，回来接着做）。</summary>
    public sealed record Snap(List<SnapItem> Items, List<string> Assumptions, int Revisions, List<string> Reasons, DateTimeOffset? Started, bool Seeded);
    public sealed record SnapItem(string Title, string Status, string? Note, DateTimeOffset? Started, DateTimeOffset? Ended, List<string> Steps);

    public Snap? ToSnap() => Items.Count == 0 && Assumptions.Count == 0 ? null
        : new([.. Items.Select(i => new SnapItem(i.Title, i.Status, i.Note, i.Started, i.Ended, [.. i.Steps]))], [.. Assumptions], Revisions, [.. Reasons], Started, Seeded);

    public void Restore(Snap? s)
    {
        if (s is null) return;
        Items = s.Items.Select(x => { var it = new Item { Title = x.Title, Status = x.Status, Note = x.Note, Started = x.Started, Ended = x.Ended }; it.Steps.AddRange(x.Steps); return it; }).ToList();
        Assumptions.Clear(); Assumptions.AddRange(s.Assumptions);
        Revisions = s.Revisions;
        Reasons.Clear(); Reasons.AddRange(s.Reasons);
        Started = s.Started;
        Seeded = s.Seeded;
    }

    public Item? Current => Items.FirstOrDefault(i => i.Status == "now");

    static string Norm(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "now" or "doing" or "in_progress" or "running" => "now",
        "done" or "completed" or "ok" => "done",
        "skip" or "skipped" or "cancelled" or "canceled" => "skip",
        _ => "todo",
    };

    /// <summary>模型交来整张清单（每次都是全量）。返回 null 表示收下；否则是要模型改的话。</summary>
    public string? Update(JsonArray? items, string? reason, DateTimeOffset now)
    {
        var rows = (items ?? []).OfType<JsonObject>()
            .Select(o => (Title: (o["title"]?.ToString() ?? "").Trim(), Status: Norm(o["status"]?.ToString()), Note: o["note"]?.ToString()?.Trim()))
            .ToList();
        if (rows.Count == 0) return "items 至少要有一步（title 写这一步做什么，status 写 todo / now / done / skip）。";
        if (rows.Count > MaxItems) return $"计划最多 {MaxItems} 步，把相近的合并。";
        if (rows.Any(r => r.Title.Length == 0)) return "每一步都要写 title。";

        var old = Items;
        var changed = old.Count > 0 && !old.Select(i => i.Title).SequenceEqual(rows.Select(r => r.Title));
        var next = new List<Item>();
        var seenNow = false;
        foreach (var r in rows)
        {
            var prev = old.FirstOrDefault(i => i.Title == r.Title);
            var st = r.Status;
            if (st == "now" && seenNow) st = "todo";      // 同一时间只做一步
            if (st == "now") seenNow = true;
            var it = new Item { Title = r.Title.Length > 60 ? r.Title[..60] : r.Title, Status = st, Note = string.IsNullOrEmpty(r.Note) ? prev?.Note : r.Note };
            it.Started = prev?.Started;
            it.Ended = prev?.Ended;
            if (prev is not null) it.Steps.AddRange(prev.Steps);
            next.Add(it);
        }
        // 兜底：新的一步开始了，之前做过（开始过）却还没标完的，算做完
        var nowIdx = next.FindIndex(i => i.Status == "now");
        if (nowIdx > 0)
            for (var k = 0; k < nowIdx; k++)
                if (next[k].Status is "todo" or "now" && next[k].Started is not null) next[k].Status = "done";
        foreach (var it in next)
        {
            if (it.Status == "now") { it.Started ??= now; it.Ended = null; }
            if (it.Status is "done" or "skip") { it.Started ??= now; it.Ended ??= now; }
            if (it.Status == "todo") it.Ended = null;
        }
        if (changed)
        {
            Revisions++;
            if (!string.IsNullOrWhiteSpace(reason)) Reasons.Add(reason.Trim().Length > 120 ? reason.Trim()[..120] : reason.Trim());
        }
        Items = next;
        Started ??= now;
        return null;
    }

    /// <summary>一个工具调用开始了：挂到正在做的那一步下面（列计划这个工具本身不挂）。</summary>
    public void OnStep(string stepId, string tool)
    {
        if (tool == "plan" || Finished) return;
        Current?.Steps.Add(stepId);
    }

    /// <summary>还没做完的步骤（给模型的提醒用）。</summary>
    public List<string> Pending() => Items.Where(i => i.Status is "todo" or "now").Select(i => i.Title).ToList();

    /// <summary>回答结束：还在做的算完成；没开始的标出来——模型没报告，不替它说做了没做。
    /// cutOff=能用工具的轮数用完了（正在做的那步也算没做完）。</summary>
    public void Finish(DateTimeOffset now, bool cutOff = false)
    {
        if (Finished) return;
        foreach (var it in Items)
        {
            if (it.Status == "now" && !cutOff) { it.Status = "done"; it.Ended ??= now; }
            else if (it.Status is "todo" or "now") { it.Status = "skip"; it.Note ??= cutOff ? "没来得及做" : "没标完成"; it.Ended ??= now; }
        }
        Finished = true;
    }

    /// <summary>还没做完的步骤（收尾前看）：没开始的，加上轮数用完时正在做的。</summary>
    public List<string> Left(bool cutOff) => Items.Where(i => i.Status == "todo" || cutOff && i.Status == "now").Select(i => i.Title).ToList();

    public object State() => new
    {
        items = Items.Select(i => new { title = i.Title, status = i.Status, note = i.Note, started = i.Started, ended = i.Ended, steps = i.Steps }).ToList(),
        revisions = Revisions, reasons = Reasons, started = Started, finished = Finished, assumptions = Assumptions,
    };
}
