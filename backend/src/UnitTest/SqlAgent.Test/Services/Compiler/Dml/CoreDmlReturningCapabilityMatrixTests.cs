using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlReturningCapabilityMatrixTests
{
    [Fact]
    public void Matrix_PostgresDeclaresPortableReturning()
    {
        var capability = Returning(SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Postgres));

        Assert.Equal(SqlCapabilityStatus.Translated, capability.Status);
        Assert.Contains("returned-row count", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_SqliteReturningRequiresExplicitVersion335()
    {
        var absent = Returning(SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Sqlite));
        var old = Returning(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Sqlite,
            new SqlProviderCapabilityProfile(
                SqlAgentToolType.Sqlite,
                ServerVersion: new Version(3, 34))));
        var current = Returning(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Sqlite,
            new SqlProviderCapabilityProfile(
                SqlAgentToolType.Sqlite,
                ServerVersion: new Version(3, 35))));

        Assert.Equal(SqlCapabilityStatus.Rejected, absent.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, old.Status);
        Assert.Equal(SqlCapabilityStatus.Translated, current.Status);
        Assert.Contains("3.35", absent.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explicit", current.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_SqliteRichReturningRequiresExplicitVersion335()
    {
        var absent = RichReturning(SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Sqlite));
        var old = RichReturning(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Sqlite,
            new SqlProviderCapabilityProfile(
                SqlAgentToolType.Sqlite,
                ServerVersion: new Version(3, 34))));
        var current = RichReturning(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Sqlite,
            new SqlProviderCapabilityProfile(
                SqlAgentToolType.Sqlite,
                ServerVersion: new Version(3, 35))));

        Assert.Equal(SqlCapabilityStatus.Rejected, absent.Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, old.Status);
        Assert.Equal(SqlCapabilityStatus.Translated, current.Status);
        Assert.Contains("target table", current.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same-provider", current.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_FirebirdRichReturningRequiresExplicitVersionFive()
    {
        var old = RichReturning(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Firebird,
            new SqlProviderCapabilityProfile(
                SqlAgentToolType.Firebird,
                ServerVersion: new Version(4, 0))));
        var current = RichReturning(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Firebird,
            new SqlProviderCapabilityProfile(
                SqlAgentToolType.Firebird,
                ServerVersion: new Version(5, 0))));

        Assert.Equal(SqlCapabilityStatus.Rejected, old.Status);
        Assert.Equal(SqlCapabilityStatus.Translated, current.Status);
        Assert.Contains("5.0", current.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OLD/NEW", current.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_FirebirdReturningRequiresExplicitVersionFive()
    {
        var old = Returning(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Firebird,
            new SqlProviderCapabilityProfile(
                SqlAgentToolType.Firebird,
                ServerVersion: new Version(4, 0))));
        var current = Returning(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Firebird,
            new SqlProviderCapabilityProfile(
                SqlAgentToolType.Firebird,
                ServerVersion: new Version(5, 0))));

        Assert.Equal(SqlCapabilityStatus.Rejected, old.Status);
        Assert.Equal(SqlCapabilityStatus.Translated, current.Status);
        Assert.Contains("5.0", old.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("multi-row", current.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "MySQL")]
    [InlineData(SqlAgentToolType.Oracle, "RETURNING INTO")]
    [InlineData(SqlAgentToolType.MsSqlServer, "trigger")]
    public void Matrix_UnsupportedReturningTargetsRemainFailClosed(
        SqlAgentToolType provider,
        string expectedDetail)
    {
        var capability = Returning(SqlCapabilityMatrix.ForProvider(provider));

        Assert.Equal(SqlCapabilityStatus.Rejected, capability.Status);
        Assert.Contains(expectedDetail, capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_DocumentsSourceVersionGates()
    {
        var sourceProfile = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Postgres).Capabilities,
            item => item.Id == "provider.source_profile");

        Assert.Contains("SQLite RETURNING", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.35", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Firebird", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5.0", sourceProfile.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlCapability Returning(ProviderSqlCapabilities matrix) =>
        Assert.Single(matrix.Capabilities, item => item.Id == "dml.returning_output");

    private static SqlCapability RichReturning(ProviderSqlCapabilities matrix) =>
        Assert.Single(matrix.Capabilities, item => item.Id == "dml.returning.expression");
}
