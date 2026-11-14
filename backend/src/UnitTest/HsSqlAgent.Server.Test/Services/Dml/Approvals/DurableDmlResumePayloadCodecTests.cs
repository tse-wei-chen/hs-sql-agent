using System.Text.Json;
using System.Text.Json.Nodes;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class DurableDmlResumePayloadCodecTests
{
    private static DurableDmlResumePayload Example() =>
        new("INSERT INTO items (id) VALUES (1)", "execute_dml_sql",
            7, 11, SqlAgentToolType.Sqlite, null, null,
            new string('A', 64), "target-fence-v1", true);

    [Fact]
    public void NewPayload_WritesVersionedEnvelope_AndRoundTripsSafetyRequirements()
    {
        var value = Example();
        var encoded = DurableDmlResumePayloadCodec.Serialize(value);
        using var document = JsonDocument.Parse(encoded);

        Assert.Equal(DurableDmlResumePayloadCodec.PayloadKind,
            document.RootElement.GetProperty("Kind").GetString());
        Assert.Equal(DurableDmlResumePayloadCodec.CurrentSchemaVersion,
            document.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("Payload")
            .GetProperty("RequiresCommitWitness").GetBoolean());
        // Stable v1 projection: later CLR properties cannot leak into v1 bytes.
        Assert.Equal(10, document.RootElement.GetProperty("Payload")
            .EnumerateObject().Count());
        Assert.Equal(value, DurableDmlResumePayloadCodec.Deserialize(encoded));
    }

    [Fact]
    public void LegacyV2FlatPayload_UpcastsWithoutInventingFenceOrWitnessConsent()
    {
        var value = Example() with { FenceProtocol = null, RequiresCommitWitness = false };
        var flatJson = JsonSerializer.Serialize(value);
        var migrated = DurableDmlResumePayloadCodec.Deserialize(flatJson);
        Assert.Null(migrated.FenceProtocol);
        Assert.False(migrated.RequiresCommitWitness);
        Assert.Equal(value, migrated);

        // Historical v2 JSON can lack the newly added security fields.
        var legacy = JsonNode.Parse(flatJson)!.AsObject();
        legacy.Remove("FenceProtocol");
        legacy.Remove("RequiresCommitWitness");
        var old = DurableDmlResumePayloadCodec.Deserialize(legacy.ToJsonString());
        Assert.False(old.RequiresCommitWitness);
        Assert.Null(old.FenceProtocol);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(999)]
    public void UnsupportedSchemaVersion_FailsClosed(int schemaVersion)
    {
        var changed = JsonNode.Parse(
            DurableDmlResumePayloadCodec.Serialize(Example()))!.AsObject();
        changed["SchemaVersion"] = schemaVersion;
        Assert.Throws<InvalidOperationException>(() =>
            DurableDmlResumePayloadCodec.Deserialize(changed.ToJsonString()));
    }

    [Fact]
    public void UnknownOrDamagedEnvelope_CannotBeMistakenForLegacy()
    {
        var encoded = DurableDmlResumePayloadCodec.Serialize(Example());
        var damaged = JsonNode.Parse(encoded)!.AsObject();
        damaged.Remove("SchemaVersion");
        Assert.Throws<InvalidOperationException>(() =>
            DurableDmlResumePayloadCodec.Deserialize(damaged.ToJsonString()));

        var wrongKind = JsonNode.Parse(encoded)!.AsObject();
        wrongKind["Kind"] = "different-type";
        Assert.Throws<InvalidOperationException>(() =>
            DurableDmlResumePayloadCodec.Deserialize(wrongKind.ToJsonString()));
    }

    [Theory]
    [InlineData("FenceProtocol")]
    [InlineData("RequiresCommitWitness")]
    public void VersionedPayload_MissingSafetyFieldCannotDefaultToUnfenced(string property)
    {
        var changed = JsonNode.Parse(
            DurableDmlResumePayloadCodec.Serialize(Example()))!.AsObject();
        changed["Payload"]!.AsObject().Remove(property);
        Assert.Throws<InvalidOperationException>(() =>
            DurableDmlResumePayloadCodec.Deserialize(changed.ToJsonString()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"Sql":"SELECT 1"}""")]
    [InlineData("not-json")]
    public void PartialOrMalformedPayloads_CannotBeExecuted(string json)
    {
        Assert.Throws<InvalidOperationException>(
            () => DurableDmlResumePayloadCodec.Deserialize(json));
    }
}
