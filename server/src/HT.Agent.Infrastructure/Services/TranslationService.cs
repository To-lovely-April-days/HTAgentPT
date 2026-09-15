using System.Text;
using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using HT.Agent.Infrastructure.Translation;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

public class TranslationService(
    AppDbContext db,
    IChatModelClient chat,
    IRuntimeConfig config,
    IFileStorage storage,
    IAuditWriter audit,
    ICurrentUser me) : ITranslationService
{
    private static readonly string[] Directions = ["zh2en", "en2zh"];
    private static readonly string[] ContractMarkers = ["甲方", "乙方", "违约责任", "本合同", "争议解决", "合同价款"];

    public async Task<TextTranslationResult> TranslateTextAsync(TextTranslationRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            throw new DomainRuleException("TRANSLATE_EMPTY", "没有可翻译的内容");
        var direction = ValidateDirection(req.Direction);

        var paragraphs = TextSegmenter.SplitParagraphs(req.Text);
        var hits = await MatchTermsAsync(req.Text, direction, req.TermDomain, ct);
        var batchChars = await config.GetIntAsync(ConfigKeys.TranslateBatchChars, 3000, ct);

        var sources = new List<string>();
        var targets = new List<string>();
        foreach (var batch in TextSegmenter.Batch(paragraphs, batchChars))
        {
            var outputs = await TranslateBatchAsync(batch.ToList(), direction, hits, ct);
            sources.AddRange(batch);
            targets.AddRange(outputs);
        }

        var translation = string.Join("\n", targets);
        var pairs = req.Bilingual
            ? sources.Zip(targets, (s, t) => new BilingualPair(s, t)).ToList()
            : [];
        var notice = DetectContract(req.Text);

        await audit.WriteAsync(new AuditEntry("translate.text", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new { direction, chars = req.Text.Length, terms = hits.Count, contract = notice != null }), ct);
        return new TextTranslationResult(translation, pairs, hits, notice);
    }

    public async Task<FileTranslationResult> TranslateDocxAsync(
        Stream file, string fileName, string direction, string? termDomain, CancellationToken ct = default)
    {
        direction = ValidateDirection(direction);
        if (!fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            throw new DomainRuleException("TRANSLATE_FORMAT", "格式回填目前只支持 .docx（FR-6.3）；其他格式请粘贴文本翻译");

        var batchChars = await config.GetIntAsync(ConfigKeys.TranslateBatchChars, 3000, ct);
        // 术语命中在全文层面做一次，整篇共用同一份对照——同一设备全文译名一致（FR-6.1）
        var termsHit = new List<TermHit>();
        var sampled = new StringBuilder(); // 采样原文供合同识别（FR-6.6），不必全量留存
        DocxTranslator.Result result;
        try
        {
            result = await DocxTranslator.TranslateAsync(file, async (batch, token) =>
            {
                if (termsHit.Count == 0)
                    termsHit.AddRange(await MatchTermsAsync(string.Join("\n", batch), direction, termDomain, token));
                if (sampled.Length < 8000)
                    foreach (var b in batch) sampled.AppendLine(b);
                return await TranslateBatchAsync(batch, direction, termsHit, token);
            }, batchChars, ct);
        }
        catch (InvalidDataException ex)
        {
            throw new DomainRuleException("TRANSLATE_BAD_FILE", $"文件无法解析：{ex.Message}");
        }

        var outputName = Path.GetFileNameWithoutExtension(fileName) +
                         (direction == "zh2en" ? ".en" : ".zh") + ".docx";
        string outputKey;
        using (var outStream = new MemoryStream(result.Output))
            outputKey = await storage.SaveAsync(outStream, outputName, ct);

        var report = new DocxReport(result.Paragraphs, result.Translated, result.Unfillable);
        var task = new TranslationTask
        {
            Id = Guid.NewGuid(),
            UserId = me.UserId,
            CompanyId = me.CompanyId,
            SourceFileName = fileName,
            Direction = direction,
            TermDomain = termDomain,
            OutputFileKey = outputKey,
            Report = JsonSerializer.Serialize(report),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.TranslationTasks.Add(task);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("translate.file", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "translation_task", TargetId: task.Id.ToString(),
            Detail: new { fileName, direction, report.Paragraphs, report.Translated, unfillable = report.Unfillable.Count }), ct);

        return new FileTranslationResult(task.Id, outputName, report, termsHit, DetectContract(sampled.ToString()));
    }

    public async Task<(Stream Content, string FileName)> OpenOutputAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await db.TranslationTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new DomainRuleException("TASK_NOT_FOUND", "翻译任务不存在");
        // 产物归属人可下；同公司持翻译权限者也可下（校对协作），跨公司不可
        if (task.UserId != me.UserId &&
            (task.CompanyId != me.CompanyId || !me.Permissions.Contains(PermissionKeys.Translate)))
        {
            await audit.WriteAsync(new AuditEntry("translate.download", AuditResult.Denied,
                UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                TargetType: "translation_task", TargetId: taskId.ToString()), ct);
            throw new ForbiddenException("FORBIDDEN", "你没有该翻译产物的下载权限。本次请求已被记录。");
        }
        var stream = await storage.OpenAsync(task.OutputFileKey, ct);
        var name = Path.GetFileNameWithoutExtension(task.SourceFileName) +
                   (task.Direction == "zh2en" ? ".en" : ".zh") + ".docx";
        return (stream, name);
    }

    /// <summary>命中术语（FR-6.1）：源语言字段出现在原文中的已审定术语，按领域过滤。</summary>
    private async Task<List<TermHit>> MatchTermsAsync(string text, string direction, string? domain, CancellationToken ct)
    {
        var q = db.Terms.AsNoTracking().Where(t => t.Status == TermStatus.Approved);
        if (!string.IsNullOrWhiteSpace(domain)) q = q.Where(t => t.Domain == domain);
        var all = await q.Select(t => new { t.Zh, t.En, t.Domain }).Take(3000).ToListAsync(ct);
        return all
            .Where(t => direction == "zh2en"
                ? t.Zh.Length >= 2 && text.Contains(t.Zh)
                : t.En.Length >= 2 && text.Contains(t.En, StringComparison.OrdinalIgnoreCase))
            .Select(t => new TermHit(t.Zh, t.En, t.Domain))
            .Take(100)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> batch, string direction, IReadOnlyList<TermHit> terms, CancellationToken ct)
    {
        var targetLang = direction == "zh2en" ? "英文" : "中文";
        var sb = new StringBuilder();
        sb.AppendLine($"你是专业的实验仪器工程翻译。把用户消息中的段落逐段翻译为{targetLang}。规则：");
        sb.AppendLine($"1. 只输出译文。段落之间用单独一行 {TextSegmenter.Sentinel} 分隔，输出段数必须与输入段数完全一致。");
        sb.AppendLine("2. 不解释、不添加内容、不合并段落。");
        if (terms.Count > 0)
        {
            // 命中术语以强制对照写入提示词（FR-6.1）：同一设备或工艺全文译名一致
            sb.AppendLine("3. [术语对照] 中的术语必须严格按给定译法翻译，全文一致：");
            sb.AppendLine("[术语对照]");
            foreach (var t in terms)
                sb.AppendLine(direction == "zh2en" ? $"{t.Zh} => {t.En}" : $"{t.En} => {t.Zh}");
        }
        var user = string.Join($"\n{TextSegmenter.Sentinel}\n", batch);
        string output;
        try
        {
            output = await chat.CompleteAsync([new ChatTurn("system", sb.ToString()), new ChatTurn("user", user)], ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 模型服务不可用：明确提示，请求不丢弃（10.3）
            throw new DomainRuleException("TRANSLATE_UNAVAILABLE",
                "翻译模型服务暂时不可用，请稍后重发——你的内容没有被丢弃，重发即可。");
        }
        var parts = output.Split(TextSegmenter.Sentinel, StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0).ToList();
        // 段数不一致时不硬对齐：整批作为一段返回，调用方（docx 回填）会把该批标为未回填
        return parts.Count == batch.Count ? parts : [output.Trim()];
    }

    private static string? DetectContract(string text)
        => ContractMarkers.Count(m => text.Contains(m)) >= 2
            ? "该文本疑似合同类内容：机器译文仅供参考，法律文本须经人工复核后方可对外使用（FR-6.6）。"
            : null;

    private static string ValidateDirection(string direction)
        => Directions.Contains(direction)
            ? direction
            : throw new DomainRuleException("TRANSLATE_DIRECTION", "direction 须为 zh2en 或 en2zh");
}
