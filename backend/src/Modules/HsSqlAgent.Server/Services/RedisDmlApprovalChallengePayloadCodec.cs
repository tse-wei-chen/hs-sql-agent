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
            PayloadKind, CurrentSchemaVersion, ChallengeV1.FromDomain(challenge)));
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

            var stored = raw.Deserialize<ChallengeV1>() ?? throw InvalidPayload();
            var challenge = stored.ToDomain();
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
        string Kind, int SchemaVersion, ChallengeV1 Challenge);

    // Frozen v1 wire DTO. Changing DmlApprovalChallenge's CLR shape in v3.x
    // must not change Redis Lua's canonical equality/CAS byte representation.
    private sealed record ChallengeV1(
        string PlanFingerprint,
        string? RowSetFingerprint,
        int AffectedRows,
        string PolicyVersion,
        string ApprovalContextFingerprint,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        string Nonce,
        string? ApprovalPlanBinding)
    {
        internal static ChallengeV1 FromDomain(DmlApprovalChallenge c) =>
            new(c.PlanFingerprint, c.RowSetFingerprint, c.AffectedRows,
                c.PolicyVersion, c.ApprovalContextFingerprint, c.IssuedAt,
                c.ExpiresAt, c.Nonce, c.ApprovalPlanBinding);

        internal DmlApprovalChallenge ToDomain() =>
            new(PlanFingerprint, RowSetFingerprint, AffectedRows, PolicyVersion,
                ApprovalContextFingerprint, IssuedAt, ExpiresAt, Nonce)
            {
                ApprovalPlanBinding = ApprovalPlanBinding
            };
    }
}
