using System.Data;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL 16: an actual server-side backend termination during a deferred
/// constraint trigger inside native COMMIT, not an exception injected by a
/// simulated ADO.NET connection decorator.
/// </summary>
public sealed class DmlNativePostgresCommitDisconnectTests
{
    [Fact]
    public async Task NativePostgresBackendTerminatedDuringCommit_ReportsUnknownWithoutCommittedRows()
    {
        var source = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(source))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Native PostgreSQL COMMIT disconnect proof requires TEST_POSTGRES_CONNECTION.");
            Assert.Skip("Requires a real PostgreSQL 16 server.");
        }

        var ct = TestContext.Current.CancellationToken;
        var connectionString = new NpgsqlConnectionStringBuilder(source)
        {
            Pooling = false
        }.ConnectionString;
        var schema = "nativecommit_" + Guid.NewGuid().ToString("N")[..14];
        await using var controller = new NpgsqlConnection(connectionString);
        await controller.OpenAsync(ct);
        try
        {
            await ExecAsync(controller, "CREATE SCHEMA " + schema, ct);
            await ExecAsync(controller,
                "CREATE TABLE " + schema + ".items(id INTEGER PRIMARY KEY,note TEXT NOT NULL)", ct);
            // A PostgreSQL constraint trigger can defer physical work until
            // COMMIT. This creates a deterministic window to terminate the
            // native backend *during* COMMIT while it is still unacknowledged.
            await ExecAsync(controller, $"""
                CREATE FUNCTION {schema}.pause_at_commit() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM pg_sleep(20);
                    RETURN NEW;
                END
                $$
                """, ct);
            await ExecAsync(controller, $"""
                CREATE CONSTRAINT TRIGGER pause_commit
                AFTER INSERT ON {schema}.items
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION {schema}.pause_at_commit()
                """, ct);

            await using var worker = new NpgsqlConnection(connectionString);
            await worker.OpenAsync(ct);
            await using var transaction =
                await worker.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await using (var insert = new NpgsqlCommand(
                $"INSERT INTO {schema}.items(id,note) VALUES (101,'approved')", worker, (NpgsqlTransaction)transaction))
                await insert.ExecuteNonQueryAsync(ct);

            long backendPid;
            await using (var pid = new NpgsqlCommand("SELECT pg_backend_pid()", worker, (NpgsqlTransaction)transaction))
                backendPid = Convert.ToInt64(await pid.ExecuteScalarAsync(ct));

            var pendingCommit = DmlTransactionFailureRecovery.CommitAsync(transaction, ct);
            try
            {
                // Observe PostgreSQL 16's real COMMIT session stalled *inside*
                // the deferred trigger, not a client-side delayed mock ACK.
                var reachedCommit = false;
                using var observationDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                observationDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                while (!observationDeadline.IsCancellationRequested)
                {
                    await using var observe = new NpgsqlCommand("""
                        SELECT count(*) FROM pg_stat_activity
                        WHERE pid = @pid AND state = 'active'
                          AND query = 'COMMIT' AND wait_event = 'PgSleep'
                        """, controller);
                    observe.Parameters.AddWithValue("pid", (int)backendPid);
                    if (Convert.ToInt64(await observe.ExecuteScalarAsync(
                            observationDeadline.Token)) == 1)
                    {
                        reachedCommit = true;
                        break;
                    }
                    await Task.Delay(50, observationDeadline.Token);
                }

                Assert.True(reachedCommit, "Target COMMIT never reached PostgreSQL's deferred trigger.");

                await using (var terminate = new NpgsqlCommand(
                    "SELECT pg_terminate_backend(@pid)", controller))
                {
                    terminate.Parameters.AddWithValue("pid", (int)backendPid);
                    Assert.True((bool)(await terminate.ExecuteScalarAsync(ct))!);
                }

                var unknown = await Assert.ThrowsAsync<DmlCommitOutcomeUnknownException>(
                    () => pendingCommit.WaitAsync(TimeSpan.FromSeconds(12), ct));
                Assert.NotNull(unknown.InnerException);
                await DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync(
                    transaction, unknown);
                // A failed attempt to roll back after native disconnect must
                // never downgrade unknown COMMIT to a certain failure.
                DmlTransactionFailureRecovery.ThrowIfRollbackUnconfirmed(unknown);

                await using var independentWitness = new NpgsqlConnection(connectionString);
                await independentWitness.OpenAsync(ct);
                Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(independentWitness,
                    "SELECT count(*) FROM " + schema + ".items", ct)));
            }
            finally
            {
                // Avoid stranding a 20-second server-side trigger if an earlier
                // assertion fails before the intentional transport cut.
                if (!pendingCommit.IsCompleted)
                {
                    await using var cleanup = new NpgsqlCommand(
                        "SELECT pg_terminate_backend(@pid)", controller);
                    cleanup.Parameters.AddWithValue("pid", (int)backendPid);
                    await cleanup.ExecuteScalarAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            await ExecAsync(controller,
                "DROP SCHEMA IF EXISTS " + schema + " CASCADE",
                CancellationToken.None);
        }
    }
    private static async Task ExecAsync(NpgsqlConnection db, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection db, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, db);
        return await command.ExecuteScalarAsync(token);
    }

}
