using System.Text.Json.Nodes;
using HsSqlAgent.Server.Services;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class RedisDmlApprovalChallengePayloadCodecTests
{
    private static DmlApprovalChallenge Example()
    {
        var issued = DateTimeOffset.UtcNow;
        return new DmlApprovalChallenge(
            "plan", "rows", 2, "policy", "approval",
            issued, issued.AddMinutes(3), "nonce-1")
        {
            ApprovalPlanBinding = new string('F', 64)
        };
    }

    [Fact]
    public void V3Challenge_RoundTripsExactApprovedRecordInVersionedEnvelope()
    {
        var challenge = Example();
        var json = RedisDmlApprovalChallengePayloadCodec.Serialize(challenge);
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(RedisDmlApprovalChallengePayloadCodec.CurrentSchemaVersion,
            (int)root["SchemaVersion"]!);
        Assert.Equal(RedisDmlApprovalChallengePayloadCodec.PayloadKind,
            (string?)root["Kind"]);
        Assert.Equal(9, root["Challenge"]!.AsObject().Count);
        Assert.Equal(challenge, RedisDmlApprovalChallengePayloadCodec.Deserialize(json));
    }

    [Fact]
    public void CanonicalV1Comparison_DoesNotDependOnReferenceIdentity()
    {
        var challenge = Example();
        var decoded = RedisDmlApprovalChallengePayloadCodec.Deserialize(
            RedisDmlApprovalChallengePayloadCodec.Serialize(challenge));
        Assert.True(RedisDmlApprovalChallengePayloadCodec.MatchesV1(challenge, decoded));
        Assert.False(RedisDmlApprovalChallengePayloadCodec.MatchesV1(
            challenge, decoded with { AffectedRows = decoded.AffectedRows + 1 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(999)]
    public void UnsupportedChallengeVersion_IsNeverAccepted(int version)
    {
        var root = JsonNode.Parse(
            RedisDmlApprovalChallengePayloadCodec.Serialize(Example()))!.AsObject();
        root["SchemaVersion"] = version;
        Assert.Throws<InvalidOperationException>(() =>
            RedisDmlApprovalChallengePayloadCodec.Deserialize(root.ToJsonString()));
    }

    [Fact]
    public void MissingPlanBindingAndUnversionedRecord_CannotSilentlyAuthorize()
    {
        var json = RedisDmlApprovalChallengePayloadCodec.Serialize(Example());
        var modified = JsonNode.Parse(json)!.AsObject();
        modified["Challenge"]!.AsObject().Remove("ApprovalPlanBinding");
        Assert.Throws<InvalidOperationException>(() =>
            RedisDmlApprovalChallengePayloadCodec.Deserialize(modified.ToJsonString()));
        var flatRecord = System.Text.Json.JsonSerializer.Serialize(Example());
        Assert.Throws<InvalidOperationException>(() =>
            RedisDmlApprovalChallengePayloadCodec.Deserialize(flatRecord));
        Assert.Throws<InvalidOperationException>(() =>
            RedisDmlApprovalChallengePayloadCodec.Deserialize("{}"));
    }
}
