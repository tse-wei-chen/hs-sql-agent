using System.Buffers.Binary;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HsSqlAgent.SqlCore.Enums;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real PostgreSQL 16 TCP fault: the server has ALREADY completed COMMIT and
/// sent CommandComplete("COMMIT"), but a transparent proxy drops that message
/// before it reaches Npgsql. This differs fundamentally from terminating
/// the server backend *during* COMMIT or throwing after CommitAsync succeeds.
/// </summary>
public sealed class DmlNativePostgresPostCommitAckLossTests
{
    [Fact]
    public async Task NativeCommitAlreadySucceeded_ButClientWireReplyLost_TargetWitnessAndFenceStayCommitted()
    {
        var source = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(source))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Post-COMMIT TCP ACK loss requires real PostgreSQL 16.");
            Assert.Skip("Requires an actual PostgreSQL 16 service.");
        }

        var ct = TestContext.Current.CancellationToken;
        var schema = "ackdrop_" + Guid.NewGuid().ToString("N")[..16];
        var requestId = "req-" + Guid.NewGuid().ToString("N");
        var evidence = new DmlTargetCommitWitnessEvidence(requestId, new string('A', 64));
        var direct = new NpgsqlConnectionStringBuilder(source)
        {
            Pooling = false,
            SslMode = SslMode.Disable,
            SearchPath = schema,
            CommandTimeout = 10
        }.ConnectionString;
        var serverPort = new NpgsqlConnectionStringBuilder(source).Port;

        await using var administrator = new NpgsqlConnection(direct);
        await administrator.OpenAsync(ct);
        try
        {
            await ExecuteAsync(administrator, "CREATE SCHEMA " + schema, ct);
            await ExecuteAsync(administrator, $"""
                CREATE TABLE {schema}.items
                    (id INTEGER PRIMARY KEY, note TEXT NOT NULL);
                CREATE TABLE {schema}.HS_SQL_AGENT_DML_WITNESS
                    (REQUEST_ID VARCHAR(64) PRIMARY KEY,
                     EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL);
                CREATE TABLE {schema}.HS_SQL_AGENT_DML_FENCE
                    (REQUEST_ID VARCHAR(64) PRIMARY KEY,
                     EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL,
                     ENTRY_KIND VARCHAR(16) NOT NULL);
                """, ct);

            await using var proxy = new DropPostgresCommitCommandCompleteProxy(serverPort);
            var throughProxy = new NpgsqlConnectionStringBuilder(direct)
            {
                Host = "127.0.0.1",
                Port = proxy.Port
            }.ConnectionString;
            var fence = new RelationalDmlTargetExecutionFence();
            var witness = new RelationalDmlTargetCommitWitness();

            // The *real* application mutation, admitted Fence, and Witness
            // share one actual PostgreSQL SERIALIZABLE target transaction.
            await using (var worker = new NpgsqlConnection(throughProxy))
            {
                await worker.OpenAsync(ct);
                await using var transaction = await worker.BeginTransactionAsync(
                    IsolationLevel.Serializable, ct);

                await fence.AdmitAsync(worker, transaction, SqlAgentToolType.Postgres,
                    evidence, ct);
                await using (var insert = new NpgsqlCommand(
                    "INSERT INTO items(id,note) VALUES (901,'physical-native-commit')",
                    worker, transaction))
                    Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
                await witness.WriteAsync(worker, transaction,
                    SqlAgentToolType.Postgres, evidence, ct);

                // The proxy lets ALL traffic pass through until it sees the
                // actual PostgreSQL server reply CommandComplete("COMMIT").
                // It then drops that server reply and closes the client TCP
                // connection. The database has already committed, but the
                // application has no acknowledgement.
                proxy.ArmForCommitReplyLoss();
                var unknown = await Assert.ThrowsAsync<DmlCommitOutcomeUnknownException>(
                    () => DmlTransactionFailureRecovery.CommitAsync(transaction, ct)
                        .WaitAsync(TimeSpan.FromSeconds(12), ct));
                Assert.NotNull(unknown.InnerException);
                await proxy.DroppedServerCommitReply.WaitAsync(TimeSpan.FromSeconds(5), ct);
                Assert.True(proxy.ServerReportedNativeCommit);
                Assert.Contains("must not be replayed", unknown.Message,
                    StringComparison.OrdinalIgnoreCase);
                // Never downgrade a COMMIT-unknown classification because
                // subsequent best-effort ROLLBACK sees a broken connection.
                await DmlTransactionFailureRecovery.RollbackWithoutMaskingAsync(
                    transaction, unknown);
                DmlTransactionFailureRecovery.ThrowIfRollbackUnconfirmed(unknown);
            }

            // Separate physical target connection, *not* the proxy's report,
            // witnesses the durable business row AND both enlisted evidence
            // records after the original client socket has died.
            await using (var independent = new NpgsqlConnection(direct))
            {
                await independent.OpenAsync(ct);
                Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
                    independent, "SELECT count(*) FROM items WHERE id = 901", ct)));
                var admitted = await fence.ReadAsync(independent,
                    SqlAgentToolType.Postgres, requestId, ct);
                Assert.NotNull(admitted);
                Assert.Equal(RelationalDmlTargetExecutionFence.AdmissionKind,
                    admitted.EntryKind);
                Assert.Equal(evidence.EvidenceFingerprint, admitted.EvidenceFingerprint);
                Assert.Equal(evidence.EvidenceFingerprint,
                    await witness.ReadFingerprintAsync(independent,
                        SqlAgentToolType.Postgres, requestId, ct));
            }

            // Even if a second process incorrectly attempts to reuse the same
            // consumed approval, the target's permanent unique request fence
            // rejects admission before any second business mutation.
            await using (var replay = new NpgsqlConnection(direct))
            {
                await replay.OpenAsync(ct);
                await using var replayTx = await replay.BeginTransactionAsync(ct);
                var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                    fence.AdmitAsync(replay, replayTx,
                        SqlAgentToolType.Postgres, evidence, ct));
                Assert.Equal("23505", denied.SqlState);
            }
            await using (var verify = new NpgsqlConnection(direct))
            {
                await verify.OpenAsync(ct);
                Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
                    verify, "SELECT count(*) FROM items", ct)));
            }
        }
        finally
        {
            await ExecuteAsync(administrator,
                "DROP SCHEMA IF EXISTS " + schema + " CASCADE",
                CancellationToken.None);
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(token);
    }

    /// <summary>
    /// A byte-preserving PostgreSQL protocol proxy with precisely one fault:
    /// on the armed native COMMIT, suppress the backend CommandComplete frame
    /// (tag C, payload COMMIT-NUL) and disconnect its client. Prior frames
    /// including authentication, server parameters, queries, writes, and
    /// deferred transaction commands reach the real PostgreSQL 16 unchanged.
    /// TCP is intentionally unencrypted for inspection, ONLY on loopback.
    /// </summary>
    private sealed class DropPostgresCommitCommandCompleteProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly TaskCompletionSource _dropped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _pump;
        private readonly int _upstreamPort;
        private int _armed;
        private int _serverCommitted;

        public DropPostgresCommitCommandCompleteProxy(int upstreamPort)
        {
            _upstreamPort = upstreamPort;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _pump = PumpAsync();
        }

        public int Port { get; }
        public bool ServerReportedNativeCommit => Volatile.Read(ref _serverCommitted) == 1;
        public Task DroppedServerCommitReply => _dropped.Task;
        public void ArmForCommitReplyLoss() => Interlocked.Exchange(ref _armed, 1);

        private async Task PumpAsync()
        {
            try
            {
                using var downstream = await _listener.AcceptTcpClientAsync(_stopping.Token);
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(IPAddress.Loopback, _upstreamPort, _stopping.Token);
                var toServer = downstream.GetStream().CopyToAsync(
                    upstream.GetStream(), _stopping.Token);
                try
                {
                    await PipePostgresServerFramesAsync(
                        upstream.GetStream(), downstream.GetStream());
                }
                finally
                {
                    downstream.Close();
                    upstream.Close();
                    try { await toServer; }
                    catch (Exception ex) when (ex is IOException
                        or ObjectDisposedException or OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (Exception failure)
            {
                _dropped.TrySetException(failure);
            }
        }

        private async Task PipePostgresServerFramesAsync(Stream fromServer, Stream toClient)
        {
            var header = new byte[5];
            while (!_stopping.IsCancellationRequested)
            {
                await fromServer.ReadExactlyAsync(header, _stopping.Token);
                var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                if (size is < 4 or > 16_777_216)
                    throw new InvalidDataException("Invalid native PostgreSQL server frame length.");
                var payload = new byte[size - 4];
                await fromServer.ReadExactlyAsync(payload, _stopping.Token);

                if (Volatile.Read(ref _armed) == 1
                    && header[0] == (byte)'C'
                    && payload.AsSpan().SequenceEqual("COMMIT\0"u8))
                {
                    // Real PostgreSQL has confirmed the transaction's COMMIT
                    // to the proxy. Do not forward even a byte of this reply
                    // to Npgsql: the client must see a transport EOF instead.
                    Volatile.Write(ref _serverCommitted, 1);
                    _dropped.TrySetResult();
                    return;
                }

                await toClient.WriteAsync(header, _stopping.Token);
                await toClient.WriteAsync(payload, _stopping.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            _listener.Stop();
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            _stopping.Dispose();
        }
    }
}
