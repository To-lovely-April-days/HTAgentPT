using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>提交总部前的敏感信息检测（FR-8.3）：客户名称词表、项目编号格式、电话与邮箱格式
/// 对全部文本字段扫描，命中处提示、用户确认后方可提交。
/// 检测只做词表与格式匹配、不理解语义（H2 审核端仍须人工复核，设计批注同源）。</summary>
public static class SensitiveScanner
{
    public record Hit(string Field, string Kind, string Match);

    private static readonly Regex Mobile = new(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Landline = new(@"(?<!\d)0\d{2,3}-\d{7,8}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    public static IReadOnlyList<Hit> Scan(
        IReadOnlyDictionary<string, string?> fields,
        IReadOnlyCollection<string> customerNames,
        string? projectNoPattern)
    {
        var hits = new List<Hit>();
        Regex? projectNo = null;
        if (!string.IsNullOrWhiteSpace(projectNoPattern))
        {
            try { projectNo = new Regex(projectNoPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
            catch (ArgumentException) { /* 非法配置不阻断检测，其余三类照扫 */ }
        }

        foreach (var (field, raw) in fields)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var text = raw;
            foreach (var name in customerNames.Where(n => n.Length >= 2))
                if (text.Contains(name))
                    hits.Add(new Hit(field, "客户名称", name));
            if (projectNo is not null)
                try
                {
                    foreach (Match m in projectNo.Matches(text))
                        hits.Add(new Hit(field, "项目编号", m.Value));
                }
                catch (RegexMatchTimeoutException) { }
            foreach (Match m in Mobile.Matches(text)) hits.Add(new Hit(field, "电话", m.Value));
            foreach (Match m in Landline.Matches(text)) hits.Add(new Hit(field, "电话", m.Value));
            foreach (Match m in Email.Matches(text)) hits.Add(new Hit(field, "邮箱", m.Value));
        }
        return hits.DistinctBy(h => (h.Field, h.Kind, h.Match)).ToList();
    }
}
