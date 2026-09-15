using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Dtos;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace HT.Agent.Infrastructure.Workers;

/// <summary>解析队列工作器（FR-1.2：串行或有限并行，不与在线问答争抢算力）。
/// 引擎或模型不可用 → 任务置等待并按退避重试，不丢请求、不报失败（10.3）；
/// 内容性失败 → 记录具体原因（FR-1.7）。</summary>
public class ParseWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ParseWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WaitingRetryBase = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var worked = await RunOnceAsync(stoppingToken);
                if (!worked) await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "解析工作器循环异常");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    /// <summary>取一批任务并处理。返回是否处理了任务。</summary>
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IRuntimeConfig>();
        var concurrency = Math.Max(1, await config.GetIntAsync(ConfigKeys.ParserConcurrency, 1, ct));

        var now = DateTimeOffset.UtcNow;

        // 崩溃回收：Running 超过 30 分钟视为进程死亡遗留（解析超时上限之外），重新入队。
        // 没有这条，崩溃时正在处理的文档会永远停在「解析中」并退出检索。
        var stale = now - TimeSpan.FromMinutes(30);
        await db.ParseJobs
            .Where(j => j.Status == JobStatus.Running && j.StartedAt != null && j.StartedAt < stale)
            .ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, JobStatus.Queued)
                .SetProperty(j => j.LastError, "进程中断，任务已自动重新入队"), ct);

        // Queued 优先；Waiting 按尝试次数退避后重试（30s、60s、120s…封顶 10 分钟）
        var jobs = await db.ParseJobs
            .Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Waiting)
            .OrderBy(j => j.Status == JobStatus.Queued ? 0 : 1).ThenBy(j => j.QueuedAt)
            .Take(concurrency * 4)
            .ToListAsync(ct);
        var runnable = jobs.Where(j =>
                j.Status == JobStatus.Queued ||
                (j.StartedAt ?? j.QueuedAt) + Backoff(j.Attempts) <= now)
            .Take(concurrency)
            .ToList();
        if (runnable.Count == 0) return false;

        foreach (var job in runnable)
        {
            job.Status = JobStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            job.Attempts++;
        }
        await db.SaveChangesAsync(ct);

        foreach (var job in runnable)
            await ProcessAsync(job.Id, ct);
        return true;
    }

    private static TimeSpan Backoff(int attempts)
        => TimeSpan.FromTicks(Math.Min(WaitingRetryBase.Ticks * (1L << Math.Min(attempts, 5)), TimeSpan.FromMinutes(10).Ticks));

    internal async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var parser = scope.ServiceProvider.GetRequiredService<IDocumentParserClient>();
        var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
        var config = scope.ServiceProvider.GetRequiredService<IRuntimeConfig>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();

        var job = await db.ParseJobs.Include(j => j.Doc).ThenInclude(d => d!.Kb)
            .FirstAsync(j => j.Id == jobId, ct);
        var doc = job.Doc!;
        try
        {
            if (job.Kind == ParseJobKind.EmbedOnly)
            {
                await EmbedSingleChunkAsync(db, embedder, job, ct);
                return;
            }
            if (job.Kind == ParseJobKind.EmbedDoc)
            {
                await EmbedDocChunksAsync(db, embedder, job, ct);
                return;
            }

            doc.ParseStatus = job.Kind == ParseJobKind.Reparse ? ParseStatus.Reparsing : ParseStatus.Parsing;
            await db.SaveChangesAsync(ct);

            // 重解析期间该文档暂不参与检索（FR-1.6/10.3）：旧分块先失效
            if (job.Kind == ParseJobKind.Reparse)
                await db.Chunks.Where(c => c.DocId == doc.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsActive, false), ct);

            ParsedDocument parsed;
            await using (var file = await storage.OpenAsync(doc.FileKey, ct))
                parsed = await parser.ParseAsync(file, doc.FileName, doc.ContentType, ct);

            var strategy = doc.ChunkStrategyOverride ?? doc.Kb!.DefaultChunkStrategy;
            var opt = new ChunkingOptions(
                await config.GetIntAsync(ConfigKeys.ChunkTargetLength, 800, ct),
                await config.GetIntAsync(ConfigKeys.ChunkOverlap, 80, ct),
                await config.GetIntAsync(ConfigKeys.ChunkMinLength, 200, ct));
            var drafts = Chunker.Chunk(strategy, parsed.Blocks, opt);
            if (drafts.Count == 0)
                throw new ParseContentException("切分后没有产生任何分块（文档内容可能为空）");

            IReadOnlyList<float[]> vectors;
            string modelTag;
            try
            {
                var batch = await embedder.EmbedAsync(drafts.Select(d => d.Text).ToList(), ct);
                vectors = batch.Vectors;
                modelTag = batch.ModelTag;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // 向量化服务不可用与解析引擎不可用同类处理（10.3）
                throw new ParserUnavailableException($"向量化服务不可用：{ex.Message}", ex);
            }

            var newVersion = doc.ParseVersion + 1;
            // 原分块作废并重建（FR-1.6）：物理删除旧版本，原文件仍在对象存储（FR-1.8）
            await db.Chunks.Where(c => c.DocId == doc.Id).ExecuteDeleteAsync(ct);
            for (var i = 0; i < drafts.Count; i++)
            {
                var d = drafts[i];
                db.Chunks.Add(new Chunk
                {
                    DocId = doc.Id,
                    KbId = doc.KbId,
                    Classification = doc.Classification, // 冗余存储供检索过滤（7.2）
                    Seq = d.Seq,
                    SectionPath = d.SectionPath,
                    PageNo = d.PageNo,
                    Bbox = d.Bbox,
                    Text = d.Text,
                    SearchText = ChineseTokenizer.Tokenize(d.Text),
                    Embedding = new Vector(vectors[i]),
                    EmbeddingModel = modelTag,
                    ParseVersion = newVersion,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            doc.ParseVersion = newVersion;
            doc.ParseStatus = ParseStatus.Parsed;
            doc.ParsedAt = DateTimeOffset.UtcNow;
            doc.ParseError = null;
            job.Status = JobStatus.Succeeded;
            job.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("doc.parsed", AuditResult.Success,
                UserId: job.QueuedBy, Username: "-",
                TargetType: "document", TargetId: doc.Id.ToString(),
                Detail: new { chunks = drafts.Count, strategy = strategy.ToString(), modelTag }), ct);
        }
        catch (ParserUnavailableException ex)
        {
            // 等待而非失败（10.3）：状态区分开，界面上「等待」不是「失败」
            job.Status = JobStatus.Waiting;
            job.LastError = ex.Message;
            doc.ParseStatus = ParseStatus.Waiting;
            doc.ParseError = ex.Message;
            await db.SaveChangesAsync(ct);
            logger.LogWarning("任务 {JobId} 置等待：{Reason}", job.Id, ex.Message);
        }
        catch (ParseContentException ex)
        {
            job.Status = JobStatus.Failed;
            job.LastError = ex.Reason;
            job.FinishedAt = DateTimeOffset.UtcNow;
            doc.ParseStatus = ParseStatus.Failed;
            doc.ParseError = ex.Reason; // 具体原因（FR-1.7）
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry("doc.parse_failed", AuditResult.Failed,
                UserId: job.QueuedBy, Username: "-",
                TargetType: "document", TargetId: doc.Id.ToString(), Detail: new { reason = ex.Reason }), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.Status = JobStatus.Failed;
            job.LastError = ex.Message;
            job.FinishedAt = DateTimeOffset.UtcNow;
            doc.ParseStatus = ParseStatus.Failed;
            doc.ParseError = $"内部错误：{ex.Message}";
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "解析任务 {JobId} 失败", jobId);
        }
    }

    /// <summary>同步合入后按本机模型重算整篇缺向量的分块（FR-2.2：向量在本地重新生成）。</summary>
    private static async Task EmbedDocChunksAsync(
        AppDbContext db, IEmbeddingClient embedder, ParseJob job, CancellationToken ct)
    {
        var chunks = await db.Chunks
            .Where(c => c.DocId == job.DocId && c.IsActive && c.Embedding == null)
            .OrderBy(c => c.Seq).ToListAsync(ct);
        if (chunks.Count == 0)
        {
            job.Status = JobStatus.Succeeded;
            job.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }
        try
        {
            var batch = await embedder.EmbedAsync(chunks.Select(c => c.Text).ToList(), ct);
            for (var i = 0; i < chunks.Count; i++)
            {
                chunks[i].Embedding = new Vector(batch.Vectors[i]);
                chunks[i].EmbeddingModel = batch.ModelTag;
            }
            job.Status = JobStatus.Succeeded;
            job.FinishedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            job.Status = JobStatus.Waiting;
            job.LastError = $"向量化服务不可用：{ex.Message}";
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task EmbedSingleChunkAsync(
        AppDbContext db, IEmbeddingClient embedder, ParseJob job, CancellationToken ct)
    {
        var chunk = await db.Chunks.FirstOrDefaultAsync(c => c.Id == job.ChunkId, ct);
        if (chunk is null)
        {
            job.Status = JobStatus.Cancelled;
            job.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }
        try
        {
            var batch = await embedder.EmbedAsync([chunk.Text], ct);
            chunk.Embedding = new Vector(batch.Vectors[0]);
            chunk.EmbeddingModel = batch.ModelTag;
            job.Status = JobStatus.Succeeded;
            job.FinishedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            job.Status = JobStatus.Waiting;
            job.LastError = $"向量化服务不可用：{ex.Message}";
        }
        await db.SaveChangesAsync(ct);
    }
}
