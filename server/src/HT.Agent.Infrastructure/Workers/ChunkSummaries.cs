using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Dtos;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HT.Agent.Infrastructure.Workers;

/// <summary>入库时让本地模型给每个分块写一句「这段讲的是什么」（上下文增强检索的做法）。
/// 只在解析时跑一次；开关关着、模型连不上、某一块写失败，都只是少这一句，不影响入库。</summary>
public static class ChunkSummaries
{
    /// <summary>超过这么多块的大文档不写（太慢），只用文件、章节这些身份信息。</summary>
    private const int MaxChunks = 400;

    public static async Task<string?[]> WriteAsync(IServiceProvider sp, IRuntimeConfig config, string title, IReadOnlyList<ChunkDraft> drafts,
        ILogger logger, CancellationToken ct)
    {
        var result = new string?[drafts.Count];
        var on = (await config.GetStringAsync(ConfigKeys.ChunkContextSummary, "on", ct)).Trim().ToLowerInvariant() is "on" or "true";
        if (!on || drafts.Count > MaxChunks) return result;
        var chat = sp.GetService<IChatModelClient>();
        if (chat is null) return result;

        using var gate = new SemaphoreSlim(3);
        var failures = 0;
        var tasks = drafts.Select(async (d, i) =>
        {
            if (d.Text.Length < 40 || Volatile.Read(ref failures) >= 5) return;
            await gate.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(40));
                var prompt = $"""
                    下面是《{title}》里的一段{(string.IsNullOrEmpty(d.SectionPath) ? "" : $"（章节：{d.SectionPath}）")}。
                    用一句中文（不超过 40 个字）说明这段讲的是什么、涉及哪个设备或事项，方便以后检索。只输出这一句，不要解释。
                    ---
                    {(d.Text.Length > 1500 ? d.Text[..1500] : d.Text)}
                    """;
                var raw = await chat.CompleteAsync([new ChatTurn("user", prompt)], cts.Token);
                result[i] = ChunkContext.CleanSummary(StripThinking(raw));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                if (Interlocked.Increment(ref failures) == 5)
                    logger.LogWarning("分块说明连续失败，本文档后面的块不再写：{Error}", ex.Message);
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return result;
    }

    /// <summary>带思考过程的模型会先输出 &lt;think&gt;…&lt;/think&gt;，只要后面的结论。</summary>
    private static string StripThinking(string raw)
    {
        var i = raw.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? raw[(i + 8)..] : raw;
    }
}
