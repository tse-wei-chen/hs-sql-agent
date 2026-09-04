using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HsSqlAgent.Server.Filters;

/// <summary>
/// Converts unhandled exceptions from HsSqlAgent MVC actions without installing a host-wide
/// IExceptionHandler. Exceptions raised by the host application's controllers are untouched.
/// </summary>
internal sealed class HsSqlAgentExceptionFilter(ILogger<HsSqlAgentExceptionFilter> logger) : IAsyncExceptionFilter
{
    public Task OnExceptionAsync(ExceptionContext context)
    {
        var exception = context.Exception;
        logger.LogError(exception, "Unhandled HsSqlAgent exception: {Message}", exception.Message);

        var statusCode = exception switch
        {
            UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
            ArgumentException => StatusCodes.Status400BadRequest,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            InvalidOperationException => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };

        var isDevelopment = context.HttpContext.RequestServices
            .GetService<IWebHostEnvironment>()?
            .IsDevelopment() == true;

        context.Result = new ObjectResult(new
        {
            error = exception.Message,
            type = exception.GetType().Name,
            traceId = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier,
            detail = isDevelopment ? exception.StackTrace : null
        })
        {
            StatusCode = statusCode
        };
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }
}
