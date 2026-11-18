using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Dtos;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HT.Agent.Infrastructure.Workers;

/// <summary>对话附件的解析：一个一个来（不和知识库解析、在线问答抢算力）。
/// 解析出的分块只存在附件自己身上（不进知识库、不做向量）——问答时按和问题的相关度挑。
/// 引擎不可用 → 等一会儿再试（10.3）；内容性失败 → 写清原因（FR-1.7）；
/// 解析中停太久（进程崩了）→ 重新排队。</summary>
public class AttachmentParseWorker(IServiceScopeFactory scopeFactory, ILogger<AttachmentParseWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public record StoredChunk(int Seq, string? Section, int? Page, string Text);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOnceAsync(stoppingToken)) await Task.Delay(Poll, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "附件解析循环异常");
                await Task.Delay(Poll, stoppingToken);
            }
        }
    }

    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        await db.ChatAttachments
            .Where(a => a.Status == ParseStatus.Parsing && a.StartedAt != null && a.StartedAt < now - Stale)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, ParseStatus.Queued), ct);

        var a = await db.ChatAttachments
            .Where(x => x.Status == ParseStatus.Queued ||
                        (x.Status == ParseStatus.Waiting && (x.NextAttemptAt == null || x.NextAttemptAt <= now)))
            .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (a is null) return false;

        a.Status = ParseStatus.Parsing;
        a.StartedAt = now;
        await db.SaveChangesAsync(ct);

        var parser = scope.ServiceProvider.GetRequiredService<IDocumentParserClient>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        try
        {
            ParsedDocument parsed;
            await using (var file = await storage.OpenAsync(a.FileKey, ct))
                parsed = await parser.ParseAsync(file, a.FileName, a.ContentType, ct);
            var drafts = Chunker.Chunk(Chunker.Pick(parsed.Blocks), parsed.Blocks, new ChunkingOptions());
            var stored = drafts.Select(d => new StoredChunk(d.Seq, d.SectionPath, d.PageNo, d.Text)).ToList();
            // 图片理解（开关打开时）：客户发来的 PID、图纸、照片写成文字，和正文一起供问答挑选
            var describer = scope.ServiceProvider.GetRequiredService<Vision.ImageDescriber>();
            var notes = await describer.DescribeAsync(parsed.Images, a.FileName, ct);
            var next = stored.Count == 0 ? 0 : stored.Max(c => c.Seq) + 1;
            stored.AddRange(notes.Select((n, i) => new StoredChunk(next + i, ImageNotes.SectionName, n.Page, ImageNotes.ChunkText(n))));
            if (stored.Count == 0) throw new ParseContentException("没有读出文字（可能是空白或纯图片的文件；打开图片理解后可以识别图片内容）");
            a.Chunks = JsonSerializer.Serialize(stored, Json);
            a.CharCount = stored.Sum(c => c.Text.Length);
            a.Status = ParseStatus.Parsed;
            a.Error = null;
            a.ParsedAt = DateTimeOffset.UtcNow;
        }
        catch (ParseContentException ex)
        {
            a.Status = ParseStatus.Failed;
            a.Error = ex.Message;
        }
        catch (ParserUnavailableException ex)
        {
            a.Status = ParseStatus.Waiting;
            a.Error = $"解析引擎暂时不可用，稍后自动重试（{ex.Message}）";
            a.NextAttemptAt = DateTimeOffset.UtcNow + Retry;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static List<AttachmentRanker.Piece> Pieces(Guid id, string fileName, string? chunksJson)
    {
        if (string.IsNullOrWhiteSpace(chunksJson)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<StoredChunk>>(chunksJson, Json) ?? [])
                .Select(c => new AttachmentRanker.Piece(id, fileName, c.Seq, c.Section, c.Page, c.Text)).ToList();
        }
        catch (JsonException) { return []; }
    }
}
