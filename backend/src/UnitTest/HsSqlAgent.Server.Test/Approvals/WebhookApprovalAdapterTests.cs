using System.Net;
using System.Text;
using System.Text.Json;
using HsSqlAgent.Approvals;
using HsSqlAgent.Approvals.Webhook;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HsSqlAgent.Server.Test.Approvals;

public sealed class WebhookApprovalAdapterTests
{
    private const string Secret = "webhook-test-secret-that-is-at-least-32-bytes";

    [Fact]
    public void Signature_BindsEventAndBody()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = Encoding.UTF8.GetBytes("{\"requestId\":\"req-1\"}");
        var signature = WebhookApprovalSignature.Compute(
            Secret,
            WebhookApprovalEvents.ApprovalCompleted,
            timestamp,
            body);

        Assert.True(WebhookApprovalSignature.Verify(
            Secret,
            WebhookApprovalEvents.ApprovalCompleted,
            timestamp,
            body,
            signature));
        Assert.False(WebhookApprovalSignature.Verify(
            Secret,
            WebhookApprovalEvents.ApprovalRequested,
            timestamp,
            body,
            signature));

        body[body.Length - 2] ^= 1;
        Assert.False(WebhookApprovalSignature.Verify(
            Secret,
            WebhookApprovalEvents.ApprovalCompleted,
            timestamp,
            body,
            signature));
    }

    [Fact]
    public async Task Provider_SendsSignedEvidenceAndReturnsPending()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var options = Options.Create(CreateOptions());
        var provider = new WebhookDmlApprovalProvider(client, options);
        var request = CreateApprovalRequest();

        var result = await provider.RequestApprovalAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(DmlApprovalDecision.Pending, result.Decision);
        Assert.Equal("EXT-42", result.ExternalReference);
        Assert.Equal(WebhookApprovalEvents.ApprovalRequested, handler.Event);
        Assert.NotNull(handler.Body);
        Assert.NotNull(handler.Timestamp);
        Assert.NotNull(handler.Signature);
        Assert.True(WebhookApprovalSignature.Verify(
            Secret,
            handler.Event!,
            long.Parse(handler.Timestamp!),
            handler.Body!,
            handler.Signature!));

        var envelope = JsonSerializer.Deserialize<WebhookApprovalRequestEnvelope>(
            handler.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(envelope);
        Assert.Equal("1", envelope.SchemaVersion);
        Assert.Equal("https://sql-agent.example.test/api/hs-sql-agent/approvals/webhook", envelope.CallbackUrl);
        Assert.Equal(request.RequestId, envelope.Request.RequestId);
        Assert.Equal(request.ApprovalFingerprint, envelope.Request.ApprovalFingerprint);
    }

    [Fact]
    public async Task Callback_ValidApproval_ForwardsExactCompletionToSink()
    {
        var options = CreateOptions();
        var payload = new WebhookApprovalCompletionPayload(
            "req-1",
            new string('a', 64),
            WebhookApprovalDecision.Approved,
            "reviewer@example.test",
            "EXT-42");
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var context = CreateCallbackContext(body, timestamp, options);
        var sink = new RecordingCompletionSink(new(
            DmlApprovalCompletionStatus.Executed,
            "executed",
            1));

        await WebhookApprovalEndpointRouteBuilderExtensions.HandleCallbackAsync(
            context.Request,
            sink,
            Options.Create(options),
            TestContext.Current.CancellationToken);

        Assert.NotNull(sink.Completion);
        Assert.Equal(DmlApprovalDecision.Approved, sink.Completion.Decision);
        Assert.Equal("req-1", sink.Completion.RequestId);
        Assert.Equal(new string('a', 64), sink.Completion.ApprovalFingerprint);
        Assert.Equal("reviewer@example.test", sink.Completion.ApproverIdentity);
        Assert.Equal("EXT-42", sink.Completion.ExternalReference);
    }

    [Fact]
    public async Task Callback_ExpiredTimestamp_IsRejectedBeforeCompletionSink()
    {
        var options = CreateOptions();
        options.CallbackTimestampTolerance = TimeSpan.FromMinutes(1);
        var payload = new WebhookApprovalCompletionPayload(
            "req-1",
            new string('a', 64),
            WebhookApprovalDecision.Approved);
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var context = CreateCallbackContext(body, timestamp, options);
        var sink = new RecordingCompletionSink(new(DmlApprovalCompletionStatus.Executed, "unexpected"));

        var result = await WebhookApprovalEndpointRouteBuilderExtensions.HandleCallbackAsync(
            context.Request,
            sink,
            Options.Create(options),
            TestContext.Current.CancellationToken);

        Assert.Null(sink.Completion);
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public void Registration_RejectsSecondApprovalProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDmlApprovalProvider, ExistingProvider>();

        Assert.Throws<InvalidOperationException>(() =>
            services.AddHsSqlAgentWebhookApproval(_ => { }));
    }

    private static WebhookApprovalOptions CreateOptions() => new()
    {
        Endpoint = new Uri("https://approval.example.test/hssqlagent/requests"),
        CallbackUrl = new Uri("https://sql-agent.example.test/api/hs-sql-agent/approvals/webhook"),
        SigningSecret = Secret
    };

    private static DmlApprovalRequest CreateApprovalRequest() => new(
        "req-1",
        "Delete stale order",
        "mcp-key:1",
        "db-management:2",
        "Postgres",
        "orders-db",
        [new DmlApprovalStatement(0, "delete", "orders", 1, "[{\"id\":42}]")],
        1,
        new string('a', 64),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(5),
        DateTimeOffset.UtcNow.AddHours(1));

    private static DefaultHttpContext CreateCallbackContext(
        byte[] body,
        long timestamp,
        WebhookApprovalOptions options)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Request.ContentType = "application/json";
        context.Request.Headers[WebhookApprovalHeaders.Event] = WebhookApprovalEvents.ApprovalCompleted;
        context.Request.Headers[WebhookApprovalHeaders.Timestamp] = timestamp.ToString();
        context.Request.Headers[WebhookApprovalHeaders.Signature] = WebhookApprovalSignature.Compute(
            options.SigningSecret,
            WebhookApprovalEvents.ApprovalCompleted,
            timestamp,
            body);
        return context;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public byte[]? Body { get; private set; }
        public string? Timestamp { get; private set; }
        public string? Signature { get; private set; }
        public string? Event { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Timestamp = request.Headers.GetValues(WebhookApprovalHeaders.Timestamp).Single();
            Signature = request.Headers.GetValues(WebhookApprovalHeaders.Signature).Single();
            Event = request.Headers.GetValues(WebhookApprovalHeaders.Event).Single();
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("{\"externalReference\":\"EXT-42\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RecordingCompletionSink(DmlApprovalCompletionResult result) : IDmlApprovalCompletionSink
    {
        public DmlApprovalCompletion? Completion { get; private set; }

        public ValueTask<DmlApprovalCompletionResult> CompleteAsync(
            DmlApprovalCompletion completion,
            CancellationToken cancellationToken = default)
        {
            Completion = completion;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ExistingProvider : IDmlApprovalProvider
    {
        public ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DmlApprovalResult.Reject(request));
    }
}
