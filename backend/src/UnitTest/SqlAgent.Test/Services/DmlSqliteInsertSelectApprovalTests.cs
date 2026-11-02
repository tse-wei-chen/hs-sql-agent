using System.Data;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlSqliteInsertSelectApprovalTests
{
    private const string Approval = "sqlite-self-source-approval-v1";
    private const string Policy = "sqlite-self-source-policy-v1";
    private const string InsertSql =
        "INSERT INTO main.users (name) SELECT name FROM main.users WHERE id = 1";

    [Fact]
    public async Task ApprovedSelfSource_InsertSelect_PreviewsExactRowsAndCommitsOnce()
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, InsertSql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Equal("main.users", plan.TableName);

            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Alice", preview.Rows.Single()["name"]);
            Assert.NotNull(preview.Challenge.RowSetFingerprint);

            var committed = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            Assert.Equal(3, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
            Assert.Equal(2, await ScalarAsync(
                connectionString, "SELECT COUNT(*) FROM users WHERE name = 'Alice'"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version));
        });
    }

    [Fact]
    public async Task SourceRowsChangedAfterApproval_RevalidationRollsBackWithoutInserting()
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, InsertSql);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version);

            await ExecuteAsync(connectionString, "UPDATE users SET name = 'Mallory' WHERE id = 1");
            var rejected = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, version);
            Assert.False(rejected.Committed);
            Assert.Contains("source row", rejected.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
        });
    }

    [Theory]
    [InlineData("INSERT INTO main.users (name) SELECT name FROM main.users")]
    [InlineData("INSERT INTO main.users (name) SELECT name FROM main.users WHERE id = 1 OR id = 2")]
    [InlineData("INSERT INTO main.users (name) SELECT upper(name) FROM main.users WHERE id = 1")]
    [InlineData("INSERT INTO main.users (name) SELECT name FROM main.other_users WHERE id = 1")]
    public async Task UnsupportedOrUnboundedSource_FailsClosedBeforePreview(string sql)
    {
        await WithDatabaseAsync(async (connectionString, _) =>
        {
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                CreatePlanAsync(provider, snapshot, connectionString, sql));
            Assert.Equal(2, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
        });
    }

    [Fact]
    public async Task NativeSqliteSerializableTransaction_ReservesWriterBeforeSourceRevalidation()
    {
        await WithDatabaseAsync(async (connectionString, _) =>
        {
            var provider = new SqliteProvider();
            await using var first = provider.Connections.Create(connectionString);
            await first.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await first.BeginTransactionAsync(
                IsolationLevel.Serializable, TestContext.Current.CancellationToken);

            // A second writer cannot get between the source recheck and the
            // approved INSERT when the provider begins an IMMEDIATE transaction.
            await using var rival = new SqliteConnection(connectionString);
            await rival.OpenAsync(TestContext.Current.CancellationToken);
            await using var rivalWrite = rival.CreateCommand();
            rivalWrite.CommandText = "UPDATE users SET name = 'hijack' WHERE id = 1";
            rivalWrite.CommandTimeout = 1;
            var denied = await Assert.ThrowsAsync<SqliteException>(() =>
                rivalWrite.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            Assert.Equal(5, denied.SqliteErrorCode);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, await ScalarAsync(connectionString,
                "SELECT COUNT(*) FROM users WHERE name = 'Alice'"));
        });
    }

    [Fact]
    public async Task ForgedOrModifiedSourcePlan_CannotPassCoordinatorValidation()
    {
        await WithDatabaseAsync(async (connectionString, _) =>
        {
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, snapshot, connectionString, InsertSql);

            Assert.Throws<InvalidOperationException>(() => DmlCoordinator.ValidatePlan(
                plan with { SqliteInsertSelectProof = null }));
            Assert.Throws<InvalidOperationException>(() => DmlCoordinator.ValidatePlan(
                plan with { RowIdentityColumns = ["name", "id"] }));
            Assert.Throws<InvalidOperationException>(() => DmlCoordinator.ValidatePlan(
                plan with { MaxAffectedRows = 0 }));
        });
    }

    [Fact]
    public async Task ExcessSourceRows_RefusesApprovalBeforeAnyInsert()
    {
        await WithDatabaseAsync(async (connectionString, version) =>
        {
            await ExecuteAsync(connectionString,
                "INSERT INTO users (id, name) VALUES (3, 'Alice'), (4, 'Alice')");
            var provider = new SqliteProvider();
            var snapshot = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(
                provider, snapshot, connectionString,
                "INSERT INTO main.users (name) SELECT name FROM main.users WHERE name = 'Alice'");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: snapshot);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(4, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM users"));
        });
    }

    private static async Task<ValidatedDmlPlan> CreatePlanAsync(
        SqliteProvider provider, NativeDmlSchemaSnapshotService snapshot,
        string connectionString, string sql)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Sqlite);
        await using var connection = provider.Connections.Create(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var schema = await snapshot.CaptureAsync(
            connection, SqlAgentToolType.Sqlite, parsed, TestContext.Current.CancellationToken);
        Assert.NotNull(schema);
        var context = new SqlPlanValidationContext(
            Policy, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main.users" })
            .WithSchemaSnapshot(schema);
        return await new DmlPlanFactory(provider.Metadata).CreateWithMetadataConnectionAsync(
            connection, connectionString, parsed, SqlAgentToolType.Sqlite, context,
            maxAffectedRows: 2, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var file = Path.Combine(Path.GetTempPath(), $"hsa-insert-select-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={file};Pooling=False";
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var version = connection.ServerVersion.Trim();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                    INSERT INTO users (id, name) VALUES (1, 'Alice'), (2, 'Bob');
                    CREATE TABLE other_users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await test(connectionString, version);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
