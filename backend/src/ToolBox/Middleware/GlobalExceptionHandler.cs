using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace ToolBox.Middleware;

public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);

        var statusCode = exception switch
        {
            UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
            ArgumentException => StatusCodes.Status400BadRequest,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            InvalidOperationException => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json";

        var isDev = httpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
        var response = new
        {
            error = exception.Message,
            type = exception.GetType().Name,
            traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier,
            detail = isDev ? exception.StackTrace : null
        };

        await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);
        return true;
    }
}
