using System.Text.Json;
using HT.Agent.Domain;
using HT.Agent.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;

namespace HT.Agent.Infrastructure.Persistence;

public class PersistenceOptions
{
    /// <summary>向量维度随向量化模型定（默认 bge-m3 的 1024）。换模型改维度须全量重建（E15）。</summary>
    public int VectorDimension { get; set; } = 1024;
}

public class AppDbContext(DbContextOptions<AppDbContext> options, IOptions<PersistenceOptions> persistence)
    : DbContext(options)
{
    private readonly int _dim = persistence.Value.VectorDimension;

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserTerminal> UserTerminals => Set<UserTerminal>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SyncLog> SyncLogs => Set<SyncLog>();
    public DbSet<SysConfig> SysConfigs => Set<SysConfig>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<KnowledgeBase> KnowledgeBases => Set<KnowledgeBase>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocMetadata> DocMetadatas => Set<DocMetadata>();
    public DbSet<VocabTerm> VocabTerms => Set<VocabTerm>();
    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<ParseJob> ParseJobs => Set<ParseJob>();
    public DbSet<PublishRecord> PublishRecords => Set<PublishRecord>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<FaultCase> FaultCases => Set<FaultCase>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<CustomerDevice> CustomerDevices => Set<CustomerDevice>();
    public DbSet<Term> Terms => Set<Term>();
    public DbSet<TranslationTask> TranslationTasks => Set<TranslationTask>();
    public DbSet<Clause> Clauses => Set<Clause>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateSlot> TemplateSlots => Set<TemplateSlot>();
    public DbSet<GenerationSession> GenerationSessions => Set<GenerationSession>();
    public DbSet<QaSession> QaSessions => Set<QaSession>();
    public DbSet<QaMessage> QaMessages => Set<QaMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("vector");
        b.HasPostgresExtension("pg_trgm");

        b.Entity<Company>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Classifications).HasColumnType("jsonb")
                .HasConversion(JsonList<Classification>(), JsonListComparer<Classification>());
            e.Property(x => x.Permissions).HasColumnType("jsonb")
                .HasConversion(JsonList<string>(), JsonListComparer<string>());
        });

        b.Entity<AppUser>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.HasOne(x => x.Role).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Company).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.CustomerNo);
        });

        b.Entity<UserTerminal>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.TerminalId }).IsUnique();
        });

        // 审计：只写不改（FR-9.1）。分区键须入主键，按月分区在专用迁移里以原生 SQL 落地（7.2）。
        b.Entity<AuditLog>(e =>
        {
            e.HasKey(x => new { x.Id, x.At });
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
            e.Property(x => x.Detail).HasColumnType("jsonb");
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.UserId, x.At });
            e.HasIndex(x => new { x.Action, x.At });
        });

        b.Entity<SyncLog>(e => e.Property(x => x.Detail).HasColumnType("jsonb"));

        b.Entity<SysConfig>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Value).HasColumnType("jsonb");
        });

        b.Entity<KnowledgeBase>(e =>
        {
            e.HasIndex(x => new { x.Tier, x.CompanyId });
        });

        b.Entity<Document>(e =>
        {
            // 过滤条件走索引而非全表扫描（7.2）
            e.HasIndex(x => new { x.KbId, x.Classification, x.ParseStatus });
            e.HasOne(x => x.Metadata).WithOne(m => m.Document!)
                .HasForeignKey<DocMetadata>(m => m.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DocMetadata>(e =>
        {
            e.HasKey(x => x.DocumentId);
            e.HasIndex(x => x.ProjectNo);
            e.HasIndex(x => x.CustomerName);
        });

        b.Entity<VocabTerm>(e =>
        {
            e.HasIndex(x => new { x.VocabKey, x.Value }).IsUnique();
        });

        b.Entity<Chunk>(e =>
        {
            // kb_id 与 classification 冗余，使权限过滤参与候选选取（7.2、FR-4.4）
            e.HasIndex(x => new { x.KbId, x.Classification });
            e.HasIndex(x => x.DocId);
            e.Property(x => x.Embedding).HasColumnType($"vector({_dim})");
            e.HasOne(x => x.Doc).WithMany().OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ParseJob>(e =>
        {
            e.HasIndex(x => new { x.Status, x.QueuedAt });
            e.HasOne(x => x.Doc).WithMany().OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Project>(e =>
        {
            e.HasKey(x => x.ProjectNo);
            e.Property(x => x.ProjectNo).HasMaxLength(64);
            e.Property(x => x.ContractAmount).HasColumnType("numeric(14,2)");
            e.HasIndex(x => new { x.CustomerName, x.Year, x.DeviceType });
        });

        b.Entity<FaultCase>(e =>
        {
            e.HasIndex(x => x.CaseNo).IsUnique();
            e.HasIndex(x => new { x.DeviceModel, x.AlarmCode });
            e.HasIndex(x => x.SyncStatus);
            e.Property(x => x.Extra).HasColumnType("jsonb");
        });

        b.Entity<Ticket>(e =>
        {
            e.HasIndex(x => x.TicketNo).IsUnique();
            e.HasIndex(x => x.CustomerNo);
            e.Property(x => x.Updates).HasColumnType("jsonb");
        });

        b.Entity<CustomerDevice>(e =>
        {
            e.HasIndex(x => new { x.CustomerNo, x.DeviceNo }).IsUnique();
        });

        b.Entity<Term>(e =>
        {
            e.HasIndex(x => new { x.Domain, x.Zh });
        });

        b.Entity<TranslationTask>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.Report).HasColumnType("jsonb");
        });

        b.Entity<Clause>(e =>
        {
            e.HasIndex(x => new { x.Category, x.Code });
        });

        b.Entity<Template>(e =>
        {
            e.HasMany(x => x.Slots).WithOne(s => s.Template!).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TemplateSlot>(e =>
        {
            e.HasIndex(x => new { x.TemplateId, x.Tag }).IsUnique();
            e.Property(x => x.Choices).HasColumnType("jsonb");
            e.Property(x => x.SubFields).HasColumnType("jsonb");
        });

        // 槽位取值按整会话序列化（7.2：避免数十项规模的逐槽往返）
        b.Entity<GenerationSession>(e =>
        {
            e.Property(x => x.SlotValues).HasColumnType("jsonb");
        });

        b.Entity<QaSession>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
            e.HasMany(x => x.Messages).WithOne(m => m.Session!).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<QaMessage>(e =>
        {
            e.Property(x => x.Sources).HasColumnType("jsonb");
        });

        ApplyConventions(b);
    }

    /// <summary>全局约定：枚举存字符串（可读、与 PRD 的取值描述一致），表列名 snake_case。</summary>
    private static void ApplyConventions(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            entity.SetTableName(ToSnake(entity.ClrType.Name));
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnake(property.Name));
                var t = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (t.IsEnum && property.GetValueConverter() is null)
                    property.SetProviderClrType(typeof(string));
            }
            foreach (var key in entity.GetKeys())
                key.SetName(ToSnake(key.GetName()!));
            foreach (var fk in entity.GetForeignKeys())
                fk.SetConstraintName(ToSnake(fk.GetConstraintName()!));
            foreach (var index in entity.GetIndexes())
                index.SetDatabaseName(ToSnake(index.GetDatabaseName()!));
        }
    }

    internal static string ToSnake(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static ValueConverter<List<T>, string> JsonList<T>() => new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => JsonSerializer.Deserialize<List<T>>(v, (JsonSerializerOptions?)null) ?? new List<T>());

    private static ValueComparer<List<T>> JsonListComparer<T>() => new(
        (a, b) => (a ?? new List<T>()).SequenceEqual(b ?? new List<T>()),
        v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x)),
        v => v.ToList());
}
