using System.Net.Http.Json;
using System.Text.Json;
using HsSqlAgent.Approvals;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Approvals.Webhook;

public sealed class WebhookDmlApprovalProvider(
    HttpClient httpClient,
    IOptions<WebhookApprovalOptions> options) : IDmlApprovalProvider
{
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
        message.Headers.TryAddWithoutValidation(WebhookApprovalHeaders.Timestamp, timestamp.ToString());
        message.Headers.TryAddWithoutValidation(
            WebhookApprovalHeaders.Signature,
            WebhookApprovalSignature.Compute(_options.SigningSecret, timestamp, body));
        message.Headers.TryAddWithoutValidation(WebhookApprovalHeaders.Event, WebhookApprovalEvents.ApprovalRequested);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();

        string? externalReference = null;
        if (response.Content.Headers.ContentLength is not 0)
        {
            var accepted = await response.Content.ReadFromJsonAsync<WebhookApprovalAccepted>(JsonOptions, timeout.Token);
            externalReference = accepted?.ExternalReference;
        }

        return DmlApprovalResult.Pending(
            request,
            string.IsNullOrWhiteSpace(externalReference) ? request.RequestId : externalReference);
    }
}
