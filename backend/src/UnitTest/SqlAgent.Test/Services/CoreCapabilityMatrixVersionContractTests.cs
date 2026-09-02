using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCapabilityMatrixVersionContractTests
{
    [Fact]
    public void MatrixVersion_IsCurrentCapabilityContract()
    {
        Assert.Equal("2026-09-02.79", SqlCapabilityMatrix.Version);
    }

    [Fact]
    public void PostgresJsonArrowCapability_RejectsExplicitPre93Profile()
    {
        var undeclared = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Postgres).Capabilities,
            item => item.Id == "json.operator.postgres_arrow");
        var oldVersion = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Postgres,
                new SqlProviderCapabilityProfile(SqlAgentToolType.Postgres, new Version(9, 2))).Capabilities,
            item => item.Id == "json.operator.postgres_arrow");
        var supported = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.Postgres,
                new SqlProviderCapabilityProfile(SqlAgentToolType.Postgres, new Version(9, 3))).Capabilities,
            item => item.Id == "json.operator.postgres_arrow");

        Assert.Equal(SqlCapabilityStatus.Supported, undeclared.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, oldVersion.Status);
        Assert.Equal(SqlCapabilityStatus.Supported, supported.Status);
    }

    [Fact]
    public void MySqlJsonArrowCapability_IsVersionProven()
    {
        var undeclared = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.MySQL).Capabilities,
            item => item.Id == "json.operator.mysql_arrow");
        var oldVersion = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.MySQL,
                new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(5, 7, 8))).Capabilities,
            item => item.Id == "json.operator.mysql_arrow");
        var supported = Assert.Single(
            SqlCapabilityMatrix.ForProvider(
                SqlAgentToolType.MySQL,
                new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(5, 7, 9))).Capabilities,
            item => item.Id == "json.operator.mysql_arrow");

        Assert.Equal(SqlCapabilityStatus.Rejected, undeclared.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, oldVersion.Status);
        Assert.Equal(SqlCapabilityStatus.Supported, supported.Status);
    }

    [Fact]
    public void NativeFunctionIdentifierCapabilities_ReflectProviderGrammar()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        {
            var matrix = SqlCapabilityMatrix.ForProvider(provider);
            var quoted = Assert.Single(
                matrix.Capabilities,
                capability => capability.Id == "function.quoted_identifier");
            var qualified = Assert.Single(
                matrix.Capabilities,
                capability => capability.Id == "function.qualified");

            Assert.Equal(SqlCapabilityStatus.Supported, quoted.Status);
            Assert.Equal(
                provider == SqlAgentToolType.Sqlite
                    ? SqlCapabilityStatus.Rejected
                    : SqlCapabilityStatus.Supported,
                qualified.Status);
        }
    }

    [Fact]
    public void MatrixDetails_DoNotReferenceRemovedSqlKataBackend()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        {
            var matrix = SqlCapabilityMatrix.ForProvider(provider);
            foreach (var capability in matrix.Capabilities)
            {
                Assert.DoesNotContain(
                    "SqlKata",
                    capability.Detail,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
