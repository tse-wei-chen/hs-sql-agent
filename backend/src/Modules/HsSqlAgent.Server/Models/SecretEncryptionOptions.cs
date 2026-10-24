namespace HsSqlAgent.Server.Models;

/// <summary>
/// Optional, explicit encryption-key configuration. Secrets are Base64-encoded
/// 32-byte random values, delivered via a host secret store. Disabled until all
/// persistence readers and writers are enrolled in the rollout.
/// </summary>
public sealed class SecretEncryptionOptions
{
    public bool Enabled { get; set; }
    /// <summary>
    /// Explicit rollout opt-in. Only Durable DML payloads may be written in v2
    /// after all nodes have deployed legacy/v2 dual-read support.
    /// </summary>
    public bool EnableV2DurableDmlWrites { get; set; }
    /// <summary>Only new and explicitly updated Admin DB credentials; Bootstrap remains legacy.</summary>
    public bool EnableV2DatabasePasswordWrites { get; set; }
    /// <summary>
    /// Operator attestation; this is NOT a distributed compatibility probe.
    /// Never set true while an old-only node can read approval state.
    /// </summary>
    public bool AllNodesDualReadConfirmed { get; set; }
    public Dictionary<string, SecretEncryptionPurposeOptions> Purposes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SecretEncryptionPurposeOptions
{
    public string ActiveKeyId { get; set; } = string.Empty;
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}
