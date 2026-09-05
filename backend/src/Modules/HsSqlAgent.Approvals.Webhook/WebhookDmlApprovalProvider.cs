using System.Text.Json;
using HsSqlAgent.Approvals;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Approvals.Webhook;

public sealed class WebhookDmlApprovalProvider(
    HttpClient httpClient,
    IOptions<WebhookApprovalOptions> options) : IDmlApprovalProvider
{
    private const int MaxAcceptedResponseBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WebhookApprovalOptions _options = options.Value;

    public async ValueTask<DmlApprovalResult> RequestApprovalAsync(
        DmlApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        WebhookApprovalServiceCollectionExtensions.ValidateOptions(_options);
        if (request.DurableUntil is null)
            throw new InvalidOperationException("Webhook approval requires durable DML approval support.");

        var envelope = new WebhookApprovalRequestEnvelope(
            "1",
            _options.CallbackUrl!.AbsoluteUri,
            request);
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new ByteArrayContent(body)
        };
        message.Content.Headers.ContentType = new("application/json");
        message.Headers.TryAddWithoutValidation(WebhookApprovalHeaders.Event, WebhookApprovalEvents.ApprovalRequested);
        message.Headers.TryAddWithoutValidation(WebhookApprovalHeaders.Timestamp, timestamp.ToString());
        message.Headers.TryAddWithoutValidation(
            WebhookApprovalHeaders.Signature,
            WebhookApprovalSignature.Compute(
                _options.SigningSecret,
                WebhookApprovalEvents.ApprovalRequested,
                timestamp,
                body));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();

        var accepted = await ReadAcceptedResponseAsync(response.Content, timeout.Token);
        return DmlApprovalResult.Pending(
            request,
            string.IsNullOrWhiteSpace(accepted?.ExternalReference)
                ? request.RequestId
                : accepted.ExternalReference);
    }

    private static async Task<WebhookApprovalAccepted?> ReadAcceptedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxAcceptedResponseBytes)
            throw new InvalidOperationException("Webhook approval response exceeded the 16 KiB limit.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxAcceptedResponseBytes)
                throw new InvalidOperationException("Webhook approval response exceeded the 16 KiB limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        if (buffer.Length == 0) return null;
        try
        {
            return JsonSerializer.Deserialize<WebhookApprovalAccepted>(buffer.ToArray(), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Webhook approval response contained invalid JSON.", exception);
        }
    }
}
