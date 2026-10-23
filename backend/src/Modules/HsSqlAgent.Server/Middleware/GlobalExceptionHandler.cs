using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using SqlAgent.Service.Core.Execution;

namespace HsSqlAgent.Server.Middleware;

public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);

        var statusCode = exception switch
        {
            DmlCommitOutcomeUnknownException or DmlRollbackOutcomeUnknownException =>
                StatusCodes.Status500InternalServerError,
            UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
            ArgumentException => StatusCodes.Status400BadRequest,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            InvalidOperationException => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json";

        var isDev = httpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
        // Preserve the legacy JSON shape for ordinary errors. Both physically
        // uncertain outcomes must instead carry the machine-readable no-retry
        // contract; an unconfirmed ROLLBACK is not an ordinary HTTP conflict.
        var unknownCode = exception switch
        {
            DmlCommitOutcomeUnknownException => "dml.commit_outcome_unknown",
            DmlRollbackOutcomeUnknownException => "dml.rollback_outcome_unknown",
            _ => null
        };
        object response = unknownCode is not null
            ? new
            {
                error = exception.Message,
                type = exception.GetType().Name,
                code = unknownCode,
                status = "outcome_unknown",
                committed = (bool?)null,
                retryable = false,
                traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier,
                detail = isDev ? exception.StackTrace : null
            }
            : new
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
