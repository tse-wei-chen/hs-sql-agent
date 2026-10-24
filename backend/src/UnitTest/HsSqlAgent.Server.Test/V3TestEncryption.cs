using Microsoft.Extensions.DependencyInjection;
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

    public static Common.Interfaces.ISecretProtector Protector() =>
        new Common.Services.VersionedSecretProtector(
            new Common.Services.SecretKeyRing(
                new Dictionary<Common.Interfaces.SecretPurpose, Common.Services.SecretKeySet>
                {
                    [Common.Interfaces.SecretPurpose.DatabasePassword] = new Common.Services.SecretKeySet
                    {
                        ActiveKeyId = "test_db_k1",
                        Keys = new Dictionary<string,byte[]> {
                            ["test_db_k1"] = Enumerable.Repeat((byte)0x27,32).ToArray()
                        }
                    },
                    [Common.Interfaces.SecretPurpose.DurableDmlPayload] = new Common.Services.SecretKeySet
                    {
                        ActiveKeyId = "test_dml_k1",
                        Keys = new Dictionary<string,byte[]> {
                            ["test_dml_k1"] = Enumerable.Repeat((byte)0x28,32).ToArray()
                        }
                    }
                }));

    public static string? ProtectDb(string? password, int dbId) =>
        Protector().Protect(password, Common.Interfaces.SecretPurpose.DatabasePassword,
            dbId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static void AddTo(Microsoft.Extensions.DependencyInjection.IServiceCollection services) =>
        services.AddSingleton<Common.Interfaces.ISecretProtector>(Protector());

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
