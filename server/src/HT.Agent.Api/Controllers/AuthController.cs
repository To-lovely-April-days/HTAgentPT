using HT.Agent.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HT.Agent.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService auth, ICurrentUser me) : ControllerBase
{
    public record LoginBody(string Username, string Password, string? TerminalId, string? TerminalName);

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginBody body, CancellationToken ct)
    {
        var terminalId = body.TerminalId
            ?? Request.Headers["X-Terminal-Id"].FirstOrDefault()
            ?? "unknown";
        var result = await auth.LoginAsync(new LoginRequest(
            body.Username, body.Password, terminalId, body.TerminalName,
            HttpContext.Connection.RemoteIpAddress?.ToString()), ct);
        if (!result.Ok)
            return Unauthorized(new { code = result.ReasonCode, message = result.Message });
        return Ok(new { token = result.Token, profile = result.Profile, supersededOther = result.SupersededOther });
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(me.UserId, ct);
        return NoContent();
    }

    /// <summary>当前身份与权限（C2 框架启动时拉取；C3 个人中心「我的信息」）。
    /// 与登录返回的 profile 同构，前端刷新页面后凭令牌直接恢复工作台。</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var profile = await auth.ProfileAsync(me.UserId, ct);
        if (profile is null)
            return Unauthorized(new { code = "SESSION_EXPIRED", message = "会话已失效，请重新登录" });
        return Ok(profile);
    }
}
