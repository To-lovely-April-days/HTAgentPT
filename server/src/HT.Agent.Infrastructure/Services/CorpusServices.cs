using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Infrastructure.Services;

public class VocabService(AppDbContext db, IAuditWriter audit, ICurrentUser me) : IVocabService
{
    public async Task<IReadOnlyList<VocabRow>> ListAsync(string vocabKey, CancellationToken ct = default)
        => await db.VocabTerms.AsNoTracking()
            .Where(v => v.VocabKey == vocabKey)
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Value)
            .Select(v => new VocabRow(v.Id, v.VocabKey, v.Value, v.Aliases, v.IsActive, v.SortOrder))
            .ToListAsync(ct);

    public async Task<Guid> AddAsync(string vocabKey, string value, string? aliases, CancellationToken ct = default)
    {
        if (await db.VocabTerms.AnyAsync(v => v.VocabKey == vocabKey && v.Value == value, ct))
            throw new DomainRuleException("VOCAB_DUP", $"词表 {vocabKey} 已有取值「{value}」");
        var term = new VocabTerm { Id = Guid.NewGuid(), VocabKey = vocabKey, Value = value, Aliases = aliases };
        db.VocabTerms.Add(term);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("vocab.add", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "vocab_term", TargetId: term.Id.ToString(), Detail: new { vocabKey, value }), ct);
        return term.Id;
    }

    public async Task DisableAsync(Guid id, CancellationToken ct = default)
    {
        var term = await db.VocabTerms.FindAsync([id], ct)
            ?? throw new DomainRuleException("VOCAB_NOT_FOUND", "词表项不存在");
        term.IsActive = false;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("vocab.disable", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "vocab_term", TargetId: id.ToString(), Detail: new { term.VocabKey, term.Value }), ct);
    }

    public Task<bool> IsValidAsync(string vocabKey, string value, CancellationToken ct = default)
        => db.VocabTerms.AnyAsync(v => v.VocabKey == vocabKey && v.Value == value && v.IsActive, ct);
}

public class KnowledgeBaseService(AppDbContext db, IAuditWriter audit, ICurrentUser me) : IKnowledgeBaseService
{
    public async Task<IReadOnlyList<KbRow>> ListAsync(CancellationToken ct = default)
    {
        // 库级统计（FR-2.5）
        var stats = await db.Documents.AsNoTracking()
            .GroupBy(d => d.KbId)
            .Select(g => new { KbId = g.Key, Docs = g.Count(), Last = g.Max(d => (DateTimeOffset?)d.UploadedAt) })
            .ToDictionaryAsync(x => x.KbId, ct);
        var chunkStats = await db.Chunks.AsNoTracking()
            .Where(c => c.IsActive)
            .GroupBy(c => c.KbId)
            .Select(g => new { KbId = g.Key, Chunks = g.LongCount() })
            .ToDictionaryAsync(x => x.KbId, ct);
        var kbs = await db.KnowledgeBases.AsNoTracking().OrderBy(k => k.Tier).ThenBy(k => k.Name).ToListAsync(ct);
        return kbs.Select(k => new KbRow(
            k.Id, k.Name, k.Tier, k.DefaultChunkStrategy, k.Description, k.IsActive,
            stats.TryGetValue(k.Id, out var s) ? s.Docs : 0,
            chunkStats.TryGetValue(k.Id, out var c) ? c.Chunks : 0,
            stats.TryGetValue(k.Id, out var s2) ? s2.Last : null)).ToList();
    }

    public async Task<Guid> CreateAsync(string name, KnowledgeBaseTier tier, ChunkStrategy defaultStrategy,
        string? description, CancellationToken ct = default)
    {
        var kb = new KnowledgeBase
        {
            Id = Guid.NewGuid(),
            Name = name,
            Tier = tier,
            CompanyId = tier == KnowledgeBaseTier.Shared ? null : me.CompanyId,
            DefaultChunkStrategy = defaultStrategy,
            Description = description,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.KnowledgeBases.Add(kb);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("kb.create", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "knowledge_base", TargetId: kb.Id.ToString(), Detail: new { name, tier }), ct);
        return kb.Id;
    }

    public async Task UpdateAsync(Guid id, string name, ChunkStrategy defaultStrategy, string? description,
        CancellationToken ct = default)
    {
        var kb = await db.KnowledgeBases.FindAsync([id], ct)
            ?? throw new DomainRuleException("KB_NOT_FOUND", "知识库不存在");
        // tier 创建后不可修改（FR-2.1）——本方法根本不接收 tier 参数，接口层也没有这条路
        kb.Name = name;
        kb.DefaultChunkStrategy = defaultStrategy;
        kb.Description = description;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry("kb.update", AuditResult.Success,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            TargetType: "knowledge_base", TargetId: id.ToString(), Detail: new { name, defaultStrategy }), ct);
    }
}
