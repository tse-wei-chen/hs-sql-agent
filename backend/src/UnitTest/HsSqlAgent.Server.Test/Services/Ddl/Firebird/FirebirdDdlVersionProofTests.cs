using HsSqlAgent.Server.Services;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>Fail closed for patch/build numbers which contain a false 5.x match.</summary>
public sealed class FirebirdDdlVersionProofTests
{
    [Theory]
    [InlineData("5.0")]
    [InlineData("5.0.4")]
    [InlineData("5.0.4.1234")]
    public void AcceptActualFirebirdFive(string version) =>
        Assert.True(FirebirdDdlRuntime.IsProvenFirebird5Version(version));

    [Theory]
    [InlineData("3.0.5.0")]
    [InlineData("4.0.5.0")]
    [InlineData("6.0")]
    [InlineData("5")]
    [InlineData("5.0-beta")]
    [InlineData("LI-V5.0.4.0 Firebird 5.0")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectOtherOrUnprovenVersions(string? version) =>
        Assert.False(FirebirdDdlRuntime.IsProvenFirebird5Version(version));
}
