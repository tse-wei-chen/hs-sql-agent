using Microsoft.Data.Sqlite;
using HsSqlAgent.Provider.Abstractions;
using Xunit;

namespace SqlAgent.Test.Providers;

public sealed class ProviderDmlPlanningMetadataTests
{
    [Fact]
    public async Task SqliteSnapshot_ResolvesTargetAndPrimaryKeyColumnsInOneProviderCall()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE users (
                    id INTEGER PRIMARY KEY,
                    status TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var provider = new SqliteProvider();
        var reader = Assert.IsAssignableFrom<IProviderConnectionDmlPlanningMetadataReader>(provider);

        var snapshots = await reader.GetDmlPlanningMetadataAsync(
            connection,
            schema: null,
            table: "users",
            includeColumns: true,
            cancellationToken: TestContext.Current.CancellationToken);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("main", snapshot.Schema);
        Assert.Equal("users", snapshot.Table);
        Assert.Null(snapshot.HasEnabledDmlTrigger);
        Assert.Collection(
            snapshot.Columns,
            id =>
            {
                Assert.Equal("id", id.Name);
                Assert.True(id.IsPrimaryKey);
                Assert.Equal(1, id.PrimaryKeyOrdinal);
            },
            status =>
            {
                Assert.Equal("status", status.Name);
                Assert.False(status.IsPrimaryKey);
                Assert.Null(status.PrimaryKeyOrdinal);
            });
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void AllProviders_ExposeTransactionEnlistedDmlMetadata(SqlAgentToolType provider)
    {
        object instance = provider switch
        {
            SqlAgentToolType.Postgres => new HsSqlAgent.Provider.PostgreSql.PostgresProvider(),
            SqlAgentToolType.MySQL => new HsSqlAgent.Provider.MySql.MySqlProvider(),
            SqlAgentToolType.Sqlite => new HsSqlAgent.Provider.Sqlite.SqliteProvider(),
            SqlAgentToolType.MsSqlServer => new HsSqlAgent.Provider.SqlServer.MsSqlServerProvider(),
            SqlAgentToolType.Oracle => new HsSqlAgent.Provider.Oracle.OracleProvider(),
            SqlAgentToolType.Firebird => new HsSqlAgent.Provider.Firebird.FirebirdProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        Assert.IsAssignableFrom<IProviderConnectionDmlPlanningMetadataReader>(instance);
        Assert.IsAssignableFrom<IProviderTransactionDmlPlanningMetadataReader>(instance);
    }

    [Fact]
    public async Task SqliteTransactionSnapshot_UsesCallerTransactionAndRejectsDifferentConnection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var reader = (IProviderTransactionDmlPlanningMetadataReader)new SqliteProvider();
        var result = await reader.GetDmlPlanningMetadataInTransactionAsync(
            connection, transaction, null, "users", includeColumns: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, Assert.Single(result).Columns.Count);

        await using var other = new SqliteConnection("Data Source=:memory:");
        await other.OpenAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.GetDmlPlanningMetadataInTransactionAsync(
                other, transaction, null, "users", includeColumns: true,
                cancellationToken: TestContext.Current.CancellationToken));
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SqliteSnapshot_WithoutColumnsStillResolvesPhysicalTarget()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var reader = (IProviderConnectionDmlPlanningMetadataReader)new SqliteProvider();

        var snapshots = await reader.GetDmlPlanningMetadataAsync(
            connection,
            schema: null,
            table: "users",
            includeColumns: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("main", snapshot.Schema);
        Assert.Equal("users", snapshot.Table);
        Assert.Empty(snapshot.Columns);
    }
}
