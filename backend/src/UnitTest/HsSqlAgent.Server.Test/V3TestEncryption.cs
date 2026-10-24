using HsSqlAgent.Server.Models;

namespace HsSqlAgent.Server.Test;

/// <summary>
/// Explicit in-memory keys for v3 unit tests. Never used in production code,
/// emitted into deployment examples, or shared with external services.
/// </summary>
internal static class V3TestEncryption
{
    public static HsSqlAgentServiceOptions Prepare(HsSqlAgentServiceOptions options)
    {
        Install(options.SecretEncryption);
        return options;
    }

    public static void Configure(HsSqlAgentRuntimeOptions options) =>
        Install(options.SecretEncryption);

    private static void Install(SecretEncryptionOptions settings)
    {
        settings.Purposes["DatabasePassword"] = new SecretEncryptionPurposeOptions
        {
            ActiveKeyId = "test_db_k1",
            Keys = new Dictionary<string,string> {
                ["test_db_k1"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x27, 32).ToArray())
            }
        };
        settings.Purposes["DurableDmlPayload"] = new SecretEncryptionPurposeOptions
        {
            ActiveKeyId = "test_dml_k1",
            Keys = new Dictionary<string,string> {
                ["test_dml_k1"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x28, 32).ToArray())
            }
        };
    }
}
