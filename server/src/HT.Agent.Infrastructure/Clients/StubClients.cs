using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Clients;

// 开发与测试用桩实现（appsettings: Models:UseStubs=true）。
// 目的：没有模型服务的环境里整条链路仍可端到端跑通与验证；
// 生产部署必然配置真实地址——本文件不含任何业务规则，规则全部在服务层。

/// <summary>确定性向量：文本分词后按 token 哈希落桶并归一化。相同文本同向量，含相同型号词的文本相近。</summary>
public class StubEmbeddingClient(IRuntimeConfig config) : IEmbeddingClient
{
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var dim = await config.GetIntAsync(ConfigKeys.EmbeddingDimension, 1024, ct);
        return texts.Select(t => Embed(t, dim)).ToList();
    }

    public async Task<string> CurrentModelTagAsync(CancellationToken ct = default)
    {
        var dim = await config.GetIntAsync(ConfigKeys.EmbeddingDimension, 1024, ct);
        return $"stub@{dim}";
    }

    internal static float[] Embed(string text, int dim)
    {
        var v = new float[dim];
        var tokens = ChineseTokenizer.Tokenize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var h = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var bucket = BitConverter.ToUInt32(h, 0) % (uint)dim;
            var sign = (h[4] & 1) == 0 ? 1f : -1f;
            v[bucket] += sign;
        }
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < dim; i++) v[i] /= norm;
        return v;
    }
}

/// <summary>词元重合度打分：查询与段落的分词交集比例。</summary>
public class StubRerankClient : IRerankClient
{
    public Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken ct = default)
    {
        var q = ChineseTokenizer.Tokenize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        IReadOnlyList<double> scores = passages.Select(p =>
        {
            if (q.Count == 0) return 0d;
            var pt = ChineseTokenizer.Tokenize(p).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            return (double)q.Intersect(pt).Count() / q.Count;
        }).ToList();
        return Task.FromResult(scores);
    }
}

/// <summary>把检索到的内容原样组织成回答。不编造——桩的行为与「仅依据给定内容作答」一致。</summary>
public class StubChatClient : IChatModelClient
{
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var answer = await CompleteAsync(messages, ct);
        // 模拟流式：按标点切片输出
        foreach (var piece in answer.Split('\n'))
        {
            ct.ThrowIfCancellationRequested();
            yield return piece + "\n";
        }
    }

    public Task<string> CompleteAsync(IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
    {
        var user = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
        var refStart = sys.IndexOf("[参考内容]", StringComparison.Ordinal);
        var refs = refStart >= 0 ? sys[refStart..] : "（无参考内容）";
        return Task.FromResult($"[桩模型回答] 依据给定参考内容作答。\n{Truncate(refs, 600)}");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>纯文本/Markdown 桩解析：# 级标题、| 表格行、空行分段。二进制文件按内容性失败处理。</summary>
public class StubParserClient : IDocumentParserClient
{
    public async Task<ParsedDocument> ParseAsync(Stream file, string fileName, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (bytes.Length == 0) throw new ParseContentException("文件为空");
        if (Array.IndexOf(bytes, (byte)0) >= 0)
            throw new ParseContentException($"格式不支持：桩解析器只处理文本类文件（{fileName}）");
        var text = Encoding.UTF8.GetString(bytes);
        var blocks = new List<ParsedBlock>();
        string? tableHeader = null;
        var page = 1; var charsOnPage = 0;
        foreach (var raw in text.Split('\n'))
        {
            ct.ThrowIfCancellationRequested();
            var line = raw.TrimEnd();
            charsOnPage += line.Length;
            if (charsOnPage > 2400) { page++; charsOnPage = line.Length; } // 近似页码
            if (line.Length == 0) { tableHeader = null; continue; }
            if (line.StartsWith('#'))
            {
                var level = line.TakeWhile(c => c == '#').Count();
                blocks.Add(new ParsedBlock("heading", line.TrimStart('#').Trim(), level, page, null, null));
            }
            else if (line.StartsWith('|'))
            {
                if (tableHeader is null) tableHeader = line;
                else if (!line.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Equals(string.Empty))
                    blocks.Add(new ParsedBlock("table_row", line, null, page, null, tableHeader));
            }
            else
            {
                blocks.Add(new ParsedBlock("paragraph", line, null, page, null, null));
            }
        }
        if (blocks.Count == 0) throw new ParseContentException("未解析出任何内容块");
        return new ParsedDocument(blocks);
    }
}
