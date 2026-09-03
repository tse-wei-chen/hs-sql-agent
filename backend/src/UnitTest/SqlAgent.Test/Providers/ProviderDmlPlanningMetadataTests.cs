using Microsoft.Data.Sqlite;
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
