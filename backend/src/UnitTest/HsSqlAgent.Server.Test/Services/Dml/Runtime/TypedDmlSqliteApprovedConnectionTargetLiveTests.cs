using Admin.Service.Models;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.Server.Services;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Two genuinely distinct SQLite files, with identical schema/rows and the
/// same logical approval context. A preview approved on file A must NEVER
/// execute on file B, even when both DBs have identical DML evidence.
/// </summary>
public sealed class TypedDmlSqliteApprovedConnectionTargetLiveTests
{
    private static readonly DmlApprovalExecutionContext Context =
        new("mcp-key:7", "db-management:11", SqlAgentToolType.Sqlite, "main");

    private static readonly IReadOnlySet<string> AllowedTables =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main.users" };

    private static SecurityPolicyModel Policy() => new() { DmlMaxAffectedRows = 10 };

    [Fact]
    public async Task SingleInsert_ApprovedOnFileA_CannotCommitToIdenticalFileB()
    {
        await WithIdenticalTargetsAsync(async (fileA, fileB, provider, runtime) =>
        {
            var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
                provider, fileA,
                "INSERT INTO main.users(id,name) VALUES (3,'Carol')",
                SqlAgentToolType.Sqlite, TestContext.Current.CancellationToken);
            var approved = await runtime.PreviewAsync(
                provider, fileA, parsed, Policy(), AllowedTables, Context,
                TestContext.Current.CancellationToken);

            Assert.Equal("1:Alice|2:Bob", await ReadRowsAsync(fileA));
            Assert.Equal("1:Alice|2:Bob", await ReadRowsAsync(fileB));

            var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitAsync(provider, fileB, approved, Policy(), AllowedTables,
                    Context, TestContext.Current.CancellationToken));
            Assert.Contains("connection", denied.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("preview", denied.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("1:Alice|2:Bob", await ReadRowsAsync(fileA));
            Assert.Equal("1:Alice|2:Bob", await ReadRowsAsync(fileB));

            // Rejected cross-target commit must not consume the original
            // one-time approval; the exact original physical target succeeds.
            var committed = await runtime.CommitAsync(
                provider, fileA, approved, Policy(), AllowedTables,
                Context, TestContext.Current.CancellationToken);
            Assert.True(committed.Committed);
            Assert.Equal("1:Alice|2:Bob|3:Carol", await ReadRowsAsync(fileA));
            Assert.Equal("1:Alice|2:Bob", await ReadRowsAsync(fileB));
        });
    }

    [Fact]
    public async Task BatchInsert_ApprovedOnFileA_CannotCommitToIdenticalFileB()
    {
        await WithIdenticalTargetsAsync(async (fileA, fileB, provider, runtime) =>
        {
            var parsed = await runtime.ParseDmlBatchWithVerifiedRuntimeProfileAsync(
                provider, fileA,
                "INSERT INTO main.users(id,name) VALUES (3,'Carol');" +
                "INSERT INTO main.users(id,name) VALUES (4,'Dave')",
                SqlAgentToolType.Sqlite, TestContext.Current.CancellationToken);
            var approved = await runtime.PreviewTransactionAsync(
                provider, fileA, parsed, Policy(), AllowedTables, Context,
                TestContext.Current.CancellationToken);

            Assert.Equal(2, approved.Challenge.AffectedRows);
            var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitTransactionAsync(
                    provider, fileB, approved, Policy(), AllowedTables, Context,
                    TestContext.Current.CancellationToken));
            Assert.Contains("connection", denied.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("preview", denied.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("1:Alice|2:Bob", await ReadRowsAsync(fileA));
            Assert.Equal("1:Alice|2:Bob", await ReadRowsAsync(fileB));

            var result = await runtime.CommitTransactionAsync(
                provider, fileA, approved, Policy(), AllowedTables, Context,
                TestContext.Current.CancellationToken);
            Assert.True(result.Committed);
            Assert.Equal(2, result.AffectedRows);
            Assert.Equal("1:Alice|2:Bob|3:Carol|4:Dave", await ReadRowsAsync(fileA));
            Assert.Equal("1:Alice|2:Bob", await ReadRowsAsync(fileB));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitTransactionAsync(
                    provider, fileB, approved, Policy(), AllowedTables, Context,
                    TestContext.Current.CancellationToken));
            Assert.Equal("1:Alice|2:Bob", await ReadRowsAsync(fileB));
        });
    }

    [Fact]
    public async Task LegacyUnboundSession_CannotBypassPhysicalConnectionProof()
    {
        await WithIdenticalTargetsAsync(async (fileA, fileB, provider, runtime) =>
        {
            var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
                provider, fileA, "INSERT INTO main.users(id,name) VALUES (3,'Carol')",
                SqlAgentToolType.Sqlite, TestContext.Current.CancellationToken);
            var approved = await runtime.PreviewAsync(
                provider, fileA, parsed, Policy(), AllowedTables, Context,
                TestContext.Current.CancellationToken);
            var legacy = approved with { PreviewConnectionFingerprint = null };

            var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitAsync(provider, fileA, legacy, Policy(), AllowedTables,
                    Context, TestContext.Current.CancellationToken));
            Assert.Contains("connection", denied.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("1:Alice|2:Bob", await ReadRowsAsync(fileA));
            Assert.Equal("1:Alice|2:Bob", await ReadRowsAsync(fileB));

            var result = await runtime.CommitAsync(provider, fileA, approved, Policy(),
                AllowedTables, Context, TestContext.Current.CancellationToken);
            Assert.True(result.Committed);
            Assert.Equal("1:Alice|2:Bob|3:Carol", await ReadRowsAsync(fileA));
        });
    }

    private static async Task WithIdenticalTargetsAsync(
        Func<string, string, SqliteProvider, TypedDmlRuntime, Task> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hsa-dml-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var fileA = new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(folder, "target-a.sqlite"), Pooling = false }.ToString();
        var fileB = new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(folder, "target-b.sqlite"), Pooling = false }.ToString();
        try
        {
            const string schema = """
                CREATE TABLE main.users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                INSERT INTO main.users(id,name) VALUES (1,'Alice'),(2,'Bob');
                """;
            await ExecuteAsync(fileA, schema);
            await ExecuteAsync(fileB, schema);
            var attestation = new NativeDmlSchemaSnapshotService();
            var runtime = new TypedDmlRuntime(
                schemaSnapshotSource: attestation,
                schemaFreshnessVerifier: attestation);
            await test(fileA, fileB, new SqliteProvider(), runtime);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task ExecuteAsync(string cs, string sql)
    {
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReadRowsAsync(string cs)
    {
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_concat(id || ':' || name, '|')
            FROM (SELECT id, name FROM main.users ORDER BY id)
            """;
        return (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))
            ?.ToString() ?? throw new InvalidOperationException("Physical SQLite state is unreadable.");
    }
}
