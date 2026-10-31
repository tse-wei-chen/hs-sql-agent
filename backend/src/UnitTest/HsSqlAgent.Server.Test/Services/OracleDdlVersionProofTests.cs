using HsSqlAgent.Server.Services;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>Oracle 26ai runs a 23.26+ database kernel, not version 26.0.</summary>
public sealed class OracleDdlVersionProofTests
{
    [Theory]
    [InlineData("23.26.0.0.0")]
    [InlineData("23.26.3.0.0")]
    [InlineData("23.27.1.0.0")]
    public void AcceptVerifiedOracle26AiKernel(string version) =>
        Assert.True(OracleDdlRuntime.IsProvenOracle26Ai(version));

    [Theory]
    [InlineData("19.26.0.0.0")]
    [InlineData("21.26.0.0.0")]
    [InlineData("23.25.0.0.0")]
    [InlineData("26.0.0.0.0")]
    [InlineData("23.26.0.0.0-beta")]
    [InlineData("23.26.0.0")]
    [InlineData("123.26.0.0.0")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectUnprovenOrAmbiguousKernelVersions(string? version) =>
        Assert.False(OracleDdlRuntime.IsProvenOracle26Ai(version));
}
