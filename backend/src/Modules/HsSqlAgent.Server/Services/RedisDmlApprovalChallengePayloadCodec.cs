using System.Text.Json;
using SqlAgent.Service.Core.Execution;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Stable versioned Redis wire contract for one-time DML approval challenges.
/// All Lua CAS participants must encode the same schema, independent of CLR
/// record additions. Security-affecting schema changes need a new version.
/// </summary>
internal static class RedisDmlApprovalChallengePayloadCodec
{
    internal const int CurrentSchemaVersion = 1;
    internal const string PayloadKind = "hs-sql-agent.dml-challenge";

    internal static string Serialize(DmlApprovalChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (string.IsNullOrWhiteSpace(challenge.Nonce))
            throw new InvalidOperationException("DML approval challenge nonce is missing.");
        return JsonSerializer.Serialize(new ChallengeEnvelope(
            PayloadKind, CurrentSchemaVersion, challenge));
    }

    internal static DmlApprovalChallenge Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw InvalidPayload();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != PayloadKind
                || !root.TryGetProperty("SchemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schemaVersion)
                || schemaVersion != CurrentSchemaVersion
                || !root.TryGetProperty("Challenge", out var raw)
                || raw.ValueKind != JsonValueKind.Object)
                throw InvalidPayload();

            foreach (var name in new[] {
                "PlanFingerprint", "RowSetFingerprint", "AffectedRows",
                "PolicyVersion", "ApprovalContextFingerprint", "IssuedAt",
                "ExpiresAt", "Nonce", "ApprovalPlanBinding"
            })
                if (!raw.TryGetProperty(name, out _))
                    throw InvalidPayload();

            var challenge = raw.Deserialize<DmlApprovalChallenge>()
                ?? throw InvalidPayload();
            if (string.IsNullOrWhiteSpace(challenge.Nonce))
                throw InvalidPayload();
            return challenge;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Stored DML approval challenge payload JSON is invalid.", ex);
        }
    }

    private static InvalidOperationException InvalidPayload() =>
        new("Stored DML approval challenge payload is missing, incompatible or invalid.");

    private sealed record ChallengeEnvelope(
        string Kind, int SchemaVersion, DmlApprovalChallenge Challenge);
}
