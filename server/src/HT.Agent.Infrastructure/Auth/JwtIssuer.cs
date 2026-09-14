using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using HT.Agent.Application.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HT.Agent.Infrastructure.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "ht-agent";
    public string Audience { get; set; } = "ht-agent";
    /// <summary>部署配置提供（10.4：不硬编码）。开发默认值仅供本机。</summary>
    public string SigningKey { get; set; } = "dev-only-signing-key-change-in-deployment!";
}

public class JwtIssuer(IOptions<JwtOptions> options) : IJwtIssuer
{
    private readonly JwtOptions _opt = options.Value;

    public string Issue(Guid userId, string username, string roleCode, Guid companyId, Guid sessionId, TimeSpan lifetime)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim("name", username),
                new Claim("role", roleCode),
                new Claim("company", companyId.ToString()),
                // 互斥会话标识（决策 4）：与 app_user.active_session_id 比对，不一致即被顶下线
                new Claim("sid", sessionId.ToString())
            ],
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
