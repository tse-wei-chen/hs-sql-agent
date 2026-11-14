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

        // Provider and application exceptions may contain credentials, raw SQL,
        // parameters and connection paths. Keep those details in server logs only.
        // Preserve the HTTP status and legacy JSON field shape, not exception.Message.
        var safeMessage = exception switch
        {
            DmlCommitOutcomeUnknownException =>
                "The DML transaction may already have committed. Do not retry; reconcile database state.",
            DmlRollbackOutcomeUnknownException =>
                "The DML transaction could not be confirmed rolled back. Do not retry; reconcile database state.",
            UnauthorizedAccessException => "Access denied.",
            ArgumentException => "Invalid request.",
            KeyNotFoundException => "Requested resource was not found.",
            InvalidOperationException => "Request cannot be completed in its current state.",
            _ => "Internal server error. Check server diagnostics."
        };
        var unknownCode = exception switch
        {
            DmlCommitOutcomeUnknownException => "dml.commit_outcome_unknown",
            DmlRollbackOutcomeUnknownException => "dml.rollback_outcome_unknown",
            _ => null
        };
        object response = unknownCode is not null
            ? new
            {
                error = safeMessage,
                type = exception.GetType().Name,
                code = unknownCode,
                status = "outcome_unknown",
                committed = (bool?)null,
                retryable = false,
                traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier,
                detail = (string?)null
            }
            : new
            {
                error = safeMessage,
                type = exception.GetType().Name,
                traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier,
                detail = (string?)null
            };

        await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);
        return true;
    }
}
