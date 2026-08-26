using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreProviderCapabilityProfileMatrixTests
{
    [Fact]
    public void Matrix_SqlServerRegexWithoutProfile_RemainsRejected()
    {
        var matrix = SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MsSqlServer);
        var regex = Assert.Single(matrix.Capabilities, item => item.Id == "regex.match");

        Assert.Equal(SqlCapabilityStatus.Rejected, regex.Status);
        Assert.Contains("compatibility level 170", regex.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_SqlServerRegexBelowCompatibility170_RemainsRejected()
    {
        var matrix = SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.MsSqlServer,
            SqlServerProfile(169));
        var regex = Assert.Single(matrix.Capabilities, item => item.Id == "regex.match");

        Assert.Equal(SqlCapabilityStatus.Rejected, regex.Status);
    }

    [Fact]
    public void Matrix_SqlServerRegexAtCompatibility170_IsTranslated()
    {
        var matrix = SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.MsSqlServer,
            SqlServerProfile(170));
        var regex = Assert.Single(matrix.Capabilities, item => item.Id == "regex.match");
        var profile = Assert.Single(matrix.Capabilities, item => item.Id == "provider.target_profile");

        Assert.Equal(SqlCapabilityStatus.Translated, regex.Status);
        Assert.Contains("enabled", regex.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("170", regex.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SqlCapabilityStatus.Supported, profile.Status);
        Assert.Contains("session modes", profile.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_ProfileProviderMismatch_IsRejected()
    {
        var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL);

        var error = Assert.Throws<ArgumentException>(() =>
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MsSqlServer, profile));

        Assert.Contains("declares provider MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("matrix provider is MsSqlServer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlProviderCapabilityProfile SqlServerProfile(int compatibilityLevel) =>
        new(
            SqlAgentToolType.MsSqlServer,
            ServerVersion: new Version(17, 0),
            CompatibilityLevel: compatibilityLevel);
}
