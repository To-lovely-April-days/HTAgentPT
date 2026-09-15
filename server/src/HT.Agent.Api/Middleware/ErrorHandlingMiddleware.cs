using HT.Agent.Application.Abstractions;

namespace HT.Agent.Api.Middleware;

/// <summary>统一错误模型：业务规则 422 {code,message}；未预期异常 500，细节只进日志不出接口。</summary>
public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (DomainRuleException ex)
        {
            if (ctx.Response.HasStarted) throw;
            ctx.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await ctx.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message });
        }
        catch (ForbiddenException ex)
        {
            // 越权与业务规则分开：鉴权类拒绝统一 403（FR-7.3），抛出方已写审计
            if (ctx.Response.HasStarted) throw;
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "未处理异常 {Path}", ctx.Request.Path);
            if (ctx.Response.HasStarted) throw;
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsJsonAsync(new { code = "INTERNAL_ERROR", message = "服务内部错误" });
        }
    }
}
