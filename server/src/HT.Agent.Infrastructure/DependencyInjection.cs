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

        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<IJwtIssuer, JwtIssuer>();
        services.AddSingleton<IAuditWriter, AuditWriter>();
        services.AddSingleton<IRuntimeConfig, RuntimeConfig>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        // 桩开关：无模型服务的环境整条链路仍可运行（开发/测试）；生产配置真实地址
        var useStubs = config.GetValue<bool>("Models:UseStubs");
        if (useStubs)
        {
            services.AddSingleton<IChatModelClient, StubChatClient>();
            services.AddSingleton<IEmbeddingClient, StubEmbeddingClient>();
            services.AddSingleton<IRerankClient, StubRerankClient>();
            services.AddSingleton<IDocumentParserClient, StubParserClient>();
        }
        else
        {
            services.AddSingleton<IChatModelClient, OpenAiChatClient>();
            services.AddSingleton<IEmbeddingClient, HttpEmbeddingClient>();
            services.AddSingleton<IRerankClient, HttpRerankClient>();
            services.AddSingleton<IDocumentParserClient, HttpParserClient>();
        }

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IVocabService, VocabService>();
        services.AddScoped<IKnowledgeBaseService, KnowledgeBaseService>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IRetrievalService, RetrievalService>();
        services.AddScoped<IQaService, QaService>();

        services.AddHostedService<ParseWorker>();
        return services;
    }
}
