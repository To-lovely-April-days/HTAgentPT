using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

/// <summary>故障案例（M8）。案例既是行也是分块（FR-8.2）：结构化行支撑字段精确查询，
/// 渲染分块支撑语义检索——同一份内容两种入口，修改时两边同步。</summary>
public class FaultCaseService(
    AppDbContext db,
    IRuntimeConfig config,
    IHttpClientFactory httpFactory,
    IAuditWriter audit,
    ICurrentUser me) : IFaultCaseService
{
    /// <summary>承载案例分块的虚拟容器文档的存储键前缀。该文档不对应真实文件，
    /// 语料管理的解析/分块写操作都要挡住它（分块由本模块托管）。</summary>
    public const string VirtualFileKeyPrefix = "virtual:fault-cases:";

    public async Task<Guid> CreateAsync(CaseEdit edit, CancellationToken ct = default)
    {
        Validate(edit);
        var container = await GetOrCreateContainerAsync(ct);

        var year = DateTimeOffset.UtcNow.Year;
        var prefix = await config.GetStringAsync(ConfigKeys.CaseNoPrefix, "FC", ct);
        // 编号并发安全：唯一索引兜底，撞号重试
        for (var attempt = 0; ; attempt++)
        {
            var seq = await db.FaultCases.CountAsync(
                c => c.CompanyId == me.CompanyId && c.CaseNo.StartsWith($"{prefix}-{year}-"), ct) + 1 + attempt;
            var caseNo = $"{prefix}-{year}-{seq:D4}";
            var entity = new FaultCase
            {
                Id = Guid.NewGuid(),
                CaseNo = caseNo,
                CompanyId = me.CompanyId,
                DeviceModel = edit.DeviceModel.Trim(),
                AlarmCode = string.IsNullOrWhiteSpace(edit.AlarmCode) ? null : edit.AlarmCode.Trim(),
                Phenomenon = edit.Phenomenon.Trim(),
                CauseAnalysis = edit.CauseAnalysis.Trim(),
                Steps = edit.Steps.Trim(),
                SpareParts = edit.SpareParts,
                Result = edit.Result.Trim(),
                Extra = edit.Extra is { Count: > 0 } ? JsonSerializer.Serialize(edit.Extra) : null,
                CreatedById = me.UserId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            // 渲染入库（FR-8.2）：关键词路即刻可查（search_text 就绪），向量随队列稍后就位
            var chunk = new Chunk
            {
                DocId = container.Id,
                KbId = container.KbId,
                Classification = container.Classification,
                Seq = await db.Chunks.Where(c => c.DocId == container.Id).Select(c => (int?)c.Seq).MaxAsync(ct) + 1 ?? 0,
                SectionPath = $"故障案例 > {caseNo}",
                Text = RenderOf(entity),
                SearchText = ChineseTokenizer.Tokenize(RenderOf(entity)),
                ParseVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Chunks.Add(chunk);
            db.FaultCases.Add(entity);
            try
            {
                await db.SaveChangesAsync(ct);
                entity.ChunkId = chunk.Id;
                db.ParseJobs.Add(NewEmbedJob(container.Id, chunk.Id));
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync(new AuditEntry("case.create", AuditResult.Success,
                    UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
                    TargetType: "fault_case", TargetId: entity.Id.ToString(),
                    Detail: new { caseNo, edit.DeviceModel, edit.AlarmCode }), ct);
                return entity.Id;
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                db.ChangeTracker.Clear(); // 撞号：清跟踪重试下一个序号
            }
        }
    }

    public async Task UpdateAsync(Guid caseId, CaseEdit edit, CancellationToken ct = default)
    {
        Validate(edit);
        var entity = await db.FaultCases.FirstOrDefaultAsync(
            c => c.Id == caseId && c.CompanyId == me.CompanyId, ct)
            ?? throw new DomainRuleException("CASE_NOT_FOUND", "案例不存在");
        // 已共享的案例本地仍可改（FR-8.2 本地生效），但同步状态回退为本地版本有更新——
        // 回流批次会处理重新提交；此处只标记不擅自改共享库
        entity.DeviceModel = edit.DeviceModel.Trim();
        entity.AlarmCode = string.IsNullOrWhiteSpace(edit.AlarmCode) ? null : edit.AlarmCode.Trim();
        entity.Phenomenon = edit.Phenomenon.Trim();
        entity.CauseAnalysis = edit.CauseAnalysis.Trim();
        entity.Steps = edit.Steps.Trim();
        entity.SpareParts = edit.SpareParts;
        entity.Result = edit.Result.Trim();
        entity.Extra = edit.Extra is { Count: > 0 } ? JsonSerializer.Serialize(edit.Extra) : null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        // 案例修改时同步更新对应分块（FR-8.2 末句）：原位重渲染 + 重算向量
        if (entity.ChunkId is not null)
        {
            var chunk = await db.Chunks.FirstOrDefaultAsync(c => c.Id == entity.ChunkId, ct);
            if (chunk is not null)
            {
                chunk.Text = RenderOf(entity);
                chunk.SearchText = ChineseTokenizer.Tokenize(chunk.Text);
                chunk.Embedding = null;
                chunk.EmbeddingModel = null;
                db.ParseJobs.Add(NewEmbedJob(chunk.DocId, chunk.Id));
            }
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("case.update", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "fault_case", TargetId: caseId.ToString(), Detail: new { entity.CaseNo }), ct);
    }

    public async Task<IReadOnlyList<CaseRow>> SearchAsync(CaseSearchRequest req, CancellationToken ct = default)
    {
        var q = db.FaultCases.AsNoTracking().Where(c => c.CompanyId == me.CompanyId);
        if (!string.IsNullOrWhiteSpace(req.DeviceModel)) q = q.Where(c => c.DeviceModel.Contains(req.DeviceModel));
        if (!string.IsNullOrWhiteSpace(req.AlarmCode)) q = q.Where(c => c.AlarmCode == req.AlarmCode);
        if (req.SyncStatus is not null) q = q.Where(c => c.SyncStatus == req.SyncStatus);
        if (!string.IsNullOrWhiteSpace(req.Keyword))
            q = q.Where(c => c.Phenomenon.Contains(req.Keyword) ||
                             c.CauseAnalysis.Contains(req.Keyword) ||
                             c.Steps.Contains(req.Keyword));
        // 相关度与时间（FR-8.7）：现象命中优先于其他字段命中，同级按时间倒序
        var kw = req.Keyword;
        var ordered = string.IsNullOrWhiteSpace(kw)
            ? q.OrderByDescending(c => c.UpdatedAt)
            : q.OrderByDescending(c => c.Phenomenon.Contains(kw!)).ThenByDescending(c => c.UpdatedAt);
        return await ordered
            .Take(Math.Clamp(req.Limit, 1, 500))
            .Select(c => new CaseRow(c.Id, c.CaseNo, c.DeviceModel, c.AlarmCode, c.Phenomenon,
                c.Result, c.SyncStatus, c.SourceCompany, c.CreatedAt, c.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<CaseDetail?> GetAsync(Guid caseId, CancellationToken ct = default)
    {
        var c = await db.FaultCases.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == caseId && x.CompanyId == me.CompanyId, ct);
        if (c is null) return null;
        var extra = c.Extra is null
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(c.Extra) ?? [];
        return new CaseDetail(c.Id, c.CaseNo, c.DeviceModel, c.AlarmCode, c.Phenomenon,
            c.CauseAnalysis, c.Steps, c.SpareParts, c.Result, extra, c.SyncStatus, c.RejectReason,
            c.SourceCompany, c.CreatedById, c.CreatedAt, c.UpdatedAt, c.ChunkId);
    }

    public async Task<IReadOnlyList<SensitiveScanner.Hit>> CheckSensitiveAsync(Guid caseId, CancellationToken ct = default)
    {
        var c = await db.FaultCases.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == caseId && x.CompanyId == me.CompanyId, ct)
            ?? throw new DomainRuleException("CASE_NOT_FOUND", "案例不存在");
        var customerRows = await db.VocabTerms.AsNoTracking()
            .Where(v => v.VocabKey == VocabKeys.CustomerName)
            .Select(v => new { v.Value, v.Aliases })
            .ToListAsync(ct);
        var customers = customerRows
            .SelectMany(v => v.Aliases == null ? new[] { v.Value } : new[] { v.Value, v.Aliases })
            .ToList();
        var pattern = await config.GetAsync(ConfigKeys.IntentProjectNoPattern, ct);
        return SensitiveScanner.Scan(new Dictionary<string, string?>
        {
            ["故障现象"] = c.Phenomenon,
            ["原因判断"] = c.CauseAnalysis,
            ["处理步骤"] = c.Steps,
            ["所用备件"] = c.SpareParts,
            ["处理结果"] = c.Result,
            ["扩展字段"] = c.Extra
        }, customers, pattern);
    }

    public async Task SubmitAsync(Guid caseId, bool acknowledged, CancellationToken ct = default)
    {
        var c = await db.FaultCases.FirstOrDefaultAsync(
            x => x.Id == caseId && x.CompanyId == me.CompanyId, ct)
            ?? throw new DomainRuleException("CASE_NOT_FOUND", "案例不存在");
        if (c.SyncStatus == CaseSyncStatus.Pending)
            throw new DomainRuleException("CASE_ALREADY_SUBMITTED", "该案例已在总部待审");
        if (c.SyncStatus == CaseSyncStatus.Shared)
            throw new DomainRuleException("CASE_ALREADY_SHARED", "该案例已并入共享库；修改后如需重新共享请再次提交");

        // 检测有命中且用户未确认 → 拒（FR-8.3：确认后方可提交）
        var hits = await CheckSensitiveAsync(caseId, ct);
        if (hits.Count > 0 && !acknowledged)
            throw new DomainRuleException("SENSITIVE_HITS",
                "检测到疑似敏感内容，请核对命中项；确认无泄漏后带 acknowledged=true 重新提交：" +
                string.Join("；", hits.Take(6).Select(h => $"{h.Field}·{h.Kind}「{h.Match}」")));

        var hqUrl = (await config.GetStringAsync(ConfigKeys.SyncHqUrl, "", ct)).TrimEnd('/');
        var token = await config.GetStringAsync(ConfigKeys.SyncToken, "", ct);
        if (hqUrl.Length == 0)
            throw new DomainRuleException("SYNC_NOT_CONFIGURED", "尚未配置总部节点地址（sync.hq_url）");
        var company = await db.Companies.AsNoTracking().FirstAsync(x => x.Id == me.CompanyId, ct);

        var payload = new SubmittedCase(c.Id, c.CaseNo, company.ShortName ?? company.Name, me.Username,
            c.DeviceModel, c.AlarmCode, c.Phenomenon, c.CauseAnalysis, c.Steps, c.SpareParts, c.Result,
            c.Extra, c.CreatedAt);
        var http = httpFactory.CreateClient("sync");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{hqUrl}/api/sync/cases")
            { Content = System.Net.Http.Json.JsonContent.Create(payload) };
            req.Headers.Add("X-Sync-Token", token);
            var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new DomainRuleException("SUBMIT_HQ_REFUSED", $"总部节点拒收（HTTP {(int)resp.StatusCode}）");
        }
        catch (HttpRequestException ex)
        {
            throw new DomainRuleException("SUBMIT_HQ_UNREACHABLE",
                $"总部节点不可达：{ex.Message}。案例仍在本地生效，稍后重新提交即可（10.3）");
        }
        c.SyncStatus = CaseSyncStatus.Pending;
        c.SubmittedAt = DateTimeOffset.UtcNow;
        c.RejectReason = null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("case.submit", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "fault_case", TargetId: caseId.ToString(),
            Detail: new { c.CaseNo, sensitiveHits = hits.Count, acknowledged }), ct);
    }

    /// <summary>同型归并（FR-8.6）：同一设备型号折叠，组内按时间倒序取前三。</summary>
    public async Task<IReadOnlyList<CaseModelGroup>> SearchGroupedAsync(CaseSearchRequest req, CancellationToken ct = default)
    {
        var rows = await SearchAsync(req with { Limit = 500 }, ct);
        return rows.GroupBy(r => r.DeviceModel)
            .Select(g => new CaseModelGroup(g.Key, g.Count(), g.Take(3).ToList()))
            .OrderByDescending(g => g.Count)
            .ToList();
    }

    private static void Validate(CaseEdit e)
    {
        foreach (var (name, v) in new[]
        {
            ("设备型号", e.DeviceModel), ("故障现象", e.Phenomenon),
            ("原因判断", e.CauseAnalysis), ("处理步骤", e.Steps), ("处理结果", e.Result)
        })
            if (string.IsNullOrWhiteSpace(v))
                throw new DomainRuleException("CASE_FIELD_REQUIRED", $"{name}必填（FR-8.1）");
    }

    private static string RenderOf(FaultCase c)
    {
        var extra = c.Extra is null ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(c.Extra);
        return CaseRenderer.Render(c.CaseNo, c.DeviceModel, c.AlarmCode, c.Phenomenon,
            c.CauseAnalysis, c.Steps, c.SpareParts, c.Result, extra);
    }

    private ParseJob NewEmbedJob(Guid docId, long chunkId) => new()
    {
        Id = Guid.NewGuid(),
        DocId = docId,
        ChunkId = chunkId,
        Kind = ParseJobKind.EmbedOnly,
        QueuedBy = me.UserId,
        QueuedAt = DateTimeOffset.UtcNow
    };

    /// <summary>案例分块的虚拟容器文档：本公司私有库一份，不对应真实文件，
    /// 状态恒为 Parsed 以参与检索；语料管理的写操作被 DocumentService 挡住。</summary>
    private async Task<Document> GetOrCreateContainerAsync(CancellationToken ct)
    {
        var fileKey = VirtualFileKeyPrefix + me.CompanyId;
        var existing = await db.Documents.FirstOrDefaultAsync(d => d.FileKey == fileKey, ct);
        if (existing is not null) return existing;

        var kb = await db.KnowledgeBases
            .Where(k => k.Tier == KnowledgeBaseTier.Private && k.CompanyId == me.CompanyId && k.IsActive)
            .OrderBy(k => k.CreatedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new DomainRuleException("NO_PRIVATE_KB", "本公司尚无私有库，请先在知识库管理中创建");

        var container = new Document
        {
            Id = Guid.NewGuid(),
            KbId = kb.Id,
            FileName = "fault-cases.virtual",
            Title = "故障案例（系统维护）",
            FileKey = fileKey,
            FileSize = 0,
            ContentType = "application/x-ht-virtual",
            Classification = Classification.Internal, // 售前售后可见，客户与外部不可见（表 3-1）
            ParseStatus = ParseStatus.Parsed,
            ParseVersion = 1,
            UploadedById = me.UserId,
            UploadedAt = DateTimeOffset.UtcNow,
            ParsedAt = DateTimeOffset.UtcNow
        };
        db.Documents.Add(container);
        await db.SaveChangesAsync(ct);
        return container;
    }
}
