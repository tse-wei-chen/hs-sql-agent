using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreAggregateFilterCapabilityMatrixTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Rejected)]
    public void Matrix_FilterDefaultContract_IsFailClosedUnlessRuntimeIsProven(
        SqlAgentToolType provider,
        SqlCapabilityStatus expected)
    {
        var filter = Filter(SqlCapabilityMatrix.ForProvider(provider));

        Assert.Equal(expected, filter.Status);
    }

    [Theory]
    [InlineData(9, 3, SqlCapabilityStatus.Rejected)]
    [InlineData(9, 4, SqlCapabilityStatus.Supported)]
    [InlineData(16, 0, SqlCapabilityStatus.Supported)]
    public void Matrix_PostgresFilter_TracksNineFourBoundary(
        int major,
        int minor,
        SqlCapabilityStatus expected)
    {
        var filter = Filter(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Postgres,
            Profile(SqlAgentToolType.Postgres, major, minor)));

        Assert.Equal(expected, filter.Status);
        Assert.Contains("9.4", filter.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(3, 29, SqlCapabilityStatus.Rejected)]
    [InlineData(3, 30, SqlCapabilityStatus.Supported)]
    [InlineData(3, 50, SqlCapabilityStatus.Supported)]
    public void Matrix_SqliteFilter_TracksThreeThirtyBoundary(
        int major,
        int minor,
        SqlCapabilityStatus expected)
    {
        var filter = Filter(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Sqlite,
            Profile(SqlAgentToolType.Sqlite, major, minor)));

        Assert.Equal(expected, filter.Status);
        Assert.Contains("3.30", filter.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(3, 0, SqlCapabilityStatus.Rejected)]
    [InlineData(4, 0, SqlCapabilityStatus.Supported)]
    [InlineData(5, 0, SqlCapabilityStatus.Supported)]
    public void Matrix_FirebirdFilter_TracksFourZeroBoundary(
        int major,
        int minor,
        SqlCapabilityStatus expected)
    {
        var filter = Filter(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Firebird,
            Profile(SqlAgentToolType.Firebird, major, minor)));

        Assert.Equal(expected, filter.Status);
        Assert.Contains("4.0", filter.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_Oracle26Filter_RemainsRejectedUntilPredicateScopeIsModeled()
    {
        var filter = Filter(SqlCapabilityMatrix.ForProvider(
            SqlAgentToolType.Oracle,
            Profile(SqlAgentToolType.Oracle, 26, 0)));

        Assert.Equal(SqlCapabilityStatus.Rejected, filter.Status);
        Assert.Contains("26ai", filter.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outer references", filter.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_SqliteFilterWithoutProfile_DocumentsExplicitVersionRequirement()
    {
        var filter = Filter(SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Sqlite));

        Assert.Equal(SqlCapabilityStatus.Rejected, filter.Status);
        Assert.Contains("explicitly declares", filter.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.30", filter.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlCapability Filter(ProviderSqlCapabilities matrix) =>
        Assert.Single(matrix.Capabilities, item => item.Id == "expression.filter");

    private static SqlProviderCapabilityProfile Profile(
        SqlAgentToolType provider,
        int major,
        int minor) =>
        new(provider, new Version(major, minor));
}
