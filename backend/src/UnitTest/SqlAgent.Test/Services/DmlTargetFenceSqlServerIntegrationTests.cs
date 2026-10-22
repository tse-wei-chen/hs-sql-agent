using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using HsSqlAgent.SqlCore.Enums;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Actual SQL Server transaction and unique-key fencing parity. The isolated
/// databases, independent connections and COMMIT/ROLLBACK boundaries are real.
/// The CI job MUST supply TEST_SQLSERVER_CONNECTION; local runs may skip.
/// </summary>
public sealed class DmlTargetFenceSqlServerIntegrationTests
{
    private const SqlAgentToolType Provider = SqlAgentToolType.MsSqlServer;
    private static readonly RelationalDmlTargetExecutionFence Fence = new();
    private static readonly RelationalDmlTargetCommitWitness Witness = new();

    [Fact]
    public async Task LegacyWorkerCutover_RequiresRealGrantRemovalAndSessionDrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        var login = "hsa_old_" + Guid.NewGuid().ToString("N")[..15];
        const string password = "LegacyTestOnly#1234!";
        var verifier = new SqlServerLegacyDmlCutoverVerifier();
        var rootCs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION")!;
        await using var root = new SqlConnection(rootCs);
        await root.OpenAsync(ct);
        await ExecuteAsync(root,
            $"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF", ct);
        try
        {
            await using var admin = await target.OpenAsync(ct);
            await ExecuteAsync(admin,
                $"CREATE USER [{login}] FOR LOGIN [{login}]; " +
                $"GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.hs_sql_agent_dml_probe TO [{login}]", ct);
            try
            {
                var grant = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    verifier.VerifyAsync(admin, login, ct));
                Assert.Contains("effective DML", grant.Message);

                // A db_datawriter role is a second, inherited write path,
                // even after direct grants are removed.
                await ExecuteAsync(admin,
                    $"REVOKE INSERT, UPDATE, DELETE ON dbo.hs_sql_agent_dml_probe FROM [{login}]; " +
                    $"ALTER ROLE [db_datawriter] ADD MEMBER [{login}]", ct);
                var inherited = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    verifier.VerifyAsync(admin, login, ct));
                Assert.Contains("effective DML", inherited.Message);
                await ExecuteAsync(admin,
                    $"ALTER ROLE [db_datawriter] DROP MEMBER [{login}]", ct);

                var oldLoginCs = new SqlConnectionStringBuilder(target.ConnectionString)
                {
                    UserID = login, Password = password, Pooling = false
                }.ConnectionString;
                await using (var oldSession = new SqlConnection(oldLoginCs))
                {
                    await oldSession.OpenAsync(ct);
                    var live = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        verifier.VerifyAsync(admin, login, ct));
                    Assert.Contains("activeSessions=", live.Message);
                }

                var result = await verifier.VerifyAsync(admin, login, ct);
                Assert.Equal(login, result.LegacyLogin);
                Assert.True(result.CheckedRelations > 0);
            }
            finally
            {
                await ExecuteAsync(admin, $"DROP USER IF EXISTS [{login}]", ct);
            }
        }
        finally
        {
            await ExecuteAsync(root, $"DROP LOGIN IF EXISTS [{login}]", CancellationToken.None);
        }
    }

    [Fact]
    public async Task LegacyWorkerCutover_RejectsUnknownOrUnprivilegedInspector()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        await using var admin = await target.OpenAsync(ct);
        var verifier = new SqlServerLegacyDmlCutoverVerifier();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(admin, "missing_legacy_worker", ct));

        // The verifier must never accept an ordinary SQL login's restricted
        // view of sys.dm_exec_sessions as if it were cluster-wide.
        var login = "hsa_restricted_" + Guid.NewGuid().ToString("N")[..12];
        const string password = "LegacyTestOnly#1234!";
        await using var root = new SqlConnection(
            Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION")!);
        await root.OpenAsync(ct);
        await ExecuteAsync(root,
            $"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF", ct);
        try
        {
            await ExecuteAsync(admin, $"CREATE USER [{login}] FOR LOGIN [{login}]", ct);
            try
            {
                var cs = new SqlConnectionStringBuilder(target.ConnectionString)
                {
                    UserID = login, Password = password, Pooling = false
                }.ConnectionString;
                await using var unprivileged = new SqlConnection(cs);
                await unprivileged.OpenAsync(ct);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    verifier.VerifyAsync(unprivileged, login, ct));
                Assert.Contains("sysadmin-owned", error.Message);
            }
            finally { await ExecuteAsync(admin, $"DROP USER IF EXISTS [{login}]", ct); }
        }
        finally { await ExecuteAsync(root, $"DROP LOGIN IF EXISTS [{login}]", CancellationToken.None); }
    }

    [Fact]
    public async Task RevocationFirst_RejectsLateWorkerBeforeMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        var evidence = Evidence("revoked-first");
        await using var revoker = await target.OpenAsync(ct);
        await Fence.RevokeAsync(revoker, Provider, evidence, ct);

        await using var worker = await target.OpenAsync(ct);
        await using var tx = await worker.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await Assert.ThrowsAnyAsync<DbException>(() =>
            Fence.AdmitAsync(worker, tx, Provider, evidence, ct));
        await tx.RollbackAsync(ct);

        Assert.Equal(RelationalDmlTargetExecutionFence.RevocationKind,
            (await Fence.ReadAsync(revoker, Provider, evidence.RequestId, ct))?.EntryKind);
        Assert.Null(await Witness.ReadFingerprintAsync(revoker, Provider, evidence.RequestId, ct));
        Assert.Equal(0L, await CountRowsAsync(revoker, ct));
    }

    [Fact]
    public async Task WorkerFirst_CommitsAdmissionSqlAndWitnessTogether()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        var evidence = Evidence("worker-first");
        await using (var worker = await target.OpenAsync(ct))
        await using (var tx = await worker.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            await Fence.AdmitAsync(worker, tx, Provider, evidence, ct);
            await InsertRowAsync(worker, tx, 1, ct);
            await Witness.WriteAsync(worker, tx, Provider, evidence, ct);
            await tx.CommitAsync(ct);
        }

        await using var revoker = await target.OpenAsync(ct);
        await Assert.ThrowsAnyAsync<DbException>(() =>
            Fence.RevokeAsync(revoker, Provider, evidence, ct));
        Assert.Equal(RelationalDmlTargetExecutionFence.AdmissionKind,
            (await Fence.ReadAsync(revoker, Provider, evidence.RequestId, ct))?.EntryKind);
        Assert.Equal(evidence.EvidenceFingerprint,
            await Witness.ReadFingerprintAsync(revoker, Provider, evidence.RequestId, ct));
        Assert.Equal(1L, await CountRowsAsync(revoker, ct));
    }

    [Fact]
    public async Task RolledBackWorker_ReleasesAdmissionAndWitness()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        var evidence = Evidence("rolled-back");
        await using (var worker = await target.OpenAsync(ct))
        await using (var tx = await worker.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            await Fence.AdmitAsync(worker, tx, Provider, evidence, ct);
            await InsertRowAsync(worker, tx, 2, ct);
            await Witness.WriteAsync(worker, tx, Provider, evidence, ct);
            await tx.RollbackAsync(ct);
        }

        await using var revoker = await target.OpenAsync(ct);
        Assert.Null(await Fence.ReadAsync(revoker, Provider, evidence.RequestId, ct));
        Assert.Null(await Witness.ReadFingerprintAsync(revoker, Provider, evidence.RequestId, ct));
        Assert.Equal(0L, await CountRowsAsync(revoker, ct));
        await Fence.RevokeAsync(revoker, Provider, evidence, ct);
        Assert.Equal(RelationalDmlTargetExecutionFence.RevocationKind,
            (await Fence.ReadAsync(revoker, Provider, evidence.RequestId, ct))?.EntryKind);
    }

    [Fact]
    public async Task MissingWitnessTable_RollsBackApplicationRowAndAdmission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        var evidence = Evidence("no-witness");
        await using (var setup = await target.OpenAsync(ct))
            await ExecuteAsync(setup, "DROP TABLE HS_SQL_AGENT_DML_WITNESS", ct);

        await using (var worker = await target.OpenAsync(ct))
        await using (var tx = await worker.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            await Fence.AdmitAsync(worker, tx, Provider, evidence, ct);
            await InsertRowAsync(worker, tx, 4, ct);
            await Assert.ThrowsAnyAsync<DbException>(() =>
                Witness.WriteAsync(worker, tx, Provider, evidence, ct));
            await tx.RollbackAsync(ct);
        }

        await using var revoker = await target.OpenAsync(ct);
        Assert.Null(await Fence.ReadAsync(revoker, Provider, evidence.RequestId, ct));
        Assert.Equal(0L, await CountRowsAsync(revoker, ct));
        await Fence.RevokeAsync(revoker, Provider, evidence, ct);
    }

    [Fact]
    public async Task RevokerCannotClaimSuccessWhileWorkerHoldsUncommittedAdmission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        var evidence = Evidence("concurrent");
        await using var worker = await target.OpenAsync(ct);
        await using var tx = await worker.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await Fence.AdmitAsync(worker, tx, Provider, evidence, ct);
        await InsertRowAsync(worker, tx, 3, ct);
        await Witness.WriteAsync(worker, tx, Provider, evidence, ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.Run(async () =>
        {
            await using var revoker = await target.OpenAsync(deadline.Token);
            started.TrySetResult();
            await Fence.RevokeAsync(revoker, Provider, evidence, deadline.Token);
        }, ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await Task.Delay(200, ct);
        Assert.False(pending.IsCompleted,
            "The target cannot acknowledge a revocation against an uncommitted admission.");

        await tx.CommitAsync(ct);
        await Assert.ThrowsAnyAsync<DbException>(() =>
            pending.WaitAsync(TimeSpan.FromSeconds(12), ct));

        await using var verifier = await target.OpenAsync(ct);
        Assert.Equal(RelationalDmlTargetExecutionFence.AdmissionKind,
            (await Fence.ReadAsync(verifier, Provider, evidence.RequestId, ct))?.EntryKind);
        Assert.Equal(evidence.EvidenceFingerprint,
            await Witness.ReadFingerprintAsync(verifier, Provider, evidence.RequestId, ct));
        Assert.Equal(1L, await CountRowsAsync(verifier, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealCoordinator_EnlistsAdmissionBeforeCompiledDml(bool batch)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        var evidence = Evidence("coordinator-commit");
        var factory = new SqlServerConnectionFactory();
        var store = new InMemoryDmlApprovalChallengeStore();
        const string approvalContext = "sqlserver-fence-approval-context-v1";
        var plan = CreateInsertPlan(12L);
        await using var versionReader = await target.OpenAsync(ct);
        var version = versionReader.ServerVersion.Trim();
        var preview = await new DmlCoordinator(factory, challengeStore: store)
            .PreviewAsync(target.ConnectionString, plan, approvalContext, ct, version);

        var result = batch
            ? await new DmlAtomicTransactionCoordinator(factory,
                targetCommitWitness: Witness, targetExecutionFence: Fence)
                .CommitAsync(target.ConnectionString, [plan], [preview], ct,
                    version, evidence, requireExecutionFence: true)
            : await new DmlCoordinator(factory, challengeStore: store,
                targetCommitWitness: Witness, targetExecutionFence: Fence)
                .CommitAsync(target.ConnectionString, plan, preview.Challenge,
                    approvalContext, ct, version, evidence, requireExecutionFence: true);

        Assert.True(result.Committed);
        await using var inspector = await target.OpenAsync(ct);
        Assert.Equal(1L, await CountRowsAsync(inspector, ct));
        Assert.Equal(RelationalDmlTargetExecutionFence.AdmissionKind,
            (await Fence.ReadAsync(inspector, Provider, evidence.RequestId, ct))?.EntryKind);
        Assert.Equal(evidence.EvidenceFingerprint,
            await Witness.ReadFingerprintAsync(inspector, Provider, evidence.RequestId, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealCoordinator_RevokedBeforeCompiledDmlNeverMutates(bool batch)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var target = await Target.CreateAsync(ct);
        var evidence = Evidence("coordinator-revoked");
        var factory = new SqlServerConnectionFactory();
        var store = new InMemoryDmlApprovalChallengeStore();
        const string approvalContext = "sqlserver-fence-approval-context-v1";
        var plan = CreateInsertPlan(13L);
        await using var inspector = await target.OpenAsync(ct);
        var version = inspector.ServerVersion.Trim();
        var preview = await new DmlCoordinator(factory, challengeStore: store)
            .PreviewAsync(target.ConnectionString, plan, approvalContext, ct, version);
        await Fence.RevokeAsync(inspector, Provider, evidence, ct);

        if (batch)
            await Assert.ThrowsAnyAsync<DbException>(() =>
                new DmlAtomicTransactionCoordinator(factory,
                    targetCommitWitness: Witness, targetExecutionFence: Fence)
                    .CommitAsync(target.ConnectionString, [plan], [preview], ct,
                        version, evidence, requireExecutionFence: true));
        else
            await Assert.ThrowsAnyAsync<DbException>(() =>
                new DmlCoordinator(factory, challengeStore: store,
                    targetCommitWitness: Witness, targetExecutionFence: Fence)
                    .CommitAsync(target.ConnectionString, plan, preview.Challenge,
                        approvalContext, ct, version, evidence, requireExecutionFence: true));

        Assert.Equal(0L, await CountRowsAsync(inspector, ct));
        Assert.Equal(RelationalDmlTargetExecutionFence.RevocationKind,
            (await Fence.ReadAsync(inspector, Provider, evidence.RequestId, ct))?.EntryKind);
        Assert.Null(await Witness.ReadFingerprintAsync(inspector, Provider, evidence.RequestId, ct));
    }

    private static ValidatedDmlPlan CreateInsertPlan(long id)
    {
        const string policyVersion = "sqlserver-fence-provider-policy-v1";
        var parsed = CoreSqlTextParser.ParseDml(
            $"INSERT INTO hs_sql_agent_dml_probe (id) VALUES ({id})", Provider);
        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed, Provider, new SqlPlanValidationContext(policyVersion));
        var rows = ImmutableArray.Create(
            ImmutableDictionary.CreateRange<string, object?>(
                StringComparer.OrdinalIgnoreCase,
                [new KeyValuePair<string, object?>("id", id)]));
        return new ValidatedDmlPlan(
            DmlOperation.Insert,
            "hs_sql_agent_dml_probe",
            command,
            MatchQueryCommand: null,
            RowIdentityColumns: ImmutableArray<string>.Empty,
            RowIdentityAssurance: DmlRowIdentityAssurance.CountOnly,
            PlanFingerprint: DmlFingerprintService.ComputePlanFingerprint(command, policyVersion),
            PolicyVersion: policyVersion,
            ApprovalTtl: TimeSpan.FromMinutes(5),
            MaxAffectedRows: 1,
            ApprovalMode: DmlApprovalMode.InsertValues,
            InsertRows: rows);
    }

    private static DmlTargetCommitWitnessEvidence Evidence(string scenario) =>
        new("hsa-mssql-fence-" + scenario, new string('A', 64));

    private sealed class SqlServerConnectionFactory : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new SqlConnection(connectionString);
    }

    private static async Task InsertRowAsync(DbConnection connection, DbTransaction transaction,
        int id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO hs_sql_agent_dml_probe (ID) VALUES (@id)";
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
        command.CommandText = "SELECT COUNT(*) FROM hs_sql_agent_dml_probe";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class Target(string rootConnectionString, string isolatedConnectionString,
        string databaseName) : IAsyncDisposable
    {
        public string ConnectionString => isolatedConnectionString;

        public static async Task<Target> CreateAsync(CancellationToken ct)
        {
            const string key = "TEST_SQLSERVER_CONNECTION";
            var root = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(root))
            {
                if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(key + " must be configured for SQL Server parity in CI.");
                Assert.Skip("Live SQL Server fencing requires " + key);
            }

            // Container startup can finish after Actions starts the job. Do
            // not silently skip when the mandatory database is unavailable.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var probe = new SqlConnection(root);
                    await probe.OpenAsync(ct);
                    break;
                }
                catch (SqlException) when (attempt < 24 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
            }

            var name = "hsa_mssql_fence_" + Guid.NewGuid().ToString("N");
            var isolated = new SqlConnectionStringBuilder(root)
            {
                InitialCatalog = name,
                Pooling = false
            }.ConnectionString;
            await using (var admin = new SqlConnection(root))
            {
                await admin.OpenAsync(ct);
                await ExecuteAsync(admin, "CREATE DATABASE [" + name + "]", ct);
            }

            var target = new Target(root, isolated, name);
            try
            {
                await using var db = await target.OpenAsync(ct);
                await ExecuteAsync(db,
                    "CREATE TABLE HS_SQL_AGENT_DML_FENCE (" +
                    "REQUEST_ID VARCHAR(64) NOT NULL PRIMARY KEY, " +
                    "EVIDENCE_FINGERPRINT CHAR(64) NOT NULL, " +
                    "ENTRY_KIND VARCHAR(16) NOT NULL CHECK (ENTRY_KIND IN ('Admitted','Revoked')))", ct);
                await ExecuteAsync(db,
                    "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
                    "REQUEST_ID VARCHAR(64) NOT NULL PRIMARY KEY, " +
                    "EVIDENCE_FINGERPRINT CHAR(64) NOT NULL)", ct);
                await ExecuteAsync(db,
                    "CREATE TABLE hs_sql_agent_dml_probe (ID INT NOT NULL PRIMARY KEY)", ct);
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
            var connection = new SqlConnection(isolatedConnectionString);
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
            await using var admin = new SqlConnection(rootConnectionString);
            await admin.OpenAsync(CancellationToken.None);
            await ExecuteAsync(admin,
                "ALTER DATABASE [" + databaseName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
                CancellationToken.None);
            await ExecuteAsync(admin, "DROP DATABASE [" + databaseName + "]", CancellationToken.None);
        }
    }
}
