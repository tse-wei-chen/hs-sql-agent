using System.Text.Json;
using HsSqlAgent.SqlCore.Enums;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Immutable persisted execution intent. Changes to its authorization or safety
/// semantics require a new envelope schema version and a deliberate upcaster.
/// Do not add a defaulted security flag and rely on legacy JSON omission.
/// </summary>
internal sealed record DurableDmlResumePayload(
    string Sql,
    string RequiredToolName,
    int AccessKeyId,
    int DbManagementId,
    SqlAgentToolType SourceDialect,
    int? CustomToolId,
    int? CustomToolRevisionId,
    string ConnectionFingerprint,
    string? FenceProtocol = null,
    bool RequiresCommitWitness = false);

/// <summary>
/// Versioned encrypted Admin Store payload. Schema version is independent of
/// NuGet SemVer; newer readers can upcast older envelopes in a controlled way.
/// The existing protected DB column is sufficient; no schema migration needed.
/// </summary>
internal static class DurableDmlResumePayloadCodec
{
    internal const int CurrentSchemaVersion = 1;
    internal const string PayloadKind = "hs-sql-agent.dml-resume";

    internal static string Serialize(DurableDmlResumePayload payload)
    {
        Validate(payload);
        return JsonSerializer.Serialize(new ResumeEnvelope(
            PayloadKind, CurrentSchemaVersion, payload));
    }

    internal static DurableDmlResumePayload Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw InvalidPayload();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidPayload();

            // Version 0 was an unwrapped JSON record. Read it only through
            // this explicitly named migration path; all v3 writes are v1.
            if (!root.TryGetProperty("SchemaVersion", out var version))
            {
                // Never mistake a damaged/unversioned envelope for legacy.
                if (root.TryGetProperty("Kind", out _)
                    || root.TryGetProperty("Payload", out _))
                    throw InvalidPayload();
                return DeserializePayload(root, isLegacy: true);
            }

            if (version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schemaVersion)
                || schemaVersion != CurrentSchemaVersion)
                throw new InvalidOperationException(
                    "Unsupported durable DML approval payload schema version. " +
                    "Upgrade the worker or reissue a new approval; do not execute a mismatched payload.");

            if (!root.TryGetProperty("Kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != PayloadKind
                || !root.TryGetProperty("Payload", out var content)
                || content.ValueKind != JsonValueKind.Object)
                throw InvalidPayload();

            return DeserializePayload(content, isLegacy: false);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Durable DML approval payload JSON is invalid.", ex);
        }
    }

    private static DurableDmlResumePayload DeserializePayload(JsonElement content, bool isLegacy)
    {
        // Reject missing critical security fields instead of accepting a
        // default-valued record and proceeding to target SQL.
        var requiredFields = new[] {
            "Sql", "RequiredToolName", "AccessKeyId", "DbManagementId",
            "SourceDialect", "ConnectionFingerprint"
        };
        foreach (var field in requiredFields)
            if (!content.TryGetProperty(field, out var value)
                || value.ValueKind == JsonValueKind.Null)
                throw InvalidPayload();

        if (!isLegacy
            && (!content.TryGetProperty("FenceProtocol", out _)
                || !content.TryGetProperty("RequiresCommitWitness", out _)))
            throw InvalidPayload();

        var payload = content.Deserialize<DurableDmlResumePayload>()
                      ?? throw InvalidPayload();
        Validate(payload);
        if (!isLegacy && payload.FenceProtocol is not null && !payload.RequiresCommitWitness)
            throw InvalidPayload();
        return payload;
    }

    private static void Validate(DurableDmlResumePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(payload.Sql)
            || string.IsNullOrWhiteSpace(payload.RequiredToolName)
            || payload.AccessKeyId <= 0 || payload.DbManagementId <= 0
            || !Enum.IsDefined(payload.SourceDialect)
            || payload.ConnectionFingerprint is not { Length: 64 }
            || !payload.ConnectionFingerprint.All(Uri.IsHexDigit))
            throw InvalidPayload();
    }

    private static InvalidOperationException InvalidPayload() =>
        new("Durable DML approval payload is incomplete or invalid; it cannot be resumed.");

    private sealed record ResumeEnvelope(
        string Kind, int SchemaVersion, DurableDmlResumePayload Payload);
}
