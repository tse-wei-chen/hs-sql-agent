using System.Data;
using System.Data.Common;
using HsSqlAgent.SqlCore.Enums;
using MySql.Data.MySqlClient;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real database proof for the target-side unique-index admission/revocation
/// protocol. PostgreSQL and MySQL are provisioned as CI services, not mocks.
/// Every case uses independent physical connections and transaction boundaries.
/// </summary>
public sealed class DmlTargetFenceLiveProviderIntegrationTests
{
    private static readonly RelationalDmlTargetExecutionFence Fence = new();
    private static readonly RelationalDmlTargetCommitWitness Witness = new();

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    public async Task RevocationFirst_PreventsLateWorkerFromStartingSql(SqlAgentToolType provider)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(provider, ct);
        var evidence = Evidence("revocation-first");
        await using var revoker = await target.OpenAsync(ct);
        await Fence.RevokeAsync(revoker, provider, evidence, ct);

        await using var worker = await target.OpenAsync(ct);
        await using var tx = await worker.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await Assert.ThrowsAnyAsync<DbException>(() =>
            Fence.AdmitAsync(worker, tx, provider, evidence, ct));
        await tx.RollbackAsync(ct);

        Assert.Equal(RelationalDmlTargetExecutionFence.RevocationKind,
            (await Fence.ReadAsync(revoker, provider, evidence.RequestId, ct))?.EntryKind);
        Assert.Null(await Witness.ReadFingerprintAsync(revoker, provider, evidence.RequestId, ct));
        Assert.Equal(0L, await CountRowsAsync(revoker, ct));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    public async Task WorkerFirst_EnlistsAdmissionWitnessAndSql_RevocationMustFail(SqlAgentToolType provider)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(provider, ct);
        var evidence = Evidence("worker-first");
        await using (var worker = await target.OpenAsync(ct))
        await using (var tx = await worker.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            await Fence.AdmitAsync(worker, tx, provider, evidence, ct);
            await InsertRowAsync(worker, tx, 1, ct);
            await Witness.WriteAsync(worker, tx, provider, evidence, ct);
            await tx.CommitAsync(ct);
        }

        await using var revoker = await target.OpenAsync(ct);
        await Assert.ThrowsAnyAsync<DbException>(() =>
            Fence.RevokeAsync(revoker, provider, evidence, ct));

        var admission = await Fence.ReadAsync(revoker, provider, evidence.RequestId, ct);
        Assert.Equal(RelationalDmlTargetExecutionFence.AdmissionKind, admission?.EntryKind);
        Assert.Equal(evidence.EvidenceFingerprint, admission?.EvidenceFingerprint);
        Assert.Equal(evidence.EvidenceFingerprint,
            await Witness.ReadFingerprintAsync(revoker, provider, evidence.RequestId, ct));
        Assert.Equal(1L, await CountRowsAsync(revoker, ct));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    public async Task RolledBackWorker_ReleasesAdmissionAndWitness_RevocationCanWin(SqlAgentToolType provider)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(provider, ct);
        var evidence = Evidence("rollback");
        await using (var worker = await target.OpenAsync(ct))
        await using (var tx = await worker.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            await Fence.AdmitAsync(worker, tx, provider, evidence, ct);
            await InsertRowAsync(worker, tx, 2, ct);
            await Witness.WriteAsync(worker, tx, provider, evidence, ct);
            await tx.RollbackAsync(ct);
        }

        await using var revoker = await target.OpenAsync(ct);
        Assert.Null(await Fence.ReadAsync(revoker, provider, evidence.RequestId, ct));
        Assert.Null(await Witness.ReadFingerprintAsync(revoker, provider, evidence.RequestId, ct));
        await Fence.RevokeAsync(revoker, provider, evidence, ct);
        Assert.Equal(RelationalDmlTargetExecutionFence.RevocationKind,
            (await Fence.ReadAsync(revoker, provider, evidence.RequestId, ct))?.EntryKind);
        Assert.Equal(0L, await CountRowsAsync(revoker, ct));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    public async Task MissingWitnessTable_RollsBackAdmittedDmlWithoutLeavingEvidence(
        SqlAgentToolType provider)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(provider, ct);
        var evidence = Evidence("missing-witness");
        await using (var setup = await target.OpenAsync(ct))
        {
            await using var drop = setup.CreateCommand();
            drop.CommandText = "DROP TABLE HS_SQL_AGENT_DML_WITNESS";
            await drop.ExecuteNonQueryAsync(ct);
        }

        await using (var worker = await target.OpenAsync(ct))
        await using (var tx = await worker.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            await Fence.AdmitAsync(worker, tx, provider, evidence, ct);
            await InsertRowAsync(worker, tx, 4, ct);
            await Assert.ThrowsAnyAsync<DbException>(() =>
                Witness.WriteAsync(worker, tx, provider, evidence, ct));
            await tx.RollbackAsync(ct);
        }

        await using var revoker = await target.OpenAsync(ct);
        Assert.Null(await Fence.ReadAsync(revoker, provider, evidence.RequestId, ct));
        Assert.Equal(0L, await CountRowsAsync(revoker, ct));
        await Fence.RevokeAsync(revoker, provider, evidence, ct);
        Assert.Equal(RelationalDmlTargetExecutionFence.RevocationKind,
            (await Fence.ReadAsync(revoker, provider, evidence.RequestId, ct))?.EntryKind);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    public async Task ConcurrentRevocation_CannotAcknowledgeWhileAdmissionUncommitted(
        SqlAgentToolType provider)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(provider, ct);
        var evidence = Evidence("concurrent");
        await using var worker = await target.OpenAsync(ct);
        await using var tx = await worker.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await Fence.AdmitAsync(worker, tx, provider, evidence, ct);
        await InsertRowAsync(worker, tx, 3, ct);
        await Witness.WriteAsync(worker, tx, provider, evidence, ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var reachedRevocation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingRevocation = Task.Run(async () =>
        {
            await using var revoker = await target.OpenAsync(deadline.Token);
            reachedRevocation.TrySetResult();
            await Fence.RevokeAsync(revoker, provider, evidence, deadline.Token);
        }, ct);
        await reachedRevocation.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        // The INSERT competes for the same unique target request ID. A
        // revocation cannot report success while the worker holds admission.
        await Task.Delay(200, ct);
        Assert.False(pendingRevocation.IsCompleted,
            "Revocation must not acknowledge while the admitted worker's target transaction is uncommitted.");

        await tx.CommitAsync(ct);
        await Assert.ThrowsAnyAsync<DbException>(() => pendingRevocation.WaitAsync(
            TimeSpan.FromSeconds(12), ct));

        await using var verifier = await target.OpenAsync(ct);
        Assert.Equal(RelationalDmlTargetExecutionFence.AdmissionKind,
            (await Fence.ReadAsync(verifier, provider, evidence.RequestId, ct))?.EntryKind);
        Assert.Equal(evidence.EvidenceFingerprint,
            await Witness.ReadFingerprintAsync(verifier, provider, evidence.RequestId, ct));
        Assert.Equal(1L, await CountRowsAsync(verifier, ct));
    }

    private static DmlTargetCommitWitnessEvidence Evidence(string scenario) =>
        new("hsa-live-fence-" + scenario, new string('A', 64));

    private static async Task InsertRowAsync(DbConnection connection, DbTransaction transaction,
        int id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO HS_SQL_AGENT_DML_PROBE (ID) VALUES (@id)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.DbType = DbType.Int32;
        parameter.Value = id;
        command.Parameters.Add(parameter);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
    }

    private static async Task<long> CountRowsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM HS_SQL_AGENT_DML_PROBE";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private sealed class Target(SqlAgentToolType provider, string rootConnectionString,
        string isolatedConnectionString, string name) : IAsyncDisposable
    {
        public static async Task<Target> CreateAsync(SqlAgentToolType provider, CancellationToken ct)
        {
            var envName = provider switch
            {
                SqlAgentToolType.Postgres => "TEST_POSTGRES_CONNECTION",
                SqlAgentToolType.MySQL => "TEST_MYSQL_CONNECTION",
                _ => throw new ArgumentOutOfRangeException(nameof(provider))
            };
            var rootConnectionString = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(rootConnectionString))
            {
                if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(envName + " must be configured for real fencing parity in CI.");
                Assert.Skip("Live provider fencing requires " + envName);
            }

            var name = "hsa_fence_" + Guid.NewGuid().ToString("N");
            string isolatedConnectionString;
            if (provider == SqlAgentToolType.Postgres)
            {
                isolatedConnectionString = new NpgsqlConnectionStringBuilder(rootConnectionString)
                {
                    SearchPath = name,
                    Pooling = false
                }.ConnectionString;
            }
            else
            {
                isolatedConnectionString = new MySqlConnectionStringBuilder(rootConnectionString)
                {
                    Database = name,
                    Pooling = false
                }.ConnectionString;
            }

            await using (var admin = CreateConnection(provider, rootConnectionString))
            {
                await admin.OpenAsync(ct);
                await ExecuteAsync(admin, provider == SqlAgentToolType.Postgres
                    ? "CREATE SCHEMA " + name
                    : "CREATE DATABASE " + name, ct);
            }

            var target = new Target(provider, rootConnectionString, isolatedConnectionString, name);
            try
            {
                await using var db = await target.OpenAsync(ct);
                if (provider == SqlAgentToolType.MySQL)
                {
                    // The production fence contract requires a transactional
                    // engine and binary request-ID collation, not the server's
                    // possibly case-insensitive database default.
                    await ExecuteAsync(db,
                        "CREATE TABLE HS_SQL_AGENT_DML_FENCE (" +
                        "REQUEST_ID VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY, " +
                        "EVIDENCE_FINGERPRINT CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL, " +
                        "ENTRY_KIND VARCHAR(16) NOT NULL) ENGINE=InnoDB", ct);
                    await ExecuteAsync(db,
                        "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
                        "REQUEST_ID VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY, " +
                        "EVIDENCE_FINGERPRINT CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL) ENGINE=InnoDB", ct);
                    await ExecuteAsync(db,
                        "CREATE TABLE HS_SQL_AGENT_DML_PROBE (ID INTEGER PRIMARY KEY) ENGINE=InnoDB", ct);
                }
                else
                {
                    await ExecuteAsync(db,
                        "CREATE TABLE HS_SQL_AGENT_DML_FENCE (" +
                        "REQUEST_ID VARCHAR(64) PRIMARY KEY, " +
                        "EVIDENCE_FINGERPRINT CHAR(64) NOT NULL, " +
                        "ENTRY_KIND VARCHAR(16) NOT NULL " +
                        "CHECK (ENTRY_KIND IN ('Admitted','Revoked')))", ct);
                    await ExecuteAsync(db,
                        "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
                        "REQUEST_ID VARCHAR(64) PRIMARY KEY, EVIDENCE_FINGERPRINT CHAR(64) NOT NULL)", ct);
                    await ExecuteAsync(db,
                        "CREATE TABLE HS_SQL_AGENT_DML_PROBE (ID INTEGER PRIMARY KEY)", ct);
                }
                return target;
            }
            catch
            {
                await target.DisposeAsync();
                throw;
            }
        }

        public async Task<DbConnection> OpenAsync(CancellationToken ct)
        {
            var connection = CreateConnection(provider, isolatedConnectionString);
            try
            {
                await connection.OpenAsync(ct);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = CreateConnection(provider, rootConnectionString);
            await admin.OpenAsync(CancellationToken.None);
            await ExecuteAsync(admin, provider == SqlAgentToolType.Postgres
                ? "DROP SCHEMA IF EXISTS " + name + " CASCADE"
                : "DROP DATABASE IF EXISTS " + name, CancellationToken.None);
        }

        private static DbConnection CreateConnection(SqlAgentToolType provider, string cs) =>
            provider == SqlAgentToolType.Postgres
                ? new NpgsqlConnection(cs)
                : new MySqlConnection(cs);

        private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
