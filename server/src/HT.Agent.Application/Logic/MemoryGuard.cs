using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>记忆写入前的检查：不存联系方式、证件号、银行卡、口令这类敏感内容；长度有上限。
/// 只看文字形态（通用规则），不针对任何业务内容。</summary>
public static class MemoryGuard
{
    public static readonly string[] Kinds = ["偏好", "工作", "客户"];

    private static readonly (Regex Re, string Why)[] Sensitive =
    [
        (new(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.Compiled), "手机号"),
        (new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled), "邮箱"),
        (new(@"(?<!\d)\d{17}[\dXx](?!\d)", RegexOptions.Compiled), "身份证号"),
        (new(@"(?<!\d)\d{16,19}(?!\d)", RegexOptions.Compiled), "银行卡号"),
        (new(@"(密码|口令|password|passwd|pwd)\s*[:：是为=]", RegexOptions.Compiled | RegexOptions.IgnoreCase), "口令"),
        (new(@"(?<!\d)0\d{2,3}-?\d{7,8}(?!\d)", RegexOptions.Compiled), "电话号码"),
    ];

    /// <summary>不能存就返回原因，能存返回 null。</summary>
    public static string? Reject(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length < 2) return "内容太短";
        if (t.Length > 200) return "一条记忆不超过 200 字，请拆成几条或精简";
        foreach (var (re, why) in Sensitive)
            if (re.IsMatch(t)) return $"含有{why}，这类敏感信息不记";
        return null;
    }

    public static string NormalizeKind(string? kind) => Kinds.Contains(kind?.Trim()) ? kind!.Trim() : "偏好";
}
