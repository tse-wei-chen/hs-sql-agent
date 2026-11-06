using System.Collections.Immutable;
using Admin.Service.Models;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.SqlParsing;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real SQLite DML preview/commit boundaries. Each test uses an isolated
/// physical database and never substitutes a mock executor or schema verifier.
/// Permission/context mutations must not consume an approval or touch the DB;
/// a successful or failed commit must consume the aggregate challenge once.
/// </summary>
public sealed class TypedDmlSqliteApprovalReplayToctouLiveTests
{
    private static readonly DmlApprovalExecutionContext ApprovalContext =
        new("mcp-key:7", "db-management:11", SqlAgentToolType.Sqlite, "main");

    private static readonly IReadOnlySet<string> AllowedTables =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main.users" };

    private const string InsertBatch =
        "INSERT INTO main.users (id,name) VALUES (3,'Carol');" +
        "INSERT INTO main.users (id,name) VALUES (4,'Dave')";

    [Fact]
    public async Task ApprovalContextPolicyAndOrderTampering_NoPhysicalChange_ValidCommitRemainsSingleUse()
    {
        await WithDatabaseAsync(async (connectionString, provider, runtime) =>
        {
            var approved = await PreviewBatchAsync(runtime, provider, connectionString, InsertBatch);
            var baseline = await PhysicalSnapshotAsync(connectionString);
            Assert.Equal(2, approved.Statements.Length);
            Assert.Equal(2, approved.Challenge.AffectedRows);

            var contexts = new[]
            {
                ApprovalContext with { PrincipalIdentity = "mcp-key:8" },
                ApprovalContext with { TargetIdentity = "db-management:12" },
                ApprovalContext with { Provider = SqlAgentToolType.Postgres },
                ApprovalContext with { DatabaseIdentity = "attached" },
                ApprovalContext with { PrincipalIdentity = "mcp-key:7\u200B" },
                ApprovalContext with { DatabaseIdentity = "MAIN" }
            };
            foreach (var changedContext in contexts)
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    runtime.CommitTransactionAsync(
                        provider, connectionString, approved, Policy(), AllowedTables,
                        changedContext, TestContext.Current.CancellationToken));
                Assert.Contains("context", exception.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(baseline, await PhysicalSnapshotAsync(connectionString));
            }

            var changedPolicy = Policy();
            changedPolicy.DmlMaxAffectedRows++;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitTransactionAsync(
                    provider, connectionString, approved, changedPolicy, AllowedTables,
                    ApprovalContext, TestContext.Current.CancellationToken));
            Assert.Equal(baseline, await PhysicalSnapshotAsync(connectionString));

            var widerWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "main.users", "main.secrets" };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitTransactionAsync(
                    provider, connectionString, approved, Policy(), widerWhitelist,
                    ApprovalContext, TestContext.Current.CancellationToken));
            Assert.Equal(baseline, await PhysicalSnapshotAsync(connectionString));

            var reordered = new TypedDmlTransactionApprovalSession(
                approved.Statements.Reverse().ToImmutableArray(), approved.Challenge);
            var reorderedError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitTransactionAsync(
                    provider, connectionString, reordered, Policy(), AllowedTables,
                    ApprovalContext, TestContext.Current.CancellationToken));
            Assert.Contains("order", reorderedError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(baseline, await PhysicalSnapshotAsync(connectionString));

            // A rejected (unprivileged) attempt must not use up the legitimate
            // principal's one-time approval; only the correct session can commit.
            var committed = await runtime.CommitTransactionAsync(
                provider, connectionString, approved, Policy(), AllowedTables,
                ApprovalContext, TestContext.Current.CancellationToken);
            Assert.True(committed.Committed);
            Assert.Equal(2, committed.AffectedRows);
            Assert.Equal("1:Alice|2:Bob|3:Carol|4:Dave",
                await PhysicalSnapshotAsync(connectionString));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitTransactionAsync(
                    provider, connectionString, approved, Policy(), AllowedTables,
                    ApprovalContext, TestContext.Current.CancellationToken));
            Assert.Equal("1:Alice|2:Bob|3:Carol|4:Dave",
                await PhysicalSnapshotAsync(connectionString));
        });
    }

    [Fact]
    public async Task ConcurrentCommit_OnlyOneCanConsumeApprovedChallenge_AndInsertRowsOnce()
    {
        await WithDatabaseAsync(async (connectionString, provider, runtime) =>
        {
            var approved = await PreviewBatchAsync(runtime, provider, connectionString, InsertBatch);
            var baseline = await PhysicalSnapshotAsync(connectionString);
            Assert.Equal("1:Alice|2:Bob", baseline);

            async Task<string> CommitAttemptAsync()
            {
                try
                {
                    var result = await runtime.CommitTransactionAsync(
                        provider, connectionString, approved, Policy(), AllowedTables,
                        ApprovalContext, TestContext.Current.CancellationToken);
                    Assert.True(result.Committed);
                    return "committed";
                }
                catch (InvalidOperationException exception)
                {
                    Assert.Contains("consumed", exception.Message, StringComparison.OrdinalIgnoreCase);
                    return "replayed";
                }
            }

            var results = await Task.WhenAll(CommitAttemptAsync(), CommitAttemptAsync());
            Assert.Equal(1, results.Count(x => x == "committed"));
            Assert.Equal(1, results.Count(x => x == "replayed"));
            Assert.Equal("1:Alice|2:Bob|3:Carol|4:Dave",
                await PhysicalSnapshotAsync(connectionString));
        });
    }

    [Fact]
    public async Task ChangedApprovedRowSet_SecondStatementDenied_EntireTransactionRolledBack()
    {
        await WithDatabaseAsync(async (connectionString, provider, runtime) =>
        {
            var approved = await PreviewBatchAsync(runtime, provider, connectionString,
                "UPDATE main.users SET name = 'Approved' WHERE id = 1;" +
                "DELETE FROM main.users WHERE name = 'Bob'");
            Assert.Equal(2, approved.Challenge.AffectedRows);

            await ExecuteAsync(connectionString,
                "UPDATE main.users SET name = 'Mallory' WHERE id = 2");
            var baseline = await PhysicalSnapshotAsync(connectionString);
            Assert.Equal("1:Alice|2:Mallory", baseline);

            var result = await runtime.CommitTransactionAsync(
                provider, connectionString, approved, Policy(), AllowedTables,
                ApprovalContext, TestContext.Current.CancellationToken);
            Assert.False(result.Committed);
            Assert.Equal(0, result.AffectedRows);
            Assert.Equal(baseline, await PhysicalSnapshotAsync(connectionString));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitTransactionAsync(
                    provider, connectionString, approved, Policy(), AllowedTables,
                    ApprovalContext, TestContext.Current.CancellationToken));
            Assert.Equal(baseline, await PhysicalSnapshotAsync(connectionString));
        });
    }

    [Fact]
    public async Task SecondStatementUniqueKeyFailure_RollsBackFirstInsert_AndCannotReplay()
    {
        await WithDatabaseAsync(async (connectionString, provider, runtime) =>
        {
            var approved = await PreviewBatchAsync(runtime, provider, connectionString,
                "INSERT INTO main.users (id,name) VALUES (3,'UnapprovedPartial');" +
                "INSERT INTO main.users (id,name) VALUES (1,'Duplicate')");
            var baseline = await PhysicalSnapshotAsync(connectionString);

            await Assert.ThrowsAsync<SqliteException>(() =>
                runtime.CommitTransactionAsync(
                    provider, connectionString, approved, Policy(), AllowedTables,
                    ApprovalContext, TestContext.Current.CancellationToken));
            Assert.Equal(baseline, await PhysicalSnapshotAsync(connectionString));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitTransactionAsync(
                    provider, connectionString, approved, Policy(), AllowedTables,
                    ApprovalContext, TestContext.Current.CancellationToken));
            Assert.Equal(baseline, await PhysicalSnapshotAsync(connectionString));
        });
    }

    [Fact]
    public async Task ExpiredPreview_RejectsWithoutConsumingOrMutatingPhysicalDatabase()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        await WithDatabaseAsync(async (connectionString, provider, runtime) =>
        {
            var approved = await PreviewBatchAsync(runtime, provider, connectionString, InsertBatch);
            var baseline = await PhysicalSnapshotAsync(connectionString);
            clock.Advance(TimeSpan.FromHours(1));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitTransactionAsync(
                    provider, connectionString, approved, Policy(), AllowedTables,
                    ApprovalContext, TestContext.Current.CancellationToken));
            Assert.Contains("expired", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(baseline, await PhysicalSnapshotAsync(connectionString));
        }, clock);
    }

    private static SecurityPolicyModel Policy() => new() { DmlMaxAffectedRows = 10 };

    private static async Task<TypedDmlTransactionApprovalSession> PreviewBatchAsync(
        TypedDmlRuntime runtime, SqliteProvider provider, string connectionString, string sql)
    {
        var parsed = await runtime.ParseDmlBatchWithVerifiedRuntimeProfileAsync(
            provider, connectionString, sql, SqlAgentToolType.Sqlite,
            TestContext.Current.CancellationToken);
        return await runtime.PreviewTransactionAsync(
            provider, connectionString, parsed, Policy(), AllowedTables, ApprovalContext,
            TestContext.Current.CancellationToken);
    }

    private static async Task WithDatabaseAsync(
        Func<string, SqliteProvider, TypedDmlRuntime, Task> scenario,
        TimeProvider? clock = null)
    {
        var file = Path.Combine(Path.GetTempPath(),
            $"hsa-dml-replay-{Guid.NewGuid():N}.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
            { DataSource = file, Pooling = false }.ToString();
        try
        {
            await ExecuteAsync(connectionString, """
                CREATE TABLE main.users(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                INSERT INTO main.users(id,name) VALUES (1,'Alice'),(2,'Bob');
                """);
            var snapshots = new NativeDmlSchemaSnapshotService();
            var runtime = new TypedDmlRuntime(
                timeProvider: clock,
                schemaSnapshotSource: snapshots,
                schemaFreshnessVerifier: snapshots);
            await scenario(connectionString, new SqliteProvider(), runtime);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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

    private static async Task<string> PhysicalSnapshotAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_concat(id || ':' || name, '|')
            FROM (SELECT id, name FROM main.users ORDER BY id)
            """;
        return (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))
            ?.ToString() ?? throw new InvalidOperationException("SQLite state is unreadable.");
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
