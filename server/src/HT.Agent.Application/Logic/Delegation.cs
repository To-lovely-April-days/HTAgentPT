using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>主助手派的一个子任务：做什么、交回什么、要用哪些附件。</summary>
public record SubTask(int N, string Title, string Task, string Deliver, IReadOnlyList<string> Files);

/// <summary>一个子代理做完的样子。Status：done / partial（轮数用完，交回了一部分）/ error。</summary>
public record SubResult(SubTask Task, string Status, string Answer, string? Note);

/// <summary>「分头查」：主助手按任务复杂度自己决定拆不拆。这里放不依赖模型和数据库的部分：
/// 解析子任务、子代理看到的说明、交回给主助手的汇总、卡片上的一句话结论。</summary>
public static class Delegation
{
    /// <summary>子代理用不了的工具：不能再拆、不能停下来问人、不能做文件、不往界面上直接放组件、不改记忆。</summary>
    public static readonly string[] SubAgentDrops =
        ["delegate", "ask_user", "plan", "inquiry_form", "show_table", "show_block", "remember", "forget", "translate_file", "note_correction"];

    /// <summary>子代理交回的结论最长多少字（再长主助手的上下文装不下）。</summary>
    public const int MaxAnswer = 1500;

    public static List<SubTask> Parse(JsonObject args, int max)
    {
        var list = new List<SubTask>();
        if (args["tasks"] is not JsonArray arr) return list;
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            string S(string k) => o[k]?.ToString()?.Trim() ?? "";
            var task = S("task");
            if (task.Length == 0) continue;
            var title = S("title");
            if (title.Length == 0) title = task.Length > 20 ? task[..20] : task;
            var deliver = S("deliver");
            var files = o["files"] is JsonArray fa ? fa.Select(x => x?.ToString()?.Trim() ?? "").Where(x => x.Length > 0).Distinct().Take(3).ToList() : [];
            list.Add(new SubTask(list.Count + 1, Clip(title, 40), Clip(task, 1500), deliver.Length > 0 ? Clip(deliver, 300) : "结论、依据，以及没查到的", files));
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>子代理的系统提示：它是谁、只做这一件事、交回的格式。</summary>
    public static string SubPrompt(string company, string profile, SubTask t, IReadOnlyList<string> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"你是{company}智能助手派出的子代理，只负责主助手交给你的这一件事，做完把结论交回给主助手。你不和用户对话，不要寒暄，不要问问题。");
        if (profile.Length > 0) sb.AppendLine($"关于公司：{profile}");
        sb.AppendLine($"今天是 {DateTime.Now:yyyy年M月d日}。");
        if (files.Count > 0) sb.AppendLine($"这件事要用的附件：{string.Join("、", files.Select(f => $"《{f}》"))}。");
        sb.AppendLine("要求：");
        sb.AppendLine("- 只做这一件事；资料不够就写进「没查到」，不要编。");
        sb.AppendLine("- 引用资料写工具返回的编号，如 [3]；数字写单位和出处。");
        sb.AppendLine($"- 交回不超过 {MaxAnswer} 字，按这个格式：");
        sb.AppendLine("结论：一两句话");
        sb.AppendLine("明细：逐条列出");
        sb.AppendLine("依据：用到的资料编号、项目、技能");
        sb.Append("没查到：还缺什么（没有就写「无」）");
        return sb.ToString();
    }

    public static string SubQuestion(SubTask t) => $"{t.Task}\n要交回：{t.Deliver}";

    /// <summary>卡片上的一句话：取「结论」那一行，截到 40 字。</summary>
    public static string Short(string answer)
    {
        var m = Regex.Match(answer ?? "", @"结论[:：]\s*(.+)");
        var s = (m.Success ? m.Groups[1].Value : (answer ?? "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "").Trim();
        return s.Length <= 40 ? s : s[..40] + "…";
    }

    /// <summary>交回给主助手的：几份结论，没查完的写明，最后告诉它怎么汇总。</summary>
    public static string ForMain(IReadOnlyList<SubResult> results)
    {
        var sb = new StringBuilder($"分头查完了（{results.Count} 个子任务）：\n");
        foreach (var r in results)
        {
            var head = r.Status switch
            {
                "done" => $"【子任务 {r.Task.N}：{r.Task.Title}】",
                "partial" => $"【子任务 {r.Task.N}：{r.Task.Title}】（没查完：{r.Note}，下面是查到的部分）",
                _ => $"【子任务 {r.Task.N}：{r.Task.Title}】（没做成：{r.Note}）"
            };
            sb.AppendLine(head);
            if (r.Answer.Length > 0) sb.AppendLine(Clip(r.Answer, MaxAnswer + 300));
            sb.AppendLine();
        }
        sb.Append("汇总时：合并、去重；子任务之间说法矛盾的地方再核一遍原文；「没查到」和没查完、没做成的部分自己补查一次，补不上就在回答里说清楚哪一部分没覆盖。引用编号照原样用。");
        return sb.ToString();
    }

    public static string Compact(IReadOnlyList<SubResult> results)
        => "（分头查的结论）\n" + string.Join("\n", results.Select(r => $"{r.Task.N}. {r.Task.Title}：{Short(r.Answer)}"));

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
