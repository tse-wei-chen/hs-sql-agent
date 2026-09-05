using System.Text.Json;
using HsSqlAgent.Approvals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Approvals.Webhook;

public static class WebhookApprovalEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteHandlerBuilder MapHsSqlAgentWebhookApprovalCallback(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/api/hs-sql-agent/approvals/webhook")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        return endpoints.MapPost(pattern, HandleCallbackAsync);
    }

    internal static async Task<IResult> HandleCallbackAsync(
        HttpRequest request,
        IDmlApprovalCompletionSink completionSink,
        IOptions<WebhookApprovalOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        WebhookApprovalServiceCollectionExtensions.ValidateOptions(options);

        if (!request.Headers.TryGetValue(WebhookApprovalHeaders.Event, out var eventHeader)
            || !string.Equals(eventHeader.ToString(), WebhookApprovalEvents.ApprovalCompleted, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid webhook event." });

        if (!request.Headers.TryGetValue(WebhookApprovalHeaders.Timestamp, out var timestampHeader)
            || !long.TryParse(timestampHeader.ToString(), out var unixTimestamp))
            return Results.BadRequest(new { error = "Missing or invalid webhook timestamp." });

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - unixTimestamp) > options.CallbackTimestampTolerance.TotalSeconds)
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        if (!request.Headers.TryGetValue(WebhookApprovalHeaders.Signature, out var signatureHeader))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        if (request.ContentLength is > 0 && request.ContentLength > options.MaxCallbackBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > options.MaxCallbackBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var body = buffer.ToArray();

        if (!WebhookApprovalSignature.Verify(
                options.SigningSecret,
                WebhookApprovalEvents.ApprovalCompleted,
                unixTimestamp,
                body,
                signatureHeader.ToString()))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        WebhookApprovalCompletionPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WebhookApprovalCompletionPayload>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Invalid webhook JSON." });
        }

        if (payload is null
            || string.IsNullOrWhiteSpace(payload.RequestId)
            || string.IsNullOrWhiteSpace(payload.ApprovalFingerprint))
            return Results.BadRequest(new { error = "Webhook completion payload is incomplete." });

        var completion = payload.Decision switch
        {
            WebhookApprovalDecision.Approved => DmlApprovalCompletion.Approve(
                payload.RequestId,
                payload.ApprovalFingerprint,
                payload.ApproverIdentity,
                payload.ExternalReference),
            WebhookApprovalDecision.Rejected => DmlApprovalCompletion.Reject(
                payload.RequestId,
                payload.ApprovalFingerprint,
                payload.Reason,
                payload.ApproverIdentity,
                payload.ExternalReference),
            _ => throw new InvalidOperationException("Unsupported webhook approval decision.")
        };

        var result = await completionSink.CompleteAsync(completion, cancellationToken);
        return result.Status switch
        {
            DmlApprovalCompletionStatus.Executed or DmlApprovalCompletionStatus.Rejected
                or DmlApprovalCompletionStatus.AlreadyCompleted => Results.Ok(result),
            DmlApprovalCompletionStatus.AlreadyProcessing => Results.Accepted(value: result),
            DmlApprovalCompletionStatus.NotFound => Results.NotFound(result),
            DmlApprovalCompletionStatus.InvalidApproval => Results.BadRequest(result),
            DmlApprovalCompletionStatus.Expired => Results.Json(result, statusCode: StatusCodes.Status410Gone),
            DmlApprovalCompletionStatus.Stale => Results.Conflict(result),
            DmlApprovalCompletionStatus.ConfigurationError or DmlApprovalCompletionStatus.Failed
                => Results.Json(result, statusCode: StatusCodes.Status500InternalServerError),
            _ => Results.Json(result, statusCode: StatusCodes.Status500InternalServerError)
        };
    }
}
