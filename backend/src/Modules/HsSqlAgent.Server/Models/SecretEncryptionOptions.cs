namespace HsSqlAgent.Server.Models;

/// <summary>
/// v3 encryption is mandatory for every host. Each persisted-secret purpose
/// requires a distinct 32-byte key. Old key IDs remain read-only for v3 rotation.
/// There is deliberately no off switch, v2 product-data reader, or rollout gate.
/// </summary>
public sealed class SecretEncryptionOptions
{
    public Dictionary<string, SecretEncryptionPurposeOptions> Purposes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SecretEncryptionPurposeOptions
{
    public string ActiveKeyId { get; set; } = string.Empty;
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}
