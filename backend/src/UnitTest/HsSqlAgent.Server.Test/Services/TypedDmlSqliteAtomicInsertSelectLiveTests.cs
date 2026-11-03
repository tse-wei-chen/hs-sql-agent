using Admin.Service.Models;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.SqlParsing;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real SQLite 3.35+ provider, actual filesystem tables, native schema digest,
/// one-time aggregate approval and a single physical SQLite transaction.
/// Never mock the source identity, transaction or returned-row behavior.
/// </summary>
public sealed class TypedDmlSqliteAtomicInsertSelectLiveTests
{
    private static readonly DmlApprovalExecutionContext Context = new(
        "mcp-key:sqlite-m09-batch",
        "db-management:sqlite-m09-batch",
        SqlAgentToolType.Sqlite,
        "main");

    [Fact]
    public async Task NativeInsertSelect_ThenInsertValues_SingleApprovedTransaction_ReturnsOnlyCommittedIds()
    {
        await WithDatabaseAsync(async (cs, provider, runtime) =>
        {
            var session = await PreviewBatchAsync(runtime, provider, cs,
                "INSERT INTO main.users(name) SELECT name FROM main.source_users " +
                "WHERE id IN (1,2) RETURNING id, name;" +
                "INSERT INTO main.users(name) VALUES ('Eve')", maxRows: 3);

            Assert.Equal(2, session.Statements.Length);
            Assert.Equal(3, session.Challenge.AffectedRows);
            Assert.Equal(2, session.Statements[0].Preview.AffectedRows);
            Assert.All(session.Statements[0].Preview.Rows,
                row => Assert.False(row.ContainsKey("id")));
            Assert.Equal(2, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users"));

            var result = await runtime.CommitTransactionAsync(provider, cs, session,
                Policy(3), Grants, Context, TestContext.Current.CancellationToken);
            Assert.True(result.Committed);
            Assert.Equal(3, result.AffectedRows);
            Assert.Equal(new[] { 3, 4 },
                result.ReturnedRows.Select(row => Convert.ToInt32(row["id"])).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { "Carol", "Dave" },
                result.ReturnedRows.Select(row => (string)row["name"]!).OrderBy(x => x).ToArray());
            Assert.Equal(5, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users"));
            Assert.Equal(1, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users WHERE name = 'Eve'"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitTransactionAsync(provider, cs, session,
                    Policy(3), Grants, Context, TestContext.Current.CancellationToken));
            Assert.Equal(5, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users"));
        });
    }

    [Fact]
    public async Task NativeInsertSelect_SourceDriftAfterAggregatePreview_RollsBackEveryChild()
    {
        await WithDatabaseAsync(async (cs, provider, runtime) =>
        {
            var session = await PreviewBatchAsync(runtime, provider, cs,
                "INSERT INTO main.users(name) SELECT name FROM main.source_users " +
                "WHERE id BETWEEN 1 AND 2 RETURNING id, name;" +
                "INSERT INTO main.users(name) VALUES ('Eve')", 3);

            await ExecuteAsync(cs, "UPDATE main.source_users SET name = 'Mallory' WHERE id = 2");
            var denied = await runtime.CommitTransactionAsync(provider, cs, session,
                Policy(3), Grants, Context, TestContext.Current.CancellationToken);
            Assert.False(denied.Committed);
            Assert.Equal(0, denied.AffectedRows);
            Assert.Empty(denied.ReturnedRows);
            Assert.Equal(2, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users"));
        });
    }

    [Fact]
    public async Task NativeInsertSelect_FirstChildSuccess_SecondChildConstraintFailure_RollsBackInsertedRows()
    {
        await WithDatabaseAsync(async (cs, provider, runtime) =>
        {
            var session = await PreviewBatchAsync(runtime, provider, cs,
                "INSERT INTO main.users(name) SELECT name FROM main.source_users " +
                "WHERE id IN (1,2) RETURNING id, name;" +
                "INSERT INTO main.users(id,name) VALUES (1,'Duplicate')", 3);
            await Assert.ThrowsAsync<SqliteException>(() =>
                runtime.CommitTransactionAsync(provider, cs, session,
                    Policy(3), Grants, Context, TestContext.Current.CancellationToken));
            Assert.Equal(2, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users"));
            Assert.Equal(0, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users WHERE name IN ('Carol','Dave')"));
        });
    }

    [Fact]
    public async Task NativeInsertSelect_AggregateTotalRowCap_DeniesEvenWhenEachChildPasses()
    {
        await WithDatabaseAsync(async (cs, provider, runtime) =>
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                PreviewBatchAsync(runtime, provider, cs,
                    "INSERT INTO main.users(name) SELECT name FROM main.source_users WHERE id IN (1,2);" +
                    "INSERT INTO main.users(name) VALUES ('Eve')", maxRows: 2));
            Assert.Equal(2, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users"));
        });
    }

    [Fact]
    public async Task NativeInsertSelect_EarlierBatchStatementWidensApprovedSource_RollsBackTheEntireTransaction()
    {
        await WithDatabaseAsync(async (cs, provider, runtime) =>
        {
            var session = await PreviewBatchAsync(runtime, provider, cs,
                "INSERT INTO main.source_users(id,name) VALUES (3,'Eve');" +
                "INSERT INTO main.users(name) SELECT name FROM main.source_users " +
                "WHERE id IN (1,2,3) RETURNING id", 3);
            Assert.Equal(1, session.Statements[0].Preview.AffectedRows);
            Assert.Equal(2, session.Statements[1].Preview.AffectedRows);

            // The first child grows the later source predicate inside the same
            // physical transaction. The later approved preimage is now stale.
            var rejected = await runtime.CommitTransactionAsync(provider, cs, session,
                Policy(3), Grants, Context, TestContext.Current.CancellationToken);
            Assert.False(rejected.Committed);
            Assert.Equal(0, rejected.AffectedRows);
            Assert.Empty(rejected.ReturnedRows);
            Assert.Equal(2, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.source_users"));
            Assert.Equal(2, await ScalarAsync(cs, "SELECT COUNT(*) FROM main.users"));
        });
    }

    [Theory]
    [InlineData("non-sqlite")]
    [InlineData("two-source-inserts")]
    [InlineData("single-statement")]
    [InlineData("unverified-catalog")]
    public async Task NativeInsertSelect_UnprovenBatchShape_DeniedBeforeTouchingDatabase(string shape)
    {
        var sql = shape switch
        {
            "non-sqlite" =>
                "INSERT INTO public.users(name) SELECT name FROM public.source_users WHERE id=1;" +
                "INSERT INTO public.users(name) VALUES('Eve')",
            "two-source-inserts" =>
                "INSERT INTO main.users(name) SELECT name FROM main.source_users WHERE id=1;" +
                "INSERT INTO main.users(name) SELECT name FROM main.source_users WHERE id=2",
            "single-statement" =>
                "INSERT INTO main.users(name) SELECT name FROM main.source_users WHERE id=1",
            _ =>
                "INSERT INTO main.users(name) SELECT name FROM main.source_users WHERE id=1;" +
                "INSERT INTO main.users(name) VALUES('Eve')"
        };
        var dialect = shape == "non-sqlite" ? SqlAgentToolType.Postgres : SqlAgentToolType.Sqlite;
        var batch = CoreDmlBatchTextParser.ParseDmlBatch(sql, dialect);
        var provider = new SqliteProvider();
        var runtime = new TypedDmlRuntime(); // Intentionally no native snapshot/verifier.
        var denial = await Assert.ThrowsAsync<NotSupportedException>(() =>
            runtime.PreviewTransactionAsync(provider, "not-a-database", batch, Policy(3),
                Grants, Context, TestContext.Current.CancellationToken));
        Assert.Contains("SQLite", denial.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly IReadOnlySet<string> Grants =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "main.users", "main.source_users" };

    private static SecurityPolicyModel Policy(int maxRows) => new()
    {
        DmlMaxAffectedRows = maxRows
    };

    private static async Task<TypedDmlTransactionApprovalSession> PreviewBatchAsync(
        TypedDmlRuntime runtime, SqliteProvider provider, string cs, string sql, int maxRows)
    {
        var parsed = await runtime.ParseDmlBatchWithVerifiedRuntimeProfileAsync(
            provider, cs, sql, SqlAgentToolType.Sqlite, TestContext.Current.CancellationToken);
        return await runtime.PreviewTransactionAsync(
            provider, cs, parsed, Policy(maxRows), Grants, Context, TestContext.Current.CancellationToken);
    }

    private static async Task WithDatabaseAsync(
        Func<string, SqliteProvider, TypedDmlRuntime, Task> test)
    {
        var file = Path.Combine(Path.GetTempPath(), $"hsa-sqlite-batch-m09-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={file};Pooling=False";
        try
        {
            await ExecuteAsync(cs, """
                CREATE TABLE users(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                INSERT INTO users(id,name) VALUES (1,'Alice'),(2,'Bob');
                CREATE TABLE source_users(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                INSERT INTO source_users(id,name) VALUES (1,'Carol'),(2,'Dave');
                """);
            var liveProof = new NativeDmlSchemaSnapshotService();
            var runtime = new TypedDmlRuntime(
                schemaSnapshotSource: liveProof,
                schemaFreshnessVerifier: liveProof);
            await test(cs, new SqliteProvider(), runtime);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
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

    private static async Task<long> ScalarAsync(string cs, string sql)
    {
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
