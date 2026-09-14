using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HT.Agent.Api.Security;

/// <summary>接口入口权限校验（FR-7.3）：越权请求直接拒绝并记录审计。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequirePermissionAttribute(string permission) : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var me = context.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
        if (!me.IsAuthenticated)
        {
            context.Result = new UnauthorizedResult();
            return;
        }
        if (me.Permissions.Contains(permission)) return;

        var audit = context.HttpContext.RequestServices.GetRequiredService<IAuditWriter>();
        await audit.WriteAsync(new AuditEntry("authz.denied", AuditResult.Denied,
            UserId: me.UserId, Username: me.Username, CompanyId: me.CompanyId,
            Detail: new
            {
                permission,
                path = context.HttpContext.Request.Path.Value,
                method = context.HttpContext.Request.Method
            },
            Ip: me.Ip, TerminalId: me.TerminalId));
        context.Result = new ObjectResult(new
        {
            code = "FORBIDDEN",
            message = "你的角色没有这项功能的权限。本次请求已被记录。"
        })
        { StatusCode = StatusCodes.Status403Forbidden };
    }
}
