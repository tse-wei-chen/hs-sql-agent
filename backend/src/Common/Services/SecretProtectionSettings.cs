namespace Common.Services;
public sealed class SecretProtectionSettings
{
    public SecretKeyRing DatabasePasswords { get; set; } = new();
    public SecretKeyRing DmlPayloads { get; set; } = new();
    public string DmlFingerprintKey { get; set; } = string.Empty;
}
public sealed class SecretKeyRing
{
    public string ActiveKeyId { get; set; } = string.Empty;
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}
