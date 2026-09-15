using System.Security.Claims;
using HT.Agent.Api.Security;
using HT.Agent.Application.Abstractions;
using HT.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HT.Agent.Api.Middleware;

/// <summary>会话守卫。令牌有效之外还须满足：账号仍启用（FR-7.1 停用立即失权）、
/// 会话标识仍是当前会话（决策 4 互斥）。不满足时返回 401 与明确原因码——
/// 被顶下线的一端收到的是 SESSION_SUPERSEDED，不是笼统的「登录已过期」。
/// 每个请求查库校验：企业内部规模下正确性优先于缓存带来的微小收益。</summary>
public class SessionGuardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, AppDbContext db, CurrentUserHolder holder)
    {
        // 匿名端点不受会话守卫约束：被顶下线的用户带着失效令牌也必须能打到 /api/auth/login 重新登录
        if (ctx.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
        {
            await next(ctx);
            return;
        }
        var identity = ctx.User.Identity;
        if (identity is not { IsAuthenticated: true })
        {
            await next(ctx);
            return;
        }

        var sub = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub");
        var sid = ctx.User.FindFirstValue("sid");
        if (!Guid.TryParse(sub, out var userId) || !Guid.TryParse(sid, out var sessionId))
        {
            await Reject(ctx, SessionEndCodes.Expired, "令牌无效");
            return;
        }

        var user = await db.Users.AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ctx.RequestAborted);
        if (user is null || !user.IsActive)
        {
            await Reject(ctx, SessionEndCodes.Disabled, "账号已停用。如需恢复请联系系统管理员。");
            return;
        }
        if (user.ActiveSessionId != sessionId)
        {
            await Reject(ctx, SessionEndCodes.Superseded,
                "你的账号已在其他终端登录，本处会话被终止。同一账号同一时刻只允许一处在线。");
            return;
        }
        if (user.Role is null)
        {
            await Reject(ctx, SessionEndCodes.Expired, "账号尚未指派角色");
            return;
        }

        holder.Populate(
            user.Id, user.Username, user.Role.Code, user.CompanyId, sessionId,
            user.Role.Classifications, user.Role.Permissions, user.CustomerNo,
            ctx.Connection.RemoteIpAddress?.ToString(),
            ctx.Request.Headers["X-Terminal-Id"].FirstOrDefault());
        await next(ctx);
    }

    private static async Task Reject(HttpContext ctx, string code, string message)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { code, message });
    }
}
