using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using HsSqlAgent.Approvals;

namespace HsSqlAgent.Approvals.Webhook;

public static class WebhookApprovalHeaders
{
    public const string Timestamp = "X-HsSqlAgent-Webhook-Timestamp";
    public const string Signature = "X-HsSqlAgent-Webhook-Signature";
    public const string Event = "X-HsSqlAgent-Webhook-Event";
}

public static class WebhookApprovalEvents
{
    public const string ApprovalRequested = "dml.approval.requested";
    public const string ApprovalCompleted = "dml.approval.completed";
}

public sealed record WebhookApprovalRequestEnvelope(
    string SchemaVersion,
    string CallbackUrl,
    DmlApprovalRequest Request);

public sealed record WebhookApprovalAccepted(string? ExternalReference = null);

public sealed record WebhookApprovalCompletionPayload(
    string RequestId,
    string ApprovalFingerprint,
    WebhookApprovalDecision Decision,
    string? ApproverIdentity = null,
    string? ExternalReference = null,
    string? Reason = null);

[JsonConverter(typeof(JsonStringEnumConverter<WebhookApprovalDecision>))]
public enum WebhookApprovalDecision
{
    Approved,
    Rejected
}

/// <summary>
/// Shared v1 HMAC-SHA256 signature helper for request and callback bodies.
/// The signed bytes are UTF-8("{unixTimestamp}.") followed by the exact HTTP body bytes.
/// </summary>
public static class WebhookApprovalSignature
{
    public const string VersionPrefix = "v1=";

    public static string Compute(string secret, long unixTimestamp, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var prefix = Encoding.UTF8.GetBytes($"{unixTimestamp}.");
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefix.Length));
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return VersionPrefix + Convert.ToBase64String(digest);
    }

    public static bool Verify(string secret, long unixTimestamp, ReadOnlySpan<byte> body, string provided)
    {
        if (string.IsNullOrWhiteSpace(secret)
            || string.IsNullOrWhiteSpace(provided)
            || !provided.StartsWith(VersionPrefix, StringComparison.Ordinal))
            return false;

        byte[] supplied;
        try
        {
            supplied = Convert.FromBase64String(provided[VersionPrefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = Convert.FromBase64String(Compute(secret, unixTimestamp, body)[VersionPrefix.Length..]);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }
}
