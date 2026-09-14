using System.Text;
using HT.Agent.Api.Middleware;
using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Infrastructure;
using HT.Agent.Infrastructure.Auth;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<CurrentUserHolder>();
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUserHolder>());

builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

// 上传上限（FR-1.1 可配置）：Kestrel 放到物理上限，逻辑上限由 DocumentService 按运行时配置校验
var maxUploadMb = builder.Configuration.GetValue("Upload:KestrelLimitMb", 512);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxUploadMb * 1024L * 1024L);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUploadMb * 1024L * 1024L);

var app = builder.Build();

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseAuthentication();
app.UseMiddleware<SessionGuardMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

// 启动迁移与种子（部署可关：Database:MigrateOnStartup=false 时改用 CI/运维脚本执行迁移）
if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await Seeder.SeedAsync(db,
        scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
        app.Configuration);
}

app.Run();

public partial class Program;
