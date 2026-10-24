namespace Common.Services;
internal static class TestSecrets
{
    public static SecretProtectionSettings Create() => new()
    {
        DatabasePasswords = new() { ActiveKeyId = "db-1", Keys = new() { ["db-1"] = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()) } },
        DmlPayloads = new() { ActiveKeyId = "dml-1", Keys = new() { ["dml-1"] = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray()) } },
        DmlFingerprintKey = Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray())
    };
}
