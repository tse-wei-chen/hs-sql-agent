using System.Diagnostics;
using Admin.Service.Models;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Testcontainers.PostgreSql;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// #575: Docker-backed PostgreSQL 16, actual server-side connection termination
/// and cancellation while the native command is executing. Unlike synthetic
/// transaction ACK loss, pg_terminate_backend severs the real Npgsql socket.
/// These tests do not claim that a cleanly completed COMMIT loses its ACK.
/// </summary>
/// <summary>
/// Shared xUnit class fixture: one real PostgreSQL 16 Testcontainer for all
/// three native-fault cases, with unique per-test application names and
/// independent witness connections.
/// </summary>
public sealed class PostgresNativeFaultFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("hsql_faults")
        .WithUsername("fault_operator")
        .WithPassword("Fault_2026_Test!")
        .Build();

    public ValueTask InitializeAsync() =>
        new(_postgres.StartAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    public string ConnectionString(string application) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            ApplicationName = application,
            Pooling = false,
            CommandTimeout = 90,
            Timeout = 10
        }.ConnectionString;
}

public sealed class PostgresNativeFaultTestcontainersTests(PostgresNativeFaultFixture fixture)
    : IClassFixture<PostgresNativeFaultFixture>
{
    [Fact]
    public async Task Query_ServerTerminatesActualRunningNativeCommand_NoRowsAreReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = "hsql_query_kill_" + Guid.NewGuid().ToString("N")[..12];
        var target = fixture.ConnectionString(app);
        await using var witness = new NpgsqlConnection(fixture.ConnectionString("hsql_query_witness"));
        await witness.OpenAsync(ct);

        var executing = new TypedQueryRuntime().ExecuteAsync(
            new PostgresProvider(), target,
            "SELECT pg_catalog.pg_sleep(60)",
            SqlAgentToolType.Postgres, QueryPolicy(), allowedTables: null, ct);

        var backend = await WaitForExecutingBackendAsync(
            witness, app, "pg_sleep", requireCommit: false, ct);
        Assert.True(await TerminateBackendAsync(witness, backend, ct),
            "The PostgreSQL server did not terminate the executing physical session.");

        var failure = await Record.ExceptionAsync(async () =>
        {
            _ = await executing.WaitAsync(TimeSpan.FromSeconds(15), ct);
        });
        Assert.NotNull(failure);
        Assert.IsNotType<TimeoutException>(failure);
        Assert.False(ct.IsCancellationRequested);

        // The independent physical connection is healthy. The terminated
        // client cannot publish a partial Query result as successful.
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(witness, "SELECT 1", ct)));
    }

    [Fact]
    public async Task Query_CancellationDuringNativeSql_InterruptsPhysicalCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = "hsql_query_cancel_" + Guid.NewGuid().ToString("N")[..12];
        await using var witness = new NpgsqlConnection(fixture.ConnectionString("hsql_cancel_witness"));
        await witness.OpenAsync(ct);
        using var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var executing = new TypedQueryRuntime().ExecuteAsync(
            new PostgresProvider(), fixture.ConnectionString(app),
            "SELECT pg_catalog.pg_sleep(60)",
            SqlAgentToolType.Postgres, QueryPolicy(), allowedTables: null,
            queryCancellation.Token);
        var backend = await WaitForExecutingBackendAsync(
            witness, app, "pg_sleep", requireCommit: false, ct);
        queryCancellation.Cancel();

        var failure = await Record.ExceptionAsync(async () =>
        {
            _ = await executing.WaitAsync(TimeSpan.FromSeconds(15), ct);
        });
        Assert.NotNull(failure);
        Assert.IsNotType<TimeoutException>(failure);
        Assert.False(ct.IsCancellationRequested);

        // The native work was actually interrupted, not merely a rejected
        // response after a still-running 60-second SELECT.
        await WaitForBackendNotExecutingAsync(witness, backend, ct);
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(witness, "SELECT 1", ct)));
    }

    [Fact]
    public async Task Dml_PhysicalDisconnectDuringNativeCommit_IsUnknownAndCannotReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = "hsql_commit_kill_" + Guid.NewGuid().ToString("N")[..12];
        var target = fixture.ConnectionString(app);
        var schema = "dmlfault_" + Guid.NewGuid().ToString("N")[..10];
        await using var witness = new NpgsqlConnection(fixture.ConnectionString("hsql_commit_witness"));
        await witness.OpenAsync(ct);

        await ExecuteAsync(witness, "CREATE SCHEMA " + schema, ct);
        await ExecuteAsync(witness,
            "CREATE TABLE " + schema + ".parents (id integer primary key)", ct);
        await ExecuteAsync(witness,
            "INSERT INTO " + schema + ".parents VALUES (1)", ct);
        // This FK check occurs *at COMMIT*. A concurrent FOR UPDATE holds the
        // parent row so the real commit is seen waiting in pg_stat_activity.
        await ExecuteAsync(witness,
            "CREATE TABLE " + schema + ".items (id integer primary key, " +
            "parent_id integer REFERENCES " + schema + ".parents(id) " +
            "DEFERRABLE INITIALLY DEFERRED)", ct);

        var runtime = new TypedDmlRuntime();
        var actor = new DmlApprovalExecutionContext(
            "mcp-key:7", "db-management:42",
            SqlAgentToolType.Postgres, witness.Database);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            schema + ".items"
        };
        var sql = "INSERT INTO " + schema + ".items(id,parent_id) VALUES(100,1);" +
                  "INSERT INTO " + schema + ".items(id,parent_id) VALUES(101,1)";
        var policy = new SecurityPolicyModel
        {
            DmlMaxAffectedRows = 16,
            RequireWhereForUpdate = true,
            RequireWhereForDelete = true
        };
        var provider = new PostgresProvider();
        var parsed = await runtime.ParseDmlBatchWithVerifiedRuntimeProfileAsync(
            provider, target, sql, SqlAgentToolType.Postgres, ct);
        var session = await runtime.PreviewTransactionAsync(
            provider, target, parsed, policy, allowed, actor, ct);

        await using var blocker = new NpgsqlConnection(fixture.ConnectionString("hsql_commit_blocker"));
        await blocker.OpenAsync(ct);
        await using var lockTransaction = await blocker.BeginTransactionAsync(ct);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT id FROM " + schema + ".parents WHERE id=1 FOR UPDATE",
            blocker, lockTransaction))
            Assert.Equal(1, Convert.ToInt32(await lockCommand.ExecuteScalarAsync(ct)));

        Task<DmlCommitResult> committing = runtime.CommitTransactionAsync(
            provider, target, session, policy, allowed, actor, ct);
        try
        {
            // pg_stat_activity witnesses a native COMMIT blocked by the
            // deferred FK; no client mock or post-commit injected exception.
            var backend = await WaitForExecutingBackendAsync(
                witness, app, "COMMIT", requireCommit: true, ct);
            Assert.False(committing.IsCompleted,
                "The native COMMIT must still be in flight before TCP termination.");
            Assert.True(await TerminateBackendAsync(witness, backend, ct));
            var failure = await Record.ExceptionAsync(async () =>
            {
                _ = await committing.WaitAsync(TimeSpan.FromSeconds(20), ct);
            });
            Assert.IsType<DmlCommitOutcomeUnknownException>(failure);
        }
        finally
        {
            await lockTransaction.RollbackAsync(CancellationToken.None);
        }

        // The real server killed the backend during COMMIT, and its transaction
        // rolled back. The application intentionally reports UNKNOWN rather
        // than claiming success or a definitely rolled-back mutation.
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(
            witness, "SELECT count(*) FROM " + schema + ".items", ct)));
        var replay = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.CommitTransactionAsync(
                provider, target, session, policy, allowed, actor, ct));
        Assert.Contains("consumed", replay.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(
            witness, "SELECT count(*) FROM " + schema + ".items", ct)));
    }

    private static SecurityPolicyModel QueryPolicy() => new()
    {
        QueryMaxRows = 20,
        QueryTimeoutSeconds = 90
    };

    private static async Task<int> WaitForExecutingBackendAsync(
        NpgsqlConnection witness,
        string application,
        string queryFragment,
        bool requireCommit,
        CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT pid
                FROM pg_catalog.pg_stat_activity
                WHERE application_name = @app
                  AND state = 'active'
                  AND pid <> pg_catalog.pg_backend_pid()
                  AND (CASE WHEN @commit THEN
                        query ~* '^COMMIT'
                      ELSE query ILIKE '%' || @fragment || '%' END)
                  AND (NOT @commit OR wait_event_type = 'Lock')
                LIMIT 1
                """, witness);
            cmd.Parameters.AddWithValue("app", application);
            cmd.Parameters.AddWithValue("commit", requireCommit);
            cmd.Parameters.AddWithValue("fragment", queryFragment);
            var pid = await cmd.ExecuteScalarAsync(ct);
            if (pid is int id)
                return id;
            await Task.Delay(100, ct);
        }
        throw new TimeoutException(
            "The PostgreSQL backend never reached the requested live native " +
            (requireCommit ? "COMMIT lock wait" : "SQL command") +
            "; refusing to count a simulated failure as physical proof.");
    }

    private static async Task WaitForBackendNotExecutingAsync(
        NpgsqlConnection witness, int backend, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT count(*) FROM pg_catalog.pg_stat_activity
                WHERE pid = @pid AND state = 'active'
                  AND query ILIKE '%pg_sleep%'
                """, witness);
            cmd.Parameters.AddWithValue("pid", backend);
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 0)
                return;
            await Task.Delay(100, ct);
        }
        Assert.Fail("Cancelled native SQL is still running on the physical backend.");
    }

    private static async Task<bool> TerminateBackendAsync(
        NpgsqlConnection witness, int backend, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_catalog.pg_terminate_backend(@pid)", witness);
        cmd.Parameters.AddWithValue("pid", backend);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        return await cmd.ExecuteScalarAsync(ct);
    }
}
