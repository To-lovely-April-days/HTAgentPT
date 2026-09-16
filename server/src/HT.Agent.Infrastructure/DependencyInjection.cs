using HT.Agent.Application.Abstractions;
using HT.Agent.Infrastructure.Auth;
using HT.Agent.Infrastructure.Clients;
using HT.Agent.Infrastructure.Persistence;
using HT.Agent.Infrastructure.Services;
using HT.Agent.Infrastructure.Storage;
using HT.Agent.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HT.Agent.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PersistenceOptions>(config.GetSection("Persistence"));
        services.Configure<JwtOptions>(config.GetSection("Jwt"));
        services.Configure<StorageOptions>(config.GetSection("Storage"));
        services.Configure<NodeOptions>(config.GetSection("Node"));

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var conn = config.GetConnectionString("Default")
                ?? throw new InvalidOperationException("缺少连接串 ConnectionStrings:Default");
            options.UseNpgsql(conn, o => o.UseVector());
        });

        services.AddMemoryCache();
        services.AddHttpClient("model");
        services.AddHttpClient("parser");
        services.AddHttpClient("sync");

        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<IJwtIssuer, JwtIssuer>();
        services.AddSingleton<IAuditWriter, AuditWriter>();
        services.AddSingleton<IRuntimeConfig, RuntimeConfig>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        // 四个模型槽位（对话/向量化/重排/解析）全部经 Switching* 客户端按运行时配置选择——
        // 在线接口与本地部署在「系统设置」里切，改后即时生效不用重启（FR-9.6）。
        // 部署期 Models:UseStubs / Models:Parser 只作为运行时未选择时的兜底默认。
        services.AddSingleton<StubChatClient>();
        services.AddSingleton<OpenAiChatClient>();
        services.AddSingleton<IChatModelClient, SwitchingChatClient>();
        services.AddSingleton<StubEmbeddingClient>();
        services.AddSingleton<HttpEmbeddingClient>();
        services.AddSingleton<IEmbeddingClient, SwitchingEmbeddingClient>();
        services.AddSingleton<StubRerankClient>();
        services.AddSingleton<HttpRerankClient>();
        services.AddSingleton<IRerankClient, SwitchingRerankClient>();
        services.AddSingleton<StubParserClient>();
        services.AddSingleton<HttpParserClient>();
        services.AddSingleton<MinerUParserClient>();
        services.AddSingleton<MinerUOnlineParserClient>();
        services.AddSingleton<OfficeParser>();
        services.AddSingleton<IDocumentParserClient, SwitchingParserClient>();

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IVocabService, VocabService>();
        services.AddScoped<IKnowledgeBaseService, KnowledgeBaseService>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<IFaultCaseService, FaultCaseService>();
        services.AddScoped<ITranslationService, TranslationService>();
        services.AddScoped<ITermService, TermService>();
        services.AddScoped<ISharedSyncService, SharedSyncService>();
        services.AddScoped<IPublishService, PublishService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IPortalService, PortalService>();
        services.AddScoped<ITemplateService, TemplateService>();
        services.AddScoped<IGenerationService, GenerationService>();
        services.AddScoped<IGenerationChatService, GenerationChatService>();
        services.AddScoped<IClauseService, ClauseService>();
        services.AddScoped<ICaseReviewService, CaseReviewService>();
        services.AddScoped<IRetrievalService, RetrievalService>();
        services.AddScoped<IQaService, QaService>();

        services.AddScoped<BackupService>();
        services.AddHostedService<ParseWorker>();
        services.AddHostedService<BackupWorker>();
        return services;
    }
}
