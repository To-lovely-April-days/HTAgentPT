using System.Text;

namespace HT.Agent.Application.Logic;

/// <summary>中文检索分词（7.2）：供 PostgreSQL simple 解析器建全文索引。
/// 中文按二元切分；设备型号、报警代码一类含数字与连字符的串整串保留，不被错误切分。
/// 索引侧与查询侧必须使用同一实现。</summary>
public static class ChineseTokenizer
{
    /// <summary>型号/代码类字符：字母数字加连字符、点、斜杠、下划线（CJF-5L、E-17、GB/T 35510 的 GB/T）。</summary>
    private static bool IsCodeChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '/' or '_';

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF);

    /// <summary>切分为空格分隔的词串。中文出二元组（单字段落补单字），代码串整串小写保留。</summary>
    public static string Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        // NFKC 归一化：全角字母数字（ＣＪＦ－５Ｌ）折到半角，索引侧与查询侧同一入口，全角型号串不再不可检索
        text = text.Normalize(System.Text.NormalizationForm.FormKC);
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (IsCodeChar(c))
            {
                var start = i;
                while (i < text.Length && IsCodeChar(text[i])) i++;
                // 去掉纯标点边界（如句尾的 "." 粘连）
                var token = text[start..i].Trim('-', '.', '/', '_');
                if (token.Length > 0) tokens.Add(token.ToLowerInvariant());
                continue;
            }
            if (IsCjk(c))
            {
                var start = i;
                while (i < text.Length && IsCjk(text[i])) i++;
                var run = text.AsSpan(start, i - start);
                if (run.Length == 1) tokens.Add(run.ToString());
                else
                    for (var j = 0; j + 1 < run.Length; j++)
                        tokens.Add(new string([run[j], run[j + 1]]));
                continue;
            }
            i++;
        }
        return string.Join(' ', tokens);
    }

    /// <summary>查询侧：同一分词，产出 to_tsquery 的 OR 表达式；空查询返回空串。</summary>
    public static string ToTsQuery(string query)
    {
        var tokens = Tokenize(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct()
            .Take(64)
            .Select(EscapeTsLexeme)
            .Where(t => t.Length > 0)
            .ToList();
        return tokens.Count == 0 ? string.Empty : string.Join(" | ", tokens);
    }

    private static string EscapeTsLexeme(string token)
    {
        // to_tsquery 语法字符一律剔除，词素本身用引号包裹以保留连字符等
        var sb = new StringBuilder(token.Length + 2);
        foreach (var c in token)
            if (c is not ('&' or '|' or '!' or '(' or ')' or ':' or '*' or '\'' or '<' or '>'))
                sb.Append(c);
        var clean = sb.ToString();
        return clean.Length == 0 ? string.Empty : $"'{clean}'";
    }
}
