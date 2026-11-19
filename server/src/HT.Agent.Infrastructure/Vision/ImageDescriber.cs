using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HT.Agent.Infrastructure.Vision;

/// <summary>解析时看图：把文档里的图写成文字分块（知识库解析与对话附件解析共用）。
/// 开关 vision.enabled 关着、或对话模型是演示应答时什么都不做——行为和没有这个功能时一样。
/// 模型不收图：记一条警告，停掉这份文档剩下的图，解析照常完成（宁可少几段图片描述，也不让整篇解析失败）。</summary>
public class ImageDescriber(IVisionClient vision, IRuntimeConfig config, IConfiguration appConfig, ILogger<ImageDescriber> logger)
{
    public async Task<bool> EnabledAsync(CancellationToken ct)
    {
        if (!ModelSlotDefaults.Flag(await config.GetStringAsync(ConfigKeys.VisionEnabled, "false", ct), false)) return false;
        var fallback = ModelSlotDefaults.UseStubs(appConfig) ? "stub" : "openai";
        var provider = await config.GetStringAsync(ConfigKeys.ChatProvider, fallback, ct);
        return !provider.Trim().Equals("stub", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>看一批图，返回入库用的说明（判成「无关」的不在里面）。</summary>
    public async Task<List<ImageNotes.Note>> DescribeAsync(IReadOnlyList<ParsedImage>? images, string docName, CancellationToken ct)
    {
        if (images is null || images.Count == 0 || !await EnabledAsync(ct)) return [];
        var max = await config.GetIntAsync(ConfigKeys.VisionMaxImages, 30, ct);
        var picked = ImageNotes.Select(
            images.Select((i, k) => (i, k)).Where(x => IsSupported(x.i.ContentType))
                .Select(x => new ImageNotes.Candidate(x.i.Bytes, x.i.ContentType, x.i.Caption, x.i.PageNo, x.k + 1)), max);
        var notes = new List<ImageNotes.Note>();
        foreach (var img in picked)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await vision.DescribeAsync(img.Bytes, img.ContentType, ImageNotes.Prompt, ct);
                if (ImageNotes.Parse(reply, img.Page, img.Caption, img.Index) is { } note) notes.Add(note);
            }
            catch (VisionUnavailableException ex)
            {
                logger.LogWarning("看图不可用，《{Doc}》剩余图片跳过：{Reason}", docName, ex.Message);
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // 单张超时或接口抖一下：跳过这张，接着看下一张
                logger.LogWarning("《{Doc}》一张图片没看成：{Reason}", docName, ex.Message);
            }
        }
        return notes;
    }

    /// <summary>看图模型收的格式（SVG 是矢量文本，模型不认，跳过）。</summary>
    private static bool IsSupported(string contentType)
        => contentType.ToLowerInvariant() is "image/png" or "image/jpeg" or "image/jpg" or "image/webp" or "image/gif" or "image/bmp";
}
