using HsSqlAgent.Server.Services;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>Never misidentify MariaDB/Percona or a different major/minor as MySQL 8.4.</summary>
public sealed class MySqlDdlVersionProofTests
{
    [Theory]
    [InlineData("8.4.0", "MySQL Community Server - GPL")]
    [InlineData("8.4.7", "MySQL Community Server - GPL")]
    public void AcceptOnlyMySql84(string version, string comment) =>
        Assert.True(MySqlDdlRuntime.IsProvenMySql84(version, comment));

    [Theory]
    [InlineData("8.0.43", "MySQL Community Server - GPL")]
    [InlineData("5.7.44", "MySQL Community Server - GPL")]
    [InlineData("9.4.0", "MySQL Community Server - GPL")]
    [InlineData("8.4.0-MariaDB", "MariaDB Server")]
    [InlineData("8.4.0", "Percona Server")]
    [InlineData("4.8.4", "MySQL Community Server - GPL")]
    [InlineData("", "MySQL Community Server - GPL")]
    [InlineData(null, "MySQL Community Server - GPL")]
    [InlineData("8.4.0", null)]
    public void RejectUnprovenServerIdentity(string? version, string? comment) =>
        Assert.False(MySqlDdlRuntime.IsProvenMySql84(version, comment));
}
