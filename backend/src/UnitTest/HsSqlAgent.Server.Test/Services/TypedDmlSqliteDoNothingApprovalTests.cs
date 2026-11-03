using Admin.Service.Models;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.Server.Services;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real physical SQLite DB; never a mocked connection. The compiler-approved
/// DO NOTHING statement must preview the target PK conflict and revalidate it
/// inside the same SQLite IMMEDIATE write transaction that executes the insert.
/// </summary>
public sealed class TypedDmlSqliteDoNothingApprovalTests
{
    private static readonly DmlApprovalExecutionContext Context =
        new("mcp-user:42", "db-management:14", SqlAgentToolType.Sqlite, "main");
    private static readonly SecurityPolicyModel Policy = new() { DmlMaxAffectedRows = 1 };
    private static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main.users" };
    private const string InsertSql =
        "INSERT INTO users (id, name) VALUES (7, 'new-name') ON CONFLICT (id) DO NOTHING";

    [Theory]
    [InlineData(false, 1, "new-name")]
    [InlineData(true, 0, "original")]
    public async Task NativeSqlite_PreviewAndCommitRespectsExistingTargetConflict(
        bool existing, int affected, string expectedName)
    {
        await WithDatabaseAsync(async (provider, cs, runtime, token) =>
        {
            if (existing)
                await ExecAsync(cs, "INSERT INTO users (id, name) VALUES (7, 'original')", token);

            var preview = await PreviewAsync(provider, cs, runtime, InsertSql, token);
            Assert.Equal(DmlApprovalMode.InsertConflictDoNothing, preview.Plan.ApprovalMode);
            Assert.Equal(affected, preview.Preview.AffectedRows);
            Assert.NotNull(preview.Preview.Challenge.RowSetFingerprint);
            Assert.Equal(1, preview.Plan.InsertRows.Length);
            Assert.Equal(1, preview.Plan.RowIdentityColumns.Length);
            Assert.Equal("id", preview.Plan.RowIdentityColumns[0], ignoreCase: true);

            var committed = await runtime.CommitAsync(provider, cs, preview, Policy, Allowed,
                Context, token);
            Assert.True(committed.Committed);
            Assert.Equal(affected, committed.AffectedRows);
            Assert.Equal(expectedName, await ScalarStringAsync(cs,
                "SELECT name FROM users WHERE id = 7", token));

            var replay = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runtime.CommitAsync(provider, cs, preview, Policy, Allowed, Context, token));
            Assert.Contains("consumed", replay.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task NativeSqlite_UnmatchedKeyInsertedAfterPreview_CancelsApprovedInsert()
    {
        await WithDatabaseAsync(async (provider, cs, runtime, token) =>
        {
            var preview = await PreviewAsync(provider, cs, runtime, InsertSql, token);
            Assert.Equal(1, preview.Preview.AffectedRows);
            await ExecAsync(cs, "INSERT INTO users (id, name) VALUES (7, 'racer')", token);
            var result = await runtime.CommitAsync(provider, cs, preview, Policy, Allowed,
                Context, token);
            Assert.False(result.Committed);
            Assert.Equal(0, result.AffectedRows);
            Assert.Equal("racer", await ScalarStringAsync(cs,
                "SELECT name FROM users WHERE id = 7", token));
        });
    }

    [Fact]
    public async Task NativeSqlite_PreviouslyConflictingKeyRemovedAfterPreview_CancelsSkip()
    {
        await WithDatabaseAsync(async (provider, cs, runtime, token) =>
        {
            await ExecAsync(cs, "INSERT INTO users (id, name) VALUES (7, 'original')", token);
            var preview = await PreviewAsync(provider, cs, runtime, InsertSql, token);
            Assert.Equal(0, preview.Preview.AffectedRows);
            await ExecAsync(cs, "DELETE FROM users WHERE id = 7", token);
            var result = await runtime.CommitAsync(provider, cs, preview, Policy, Allowed,
                Context, token);
            Assert.False(result.Committed);
            Assert.Equal(0, await ScalarIntAsync(cs, "SELECT COUNT(*) FROM users", token));
        });
    }

    [Fact]
    public async Task NativeSqlite_SchemaOrUniquenessChangesAfterApproval_FailsClosed()
    {
        await WithDatabaseAsync(async (provider, cs, runtime, token) =>
        {
            var preview = await PreviewAsync(provider, cs, runtime, InsertSql, token);
            await ExecAsync(cs, "CREATE UNIQUE INDEX uq_users_name ON users(name)", token);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runtime.CommitAsync(provider, cs, preview, Policy, Allowed, Context, token));
            Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await ScalarIntAsync(cs, "SELECT COUNT(*) FROM users", token));
        });
    }

    [Theory]
    [InlineData("CREATE TABLE users (id INTEGER, name TEXT NOT NULL)", "unique")]
    [InlineData("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT UNIQUE)", "sole")]
    [InlineData("CREATE TABLE users (id INTEGER, tenant INTEGER, name TEXT, PRIMARY KEY (id,tenant))", "key")]
    public async Task NativeSqlite_UndischargedPhysicalPrimaryKeyProof_RejectsBeforeApproval(
        string ddl, string reason)
    {
        await WithDatabaseAsync(async (provider, cs, runtime, token) =>
        {
            await ExecAsync(cs, "DROP TABLE users; " + ddl, token);
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => PreviewAsync(provider, cs, runtime, InsertSql, token));
            Assert.Contains(reason, error.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("INSERT INTO users (id, name) VALUES (7, 'x') ON CONFLICT (id) DO UPDATE SET name = excluded.name")]
    [InlineData("INSERT INTO users (id, name) VALUES (7, 'x'), (8, 'y') ON CONFLICT (id) DO NOTHING")]
    [InlineData("INSERT INTO users (id, name) VALUES (NULL, 'x') ON CONFLICT (id) DO NOTHING")]
    [InlineData("INSERT INTO users (id, name) VALUES (7, 'x') ON CONFLICT DO NOTHING")]
    [InlineData("INSERT INTO users (id, name) VALUES (7, 'x') ON CONFLICT (id) DO NOTHING RETURNING id")]
    public async Task NativeSqlite_UnprovenConflictShapes_RemainFailClosed(string sql)
    {
        await WithDatabaseAsync(async (provider, cs, runtime, token) =>
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => PreviewAsync(provider, cs, runtime, sql, token));
            Assert.True(error is NotSupportedException or InvalidOperationException,
                $"Unexpected rejection: {error}");
        });
    }

    [Fact]
    public async Task NativeSqlite_AtomicBatchConflictStillRejectsBeforeAnyChildApproval()
    {
        await WithDatabaseAsync(async (provider, cs, runtime, token) =>
        {
            var batch = await runtime.ParseDmlBatchWithVerifiedRuntimeProfileAsync(
                provider, cs,
                InsertSql + "; INSERT INTO users (id, name) VALUES (8, 'ordinary')",
                SqlAgentToolType.Sqlite, token);
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                runtime.PreviewTransactionAsync(provider, cs, batch, Policy, Allowed,
                    Context, token));
            Assert.Equal(0, await ScalarIntAsync(cs, "SELECT COUNT(*) FROM users", token));
        });
    }

    private static async Task<TypedDmlApprovalSession> PreviewAsync(
        SqliteProvider provider, string cs, TypedDmlRuntime runtime,
        string sql, CancellationToken token)
    {
        var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
            provider, cs, sql, SqlAgentToolType.Sqlite, token);
        return await runtime.PreviewAsync(provider, cs, parsed, Policy, Allowed, Context, token);
    }

    private static async Task WithDatabaseAsync(
        Func<SqliteProvider, string, TypedDmlRuntime, CancellationToken, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "m10-sqlite-conflict-" + Guid.NewGuid().ToString("N") + ".db");
        var cs = $"Data Source={path};Pooling=False";
        var token = TestContext.Current.CancellationToken;
        var provider = new SqliteProvider();
        var native = new NativeDmlSchemaSnapshotService();
        var runtime = new TypedDmlRuntime(schemaSnapshotSource: native,
            schemaFreshnessVerifier: native);
        try
        {
            await ExecAsync(cs, "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)", token);
            await test(provider, cs, runtime, token);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task ExecAsync(string cs, string sql, CancellationToken token)
    {
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string> ScalarStringAsync(
        string cs, string sql, CancellationToken token)
    {
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<int> ScalarIntAsync(string cs, string sql, CancellationToken token)
    {
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }
}
