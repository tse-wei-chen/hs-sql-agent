using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Actual PostgreSQL SQL + native schema + strict approval; not a mocked compiler.</summary>
public sealed class DmlPostgresInsertSelectApprovalTests
{
    private const string Approval = "postgres-insert-select-source-row-approval-v1";

    [Fact]
    public async Task DifferentPhysicalTables_PreviewApprovesSourceAndCommitReturnsActualNewRow()
    {
        await WithTablesAsync(async (connectionString, schema, version) =>
        {
            var provider = new PostgresProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.source_users WHERE id = 1 RETURNING id, name";
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, schema, sql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Equal(2, plan.SchemaSnapshot!.Tables.Count);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Carol", preview.Rows.Single()["name"]);
            Assert.DoesNotContain(preview.Rows.Single().Keys, column => column.Equals("id", StringComparison.OrdinalIgnoreCase));
            var committed = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            var row = Assert.Single(committed.ReturnedRows);
            Assert.Equal(3, Convert.ToInt32(row["id"]));
            Assert.Equal("Carol", row["name"]);
            Assert.Equal(3, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {schema}.users"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceValueOrNativeIndexDrift_AfterApprovalDeniesCommit(bool changeIndex)
    {
        await WithTablesAsync(async (connectionString, schema, version) =>
        {
            var provider = new PostgresProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, schema,
                $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.source_users WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            if (changeIndex)
            {
                await ExecuteAsync(connectionString,
                    $"CREATE INDEX idx_m09_source_name ON {schema}.source_users (name)");
                await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                    connectionString, plan, preview.Challenge, Approval,
                    TestContext.Current.CancellationToken, version));
            }
            else
            {
                await ExecuteAsync(connectionString,
                    $"UPDATE {schema}.source_users SET name = 'Mallory' WHERE id = 1");
                var rejected = await coordinator.CommitAsync(
                    connectionString, plan, preview.Challenge, Approval,
                    TestContext.Current.CancellationToken, version);
                Assert.False(rejected.Committed);
                Assert.Empty(rejected.ReturnedRows);
            }
            Assert.Equal(2, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {schema}.users"));
        });
    }

    [Fact]
    public async Task ThreeSourceRows_ExceedsPolicyBeforeApproval()
    {
        await WithTablesAsync(async (connectionString, schema, version) =>
        {
            await ExecuteAsync(connectionString,
                $"INSERT INTO {schema}.source_users(id,name) VALUES (2,'Carol'),(3,'Carol')");
            var provider = new PostgresProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, schema,
                $"INSERT INTO {schema}.users (name) SELECT name FROM {schema}.source_users WHERE name = 'Carol'");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {schema}.users"));
        });
    }

    [Theory]
    [InlineData("SELECT name FROM {0}.source_users")]
    [InlineData("SELECT upper(name) FROM {0}.source_users WHERE id = 1")]
    [InlineData("SELECT s.name FROM {0}.source_users AS s WHERE id = 1")]
    [InlineData("SELECT name FROM {0}.source_users WHERE id = 1 OR id = 2")]
    public async Task UnsupportedSourceShape_StaysFailClosed(string sourceTemplate)
    {
        await WithTablesAsync(async (connectionString, schema, _) =>
        {
            var provider = new PostgresProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            await Assert.ThrowsAnyAsync<Exception>(() => CreatePlanAsync(
                provider, snapshot, connectionString, schema,
                $"INSERT INTO {schema}.users (name) " + string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, sourceTemplate, schema)));
            Assert.Equal(2, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {schema}.users"));
        });
    }

    private static async Task<ValidatedDmlPlan> CreatePlanAsync(
        PostgresProvider provider, NativeDmlSchemaSnapshotService snapshot,
        string connectionString, string schema, string sql)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);
        await using var connection = provider.Connections.Create(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var proof = await snapshot.CaptureAsync(
            connection, SqlAgentToolType.Postgres, parsed, TestContext.Current.CancellationToken);
        Assert.NotNull(proof);
        var context = new SqlPlanValidationContext("postgres-m09-v1",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { schema + ".users", schema + ".source_users" })
            .WithSchemaSnapshot(proof);
        return await new DmlPlanFactory(provider.Metadata).CreateWithMetadataConnectionAsync(
            connection, connectionString, parsed, SqlAgentToolType.Postgres, context,
            maxAffectedRows: 2, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task WithTablesAsync(Func<string,string,string,Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION required for real M09 PostgreSQL integration.");
            Assert.Skip("Requires TEST_POSTGRES_CONNECTION.");
        }
        var schema = "hsa_m09_" + Guid.NewGuid().ToString("N")[..12];
        await ExecuteAsync(connectionString!, $"CREATE SCHEMA {schema}");
        try
        {
            await ExecuteAsync(connectionString!, $"""
                CREATE TABLE {schema}.users (id INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, name TEXT NOT NULL);
                INSERT INTO {schema}.users(id,name) VALUES (1,'Alice'),(2,'Bob');
                SELECT setval(pg_get_serial_sequence('{schema}.users','id'),2,true);
                CREATE TABLE {schema}.source_users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                INSERT INTO {schema}.source_users(id,name) VALUES (1,'Carol');
                """);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await test(connectionString!, schema, connection.ServerVersion.Trim());
        }
        finally
        {
            await ExecuteAsync(connectionString!, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
