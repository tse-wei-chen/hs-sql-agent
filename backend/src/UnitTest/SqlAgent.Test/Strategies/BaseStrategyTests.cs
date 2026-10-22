using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.SqlCore.SqlParsing;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Strategies;

public interface IDbFixture : IAsyncLifetime
{
    string ConnectionString { get; }
}

/// <summary>
/// Shared provider integration coverage for the remaining strategy responsibilities: provider
/// connections/metadata plus query execution through the canonical Core compiler/executor path.
/// Legacy DML preview/token/commit behavior was removed from BaseSqlStrategy and is covered at the
/// typed DML/Core boundary instead of being duplicated here.
/// </summary>
public abstract class BaseStrategyTests<TStrategy, TFixture> : IClassFixture<TFixture>
    where TStrategy : ISqlStrategy
    where TFixture : class, IDbFixture
{
    protected readonly TFixture Fixture;
    protected readonly TStrategy ProviderStrategy;
    protected readonly CoreStrategyTestHarness<TStrategy> Strategy;

    protected BaseStrategyTests(TFixture fixture)
    {
        Fixture = fixture;
        ProviderStrategy = CreateStrategy();
        Strategy = new CoreStrategyTestHarness<TStrategy>(ProviderStrategy);
    }

    protected abstract TStrategy CreateStrategy();

    protected abstract string TestTableName { get; }
    protected abstract string TestOrdersTableName { get; }
    protected virtual string TestOrderDetailsTableName => "order_details";
    protected virtual string TestOrderDetailsUnitPriceColumn => "unit_price";
    protected virtual string TestOrderDetailsQuantityColumn => "quantity";
    protected virtual string TestOrderDetailsDiscountColumn => "discount";
    protected abstract string TestSchemaName { get; }
    protected virtual string TestOrdersUserIdColumn => "user_id";
    protected virtual string TestOrdersIdColumn => "id";
    protected virtual string TestOrderDateColumn => "order_date";
    protected virtual int TestFirstOrderId => 1;
    protected virtual bool SupportsStandaloneTime => true;
    protected virtual bool SupportsOffsetTimestamp => Strategy.DbType != SqlAgentToolType.Firebird;
    protected virtual bool SupportsPortableDateFormatting => true;
    protected virtual bool SupportsFormattedDateParsing => true;
    protected virtual string TestUserIdColumn => "id";
    protected virtual string TestUserNameColumn => "Name";
    protected virtual string TestUserAgeColumn => "age";

    protected abstract string TableNotFoundErrorCode { get; }
    protected abstract string ColumnNotFoundErrorCode { get; }

    // Retained only so existing provider subclasses do not need unrelated test-hook churn in this
    // strangler step. No shared test invokes the legacy DML compatibility surface.
    protected abstract DmlDefinition CreateInsertDml();


    // These are physical target-database fences, not a fabricated provider shim.
    // All six existing provider fixtures (including Oracle / Firebird containers)
    // execute the exact same admission, revocation and witness protocol.
    [Fact]
    public virtual async Task TargetFence_RealProvider_DeploymentReadinessIsRollbackOnly()
    {
        await WithTargetFenceTablesAsync(async (connectionString, table, provider, token) =>
        {
            await using var connection = ProviderStrategy.CreateConnection(connectionString);
            await connection.OpenAsync(token);
            var probe = new DmlTargetFenceReadinessProbe();

            await probe.VerifyAsync(connection, provider, token);

            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM HS_SQL_AGENT_DML_FENCE";
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(token)));
            count.CommandText = "SELECT COUNT(*) FROM HS_SQL_AGENT_DML_WITNESS";
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(token)));
            Assert.Equal(0L, await CountTargetFenceRowsAsync(connection, table, token));
        });
    }

    [Fact]
    public virtual async Task TargetFence_RealProvider_ReadinessFailureRollsBackAdmission()
    {
        await WithTargetFenceTablesAsync(async (connectionString, table, provider, token) =>
        {
            await using var connection = ProviderStrategy.CreateConnection(connectionString);
            await connection.OpenAsync(token);
            var probe = new DmlTargetFenceReadinessProbe();

            await Assert.ThrowsAsync<IOException>(() =>
                probe.VerifyWithDependenciesAsync(connection, provider,
                    new RelationalDmlTargetExecutionFence(),
                    new ReadinessRejectedWitness(), token));

            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM HS_SQL_AGENT_DML_FENCE";
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(token)));
            count.CommandText = "SELECT COUNT(*) FROM HS_SQL_AGENT_DML_WITNESS";
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(token)));
            Assert.Equal(0L, await CountTargetFenceRowsAsync(connection, table, token));
        });
    }

    [Fact]
    public virtual async Task TargetFence_RealProvider_MissingWitnessTableCannotPassReadiness()
    {
        await WithTargetFenceTablesAsync(async (connectionString, table, provider, token) =>
        {
            await using var connection = ProviderStrategy.CreateConnection(connectionString);
            await connection.OpenAsync(token);

            var error = await Record.ExceptionAsync(() =>
                new DmlTargetFenceReadinessProbe().VerifyAsync(connection, provider, token));
            Assert.NotNull(error);
            Assert.IsNotType<OperationCanceledException>(error);

            // Even though the fence INSERT ran first, a real failed witness
            // statement must not leave an admitted row or application DML.
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM HS_SQL_AGENT_DML_FENCE";
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(token)));
            Assert.Equal(0L, await CountTargetFenceRowsAsync(connection, table, token));
        }, provisionWitness: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task TargetFence_RealProvider_MissingUniqueKeyFailsReadiness(bool fenceKeyMissing)
    {
        await WithTargetFenceTablesAsync(async (connectionString, table, provider, token) =>
        {
            await using var connection = ProviderStrategy.CreateConnection(connectionString);
            await connection.OpenAsync(token);

            var failure = await Record.ExceptionAsync(() =>
                new DmlTargetFenceReadinessProbe().VerifyAsync(connection, provider, token));
            Assert.IsType<InvalidOperationException>(failure);
            Assert.Contains("unique key", failure.Message, StringComparison.OrdinalIgnoreCase);

            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM HS_SQL_AGENT_DML_FENCE";
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(token)));
            count.CommandText = "SELECT COUNT(*) FROM HS_SQL_AGENT_DML_WITNESS";
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(token)));
            Assert.Equal(0L, await CountTargetFenceRowsAsync(connection, table, token));
        }, enforceFencePrimaryKey: !fenceKeyMissing,
           enforceWitnessPrimaryKey: fenceKeyMissing);
    }

    private sealed class ReadinessRejectedWitness : IDmlTargetCommitWitness
    {
        public Task WriteAsync(
            DbConnection connection, DbTransaction transaction, SqlAgentToolType provider,
            DmlTargetCommitWitnessEvidence evidence, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("Simulated witness permission denial"));

        public Task<string?> ReadFingerprintAsync(
            DbConnection openConnection, SqlAgentToolType provider,
            string requestId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    [Fact]
    public virtual async Task TargetFence_RealProvider_RevocationFirstBlocksLateWorker()
    {
        await WithTargetFenceTablesAsync(async (connectionString, table, provider, token) =>
        {
            var evidence = new DmlTargetCommitWitnessEvidence(
                "fence-revoked-" + Guid.NewGuid().ToString("N"), new string('A', 64));
            var fence = new RelationalDmlTargetExecutionFence();
            var witness = new RelationalDmlTargetCommitWitness();

            await using (var revoker = ProviderStrategy.CreateConnection(connectionString))
            {
                await revoker.OpenAsync(token);
                await fence.RevokeAsync(revoker, provider, evidence, token);
            }

            await using (var worker = ProviderStrategy.CreateConnection(connectionString))
            {
                await worker.OpenAsync(token);
                await using var transaction = await worker.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable, token);
                await Assert.ThrowsAnyAsync<DbException>(() =>
                    fence.AdmitAsync(worker, transaction, provider, evidence, token));
                await transaction.RollbackAsync(token);
            }

            await using var inspector = ProviderStrategy.CreateConnection(connectionString);
            await inspector.OpenAsync(token);
            Assert.Equal(RelationalDmlTargetExecutionFence.RevocationKind,
                (await fence.ReadAsync(inspector, provider, evidence.RequestId, token))?.EntryKind);
            Assert.Null(await witness.ReadFingerprintAsync(inspector, provider, evidence.RequestId, token));
            Assert.Equal(0L, await CountTargetFenceRowsAsync(inspector, table, token));
        });
    }

    [Fact]
    public virtual async Task TargetFence_RealProvider_AdmissionAndWitnessCommitAtomically()
    {
        await WithTargetFenceTablesAsync(async (connectionString, table, provider, token) =>
        {
            var evidence = new DmlTargetCommitWitnessEvidence(
                "fence-admitted-" + Guid.NewGuid().ToString("N"), new string('B', 64));
            var fence = new RelationalDmlTargetExecutionFence();
            var witness = new RelationalDmlTargetCommitWitness();

            await using (var worker = ProviderStrategy.CreateConnection(connectionString))
            {
                await worker.OpenAsync(token);
                await using var transaction = await worker.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable, token);
                await fence.AdmitAsync(worker, transaction, provider, evidence, token);
                await ExecuteTargetFenceSqlAsync(worker,
                    $"INSERT INTO {table} (ID) VALUES (1)", token, transaction);
                await witness.WriteAsync(worker, transaction, provider, evidence, token);
                await transaction.CommitAsync(token);
            }

            await using var inspector = ProviderStrategy.CreateConnection(connectionString);
            await inspector.OpenAsync(token);
            Assert.Equal(RelationalDmlTargetExecutionFence.AdmissionKind,
                (await fence.ReadAsync(inspector, provider, evidence.RequestId, token))?.EntryKind);
            Assert.Equal(evidence.EvidenceFingerprint,
                await witness.ReadFingerprintAsync(inspector, provider, evidence.RequestId, token));
            Assert.Equal(1L, await CountTargetFenceRowsAsync(inspector, table, token));

            // An admitted COMMIT cannot be relabeled as a successful revocation.
            await Assert.ThrowsAnyAsync<DbException>(() =>
                fence.RevokeAsync(inspector, provider, evidence, token));
        });
    }

    [Fact]
    public virtual async Task TargetFence_RealProvider_RollbackReleasesBothEvidenceRows()
    {
        await WithTargetFenceTablesAsync(async (connectionString, table, provider, token) =>
        {
            var evidence = new DmlTargetCommitWitnessEvidence(
                "fence-rollback-" + Guid.NewGuid().ToString("N"), new string('C', 64));
            var fence = new RelationalDmlTargetExecutionFence();
            var witness = new RelationalDmlTargetCommitWitness();

            await using (var worker = ProviderStrategy.CreateConnection(connectionString))
            {
                await worker.OpenAsync(token);
                await using var transaction = await worker.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable, token);
                await fence.AdmitAsync(worker, transaction, provider, evidence, token);
                await ExecuteTargetFenceSqlAsync(worker,
                    $"INSERT INTO {table} (ID) VALUES (1)", token, transaction);
                await witness.WriteAsync(worker, transaction, provider, evidence, token);
                await transaction.RollbackAsync(token);
            }

            await using var inspector = ProviderStrategy.CreateConnection(connectionString);
            await inspector.OpenAsync(token);
            Assert.Null(await fence.ReadAsync(inspector, provider, evidence.RequestId, token));
            Assert.Null(await witness.ReadFingerprintAsync(inspector, provider, evidence.RequestId, token));
            Assert.Equal(0L, await CountTargetFenceRowsAsync(inspector, table, token));
            await fence.RevokeAsync(inspector, provider, evidence, token);
            Assert.Equal(RelationalDmlTargetExecutionFence.RevocationKind,
                (await fence.ReadAsync(inspector, provider, evidence.RequestId, token))?.EntryKind);
        });
    }

    [Fact]
    public virtual async Task TargetFence_RealProvider_IndependentRevokerCannotAcknowledgeAnInFlightWorker()
    {
        await WithTargetFenceTablesAsync(async (connectionString, table, provider, token) =>
        {
            var evidence = new DmlTargetCommitWitnessEvidence(
                "fence-racing-" + Guid.NewGuid().ToString("N"), new string('D', 64));
            var fence = new RelationalDmlTargetExecutionFence();
            var witness = new RelationalDmlTargetCommitWitness();

            await using var worker = ProviderStrategy.CreateConnection(connectionString);
            await worker.OpenAsync(token);
            await using var transaction = await worker.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, token);
            await fence.AdmitAsync(worker, transaction, provider, evidence, token);
            await ExecuteTargetFenceSqlAsync(worker,
                $"INSERT INTO {table} (ID) VALUES (1)", token, transaction);
            await witness.WriteAsync(worker, transaction, provider, evidence, token);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var revoke = Task.Run(async () =>
            {
                await using var revoker = ProviderStrategy.CreateConnection(connectionString);
                await revoker.OpenAsync(deadline.Token);
                started.TrySetResult();
                await fence.RevokeAsync(revoker, provider, evidence, deadline.Token);
            }, token);

            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                // Providers may block on an uncommitted unique key or reject
                // concurrent admission immediately. Neither can confirm Revoked.
                await Task.Delay(200, token);
                Assert.False(revoke.IsCompletedSuccessfully,
                    "An independent revoker acknowledged while admission was still uncommitted.");

                await transaction.CommitAsync(token);
                var failure = await Record.ExceptionAsync(async () =>
                    await revoke.WaitAsync(TimeSpan.FromSeconds(12), token));
                Assert.NotNull(failure);
                Assert.IsNotType<TimeoutException>(failure);
                Assert.IsNotType<OperationCanceledException>(failure);

                await using var inspector = ProviderStrategy.CreateConnection(connectionString);
                await inspector.OpenAsync(token);
                Assert.Equal(RelationalDmlTargetExecutionFence.AdmissionKind,
                    (await fence.ReadAsync(inspector, provider, evidence.RequestId, token))?.EntryKind);
                Assert.Equal(evidence.EvidenceFingerprint,
                    await witness.ReadFingerprintAsync(inspector, provider, evidence.RequestId, token));
                Assert.Equal(1L, await CountTargetFenceRowsAsync(inspector, table, token));
            }
            finally
            {
                deadline.Cancel();
                // Do not leave a revoker task holding a database connection
                // when the fixture's dedicated table teardown begins.
                try { await revoke.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { /* Any failure is already asserted above. */ }
            }
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public virtual async Task TargetFence_RealProvider_NativeSchemaVerificationPrecedesTargetAdmission(
        bool useDapperMutation, bool revalidateSchema)
    {
        await WithTargetFenceTablesAsync(async (connectionString, table, provider, token) =>
        {
            await using var connection = ProviderStrategy.CreateConnection(connectionString);
            await connection.OpenAsync(token);
            var fence = new RelationalDmlTargetExecutionFence();
            var witness = new RelationalDmlTargetCommitWitness();
            await new DmlTargetFenceReadinessProbe().VerifyAsync(connection, provider, token);

            var parsed = CoreSqlTextParser.ParseDml(
                $"INSERT INTO {table} (ID) VALUES (17)", provider);
            var freshness = new NativeDmlSchemaSnapshotService();
            var snapshot = revalidateSchema
                ? await freshness.CaptureAsync(connection, provider, parsed, token)
                : null;
            var nativeProvider = provider switch
            {
                SqlAgentToolType.Postgres => (ISqlProvider)new HsSqlAgent.Provider.PostgreSql.PostgresProvider(),
                SqlAgentToolType.MySQL => new HsSqlAgent.Provider.MySql.MySqlProvider(),
                SqlAgentToolType.Sqlite => new HsSqlAgent.Provider.Sqlite.SqliteProvider(),
                SqlAgentToolType.MsSqlServer => new HsSqlAgent.Provider.SqlServer.MsSqlServerProvider(),
                SqlAgentToolType.Oracle => new HsSqlAgent.Provider.Oracle.OracleProvider(),
                SqlAgentToolType.Firebird => new HsSqlAgent.Provider.Firebird.FirebirdProvider(),
                _ => throw new ArgumentOutOfRangeException(nameof(provider))
            };
            var profile = RuntimeServerProfileVerifier.Capture(provider, connection);
            var plan = revalidateSchema
                ? await new DmlPlanFactory(nativeProvider.Metadata).CreateWithMetadataConnectionAsync(
                    connection, connectionString, parsed, provider,
                    new SqlPlanValidationContext("physical-fence-transaction-continuity")
                        .WithSchemaSnapshot(snapshot!),
                    maxAffectedRows: 1, cancellationToken: token, targetProfile: profile.TargetProfile)
                : null;

            var evidence = new DmlTargetCommitWitnessEvidence(
                "after-probe-" + Guid.NewGuid().ToString("N"), new string('D', 64));
            await using (var tx = await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, token))
            {
                // Oracle's real transaction test showed that its native
                // catalog read can erase an earlier uncommitted admission.
                // Complete all immutable plan schema checks first, then
                // admit before touching any application table.
                if (revalidateSchema)
                    await DmlSchemaSnapshotFreshness.EnsureFreshAsync(
                        connection, tx, plan!.MutationCommand, snapshot!, freshness, token);

                await fence.AdmitAsync(connection, tx, provider, evidence, token);
                await RelationalDmlTargetExecutionFence.EnsureAdmittedInTransactionAsync(
                    connection, tx, provider, evidence, token);

                if (useDapperMutation)
                    await Dapper.SqlMapper.ExecuteAsync(connection,
                        new Dapper.CommandDefinition(
                            $"INSERT INTO {table} (ID) VALUES (17)",
                            transaction: tx, cancellationToken: token));
                else
                    await ExecuteTargetFenceSqlAsync(connection,
                        $"INSERT INTO {table} (ID) VALUES (17)", token, tx);
                await witness.WriteAsync(connection, tx, provider, evidence, token);
                await tx.CommitAsync(token);
            }

            var row = await fence.ReadAsync(connection, provider, evidence.RequestId, token);
            Assert.Equal(RelationalDmlTargetExecutionFence.AdmissionKind, row?.EntryKind);
            Assert.Equal(evidence.EvidenceFingerprint,
                await witness.ReadFingerprintAsync(connection, provider, evidence.RequestId, token));
            Assert.Equal(1L, await CountTargetFenceRowsAsync(connection, table, token));
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task TargetFence_RealProvider_CompiledSingleAndBatchHonorPhysicalAdmission(
        bool batch, bool revokedFirst)
    {
        await WithTargetFenceTablesAsync(async (connectionString, table, provider, token) =>
        {
            // Fully compiled and previewed SQL, not a hand-written target SQL
            // surrogate. This runs for all six fixtures including Oracle and
            // Firebird, through the public single and internal batch coordinator.
            ISqlProvider nativeProvider = provider switch
            {
                SqlAgentToolType.Postgres => new HsSqlAgent.Provider.PostgreSql.PostgresProvider(),
                SqlAgentToolType.MySQL => new HsSqlAgent.Provider.MySql.MySqlProvider(),
                SqlAgentToolType.Sqlite => new HsSqlAgent.Provider.Sqlite.SqliteProvider(),
                SqlAgentToolType.MsSqlServer => new HsSqlAgent.Provider.SqlServer.MsSqlServerProvider(),
                SqlAgentToolType.Oracle => new HsSqlAgent.Provider.Oracle.OracleProvider(),
                SqlAgentToolType.Firebird => new HsSqlAgent.Provider.Firebird.FirebirdProvider(),
                _ => throw new ArgumentOutOfRangeException(nameof(provider))
            };
            const string policy = "real-provider-compiled-fence-v1";
            const string context = "real-provider-approved-fence-v1";
            var fence = new RelationalDmlTargetExecutionFence();
            var witness = new RelationalDmlTargetCommitWitness();
            var freshness = new NativeDmlSchemaSnapshotService();
            var challengeStore = new InMemoryDmlApprovalChallengeStore();
            var evidence = new DmlTargetCommitWitnessEvidence(
                "compiled-fenced-" + Guid.NewGuid().ToString("N"), new string('A', 64));

            await using var metadataConnection = nativeProvider.Connections.Create(connectionString);
            await metadataConnection.OpenAsync(token);
            var profile = RuntimeServerProfileVerifier.Capture(provider, metadataConnection);
            var parsed = CoreSqlTextParser.ParseDml(
                $"INSERT INTO {table} (ID) VALUES (1)", provider);
            var snapshot = await freshness.CaptureAsync(
                metadataConnection, provider, parsed, token);
            Assert.NotNull(snapshot);
            var plan = await new DmlPlanFactory(nativeProvider.Metadata)
                .CreateWithMetadataConnectionAsync(
                    metadataConnection, connectionString, parsed, provider,
                    new SqlPlanValidationContext(policy).WithSchemaSnapshot(snapshot),
                    maxAffectedRows: 1, cancellationToken: token, targetProfile: profile.TargetProfile);
            Assert.Equal(DmlApprovalMode.InsertValues, plan.ApprovalMode);

            var previewCoordinator = new DmlCoordinator(nativeProvider.Connections,
                challengeStore: challengeStore, schemaFreshnessVerifier: freshness);
            var preview = await previewCoordinator.PreviewAsync(
                connectionString, plan, context, token, profile.ServerVersionIdentity);

            if (revokedFirst)
            {
                // Prevent a shared catalog handle (notably Firebird) from
                // interfering with the independent revocation transaction.
                await metadataConnection.CloseAsync();
                await using var revoker = nativeProvider.Connections.Create(connectionString);
                await revoker.OpenAsync(token);
                await fence.RevokeAsync(revoker, provider, evidence, token);
            }
            else
                await metadataConnection.CloseAsync();

            async Task<DmlCommitResult> CommitAsync() =>
                batch
                    ? await new DmlAtomicTransactionCoordinator(nativeProvider.Connections,
                        schemaFreshnessVerifier: freshness,
                        targetCommitWitness: witness, targetExecutionFence: fence)
                        .CommitAsync(connectionString, [plan], [preview], token,
                            profile.ServerVersionIdentity, evidence, requireExecutionFence: true)
                    : await new DmlCoordinator(nativeProvider.Connections,
                        challengeStore: challengeStore,
                        schemaFreshnessVerifier: freshness,
                        targetCommitWitness: witness, targetExecutionFence: fence)
                        .CommitAsync(connectionString, plan, preview.Challenge, context, token,
                            profile.ServerVersionIdentity, evidence, requireExecutionFence: true);

            if (revokedFirst)
            {
                await Assert.ThrowsAnyAsync<DbException>(CommitAsync);
            }
            else
            {
                var committed = await CommitAsync();
                Assert.True(committed.Committed);
                Assert.Equal(1, committed.AffectedRows);
            }

            await using var inspect = nativeProvider.Connections.Create(connectionString);
            await inspect.OpenAsync(token);
            Assert.Equal(revokedFirst ? 0L : 1L,
                await CountTargetFenceRowsAsync(inspect, table, token));
            var entry = await fence.ReadAsync(inspect, provider, evidence.RequestId, token);
            if (entry is null)
            {
                // Diagnostic must inspect physically committed rows, not
                // infer success from a coordinator return value.
                await using var audit = inspect.CreateCommand();
                audit.CommandText =
                    "SELECT REQUEST_ID, ENTRY_KIND FROM HS_SQL_AGENT_DML_FENCE";
                var actual = new List<string>();
                await using var reader = await audit.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    actual.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
                Assert.Fail(
                    $"Compiled DML returned without the required committed admission. " +
                    $"provider={provider}; batch={batch}; revokedFirst={revokedFirst}; " +
                    $"request={evidence.RequestId}; actual=[{string.Join(", ", actual)}]");
            }
            Assert.Equal(revokedFirst
                ? RelationalDmlTargetExecutionFence.RevocationKind
                : RelationalDmlTargetExecutionFence.AdmissionKind, entry.EntryKind);
            Assert.Equal(revokedFirst ? null : evidence.EvidenceFingerprint,
                await witness.ReadFingerprintAsync(inspect, provider, evidence.RequestId, token));
        });
    }

    private async Task WithTargetFenceTablesAsync(
        Func<string, string, SqlAgentToolType, CancellationToken, Task> assertion,
        bool provisionWitness = true,
        bool enforceFencePrimaryKey = true,
        bool enforceWitnessPrimaryKey = true)
    {
        var provider = Strategy.DbType;
        var token = TestContext.Current.CancellationToken;
        var connectionString = provider == SqlAgentToolType.Firebird
            ? new FirebirdSql.Data.FirebirdClient.FbConnectionStringBuilder(Fixture.ConnectionString)
                { Pooling = false }.ConnectionString
            : Fixture.ConnectionString;
        var probe = "HSFENCE_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var oracle = provider == SqlAgentToolType.Oracle;
        var mysql = provider == SqlAgentToolType.MySQL;
        var prefix = oracle ? "VARCHAR2" : "VARCHAR";
        var charSize = oracle ? " CHAR" : string.Empty;
        var suffix = oracle ? " SEGMENT CREATION IMMEDIATE" : mysql ? " ENGINE=InnoDB" : string.Empty;
        var requestType = $"{prefix}(64{charSize})";
        var kindType = $"{prefix}(16{charSize})";
        var fingerprintType = oracle ? "CHAR(64 CHAR)" : "CHAR(64)";
        var fenceKey = enforceFencePrimaryKey ? " PRIMARY KEY" : string.Empty;
        var witnessKey = enforceWitnessPrimaryKey ? " PRIMARY KEY" : string.Empty;
        var fenceSchema = $"CREATE TABLE HS_SQL_AGENT_DML_FENCE (" +
            $"REQUEST_ID {requestType} NOT NULL{fenceKey}, " +
            $"EVIDENCE_FINGERPRINT {fingerprintType} NOT NULL, " +
            $"ENTRY_KIND {kindType} NOT NULL)" + suffix;
        var witnessSchema = $"CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
            $"REQUEST_ID {requestType} NOT NULL{witnessKey}, " +
            $"EVIDENCE_FINGERPRINT {fingerprintType} NOT NULL)" + suffix;
        var probeSchema = $"CREATE TABLE {probe} (ID INTEGER PRIMARY KEY)" + suffix;
        await using var ddl = ProviderStrategy.CreateConnection(connectionString);
        await ddl.OpenAsync(token);
        await ExecuteTargetFenceSqlAsync(ddl, fenceSchema, token);
        try
        {
            if (provisionWitness)
                await ExecuteTargetFenceSqlAsync(ddl, witnessSchema, token);
            try
            {
                await ExecuteTargetFenceSqlAsync(ddl, probeSchema, token);
                try { await assertion(connectionString, probe, provider, token); }
                finally { await ExecuteTargetFenceSqlAsync(ddl, $"DROP TABLE {probe}", token); }
            }
            finally
            {
                if (provisionWitness)
                    await ExecuteTargetFenceSqlAsync(ddl, "DROP TABLE HS_SQL_AGENT_DML_WITNESS", token);
            }
        }
        finally { await ExecuteTargetFenceSqlAsync(ddl, "DROP TABLE HS_SQL_AGENT_DML_FENCE", token); }
    }

    private static async Task ExecuteTargetFenceSqlAsync(DbConnection connection, string sql,
        CancellationToken token, DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<long> CountTargetFenceRowsAsync(DbConnection connection, string table,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }

    [Fact]
    public virtual async Task NativeDmlSchemaFreshness_RealProviderRecheckUsesCurrentTransaction()
    {
        // The same fixture runs this test on ALL SIX engines. This checks real
        // provider catalog SQL, not merely whether the interface can be cast.
        var provider = Strategy.DbType;
        var token = TestContext.Current.CancellationToken;
        var sql = $"INSERT INTO {TestTableName} ({TestUserIdColumn}, {TestUserNameColumn}) " +
                  "VALUES (987654321, 'schema-probe')";
        var parsed = CoreSqlTextParser.ParseDml(sql, provider);
        var freshness = new NativeDmlSchemaSnapshotService();

        await using var connection = ProviderStrategy.CreateConnection(Fixture.ConnectionString);
        await connection.OpenAsync(token);
        var snapshot = await freshness.CaptureAsync(connection, provider, parsed, token);
        Assert.NotNull(snapshot);
        Assert.Equal(provider, snapshot.Provider);
        Assert.Single(snapshot.Tables);
        Assert.StartsWith(
            provider is SqlAgentToolType.Sqlite or SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                ? "native-dml-schema-v2:" : "native-dml-schema-v1:",
            snapshot.Version, StringComparison.Ordinal);

        await using var transaction = await connection.BeginTransactionAsync(token);
        var actual = await freshness.ReadCurrentSchemaDigestAsync(
            connection, transaction, provider, snapshot, token);
        Assert.Equal(snapshot.Sha256, actual);

        // Each provider must reject a transaction belonging to another connection.
        await using var other = ProviderStrategy.CreateConnection(Fixture.ConnectionString);
        await other.OpenAsync(token);
        var wrongTransaction = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            freshness.ReadCurrentSchemaDigestAsync(
                other, transaction, provider, snapshot, token));
        Assert.Contains("transaction", wrongTransaction.Message, StringComparison.OrdinalIgnoreCase);
        await transaction.RollbackAsync(token);
    }

    [Fact]
    public virtual async Task NativeDmlSchemaFreshness_RealProviderDdlChangeInvalidatesProof()
    {
        // Keep DDL entirely on a per-test table so existing shared fixture data
        // cannot be changed, even when this test runs in parallel with others.
        var provider = Strategy.DbType;
        var token = TestContext.Current.CancellationToken;
        var tableName = "HSFRESH_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var parsed = CoreSqlTextParser.ParseDml(
            $"INSERT INTO {tableName} (ID, NAME) VALUES (1, 'snapshot')", provider);
        var freshness = new NativeDmlSchemaSnapshotService();
        await using var connection = ProviderStrategy.CreateConnection(Fixture.ConnectionString);
        await connection.OpenAsync(token);

        async Task ExecuteAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(token);
        }

        await ExecuteAsync($"CREATE TABLE {tableName} (ID INTEGER PRIMARY KEY, NAME VARCHAR(80))");
        try
        {
            var original = await freshness.CaptureAsync(connection, provider, parsed, token);
            Assert.NotNull(original);

            // Complete any auto-commit DDL before beginning the metadata-read transaction.
            await ExecuteAsync($"ALTER TABLE {tableName} ADD EXTRA_COL INTEGER");
            await using var transaction = await connection.BeginTransactionAsync(token);
            var changed = await freshness.ReadCurrentSchemaDigestAsync(
                connection, transaction, provider, original, token);

            Assert.NotNull(changed);
            Assert.NotEqual(original.Sha256, changed);
            await transaction.RollbackAsync(token);
        }
        finally
        {
            await ExecuteAsync($"DROP TABLE {tableName}");
        }
    }

    [Fact]
    public virtual async Task NativeDmlSchemaFreshness_RealProviderApprovalCommitsAndRejectsStaleSchema()
    {
        // End-to-end proof on ALL SIX real provider fixtures, including the actual
        // DmlCoordinator and its one-time approval store. Each test owns its table.
        var provider = Strategy.DbType;
        var token = TestContext.Current.CancellationToken;
        var tableName = "HSAPP_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        const string policyVersion = "native-dml-execution-proof-v1";
        const string approvalContext = "native-dml-execution-approval-v1";
        var freshness = new NativeDmlSchemaSnapshotService();
        ISqlProvider nativeProvider = provider switch
        {
            SqlAgentToolType.Postgres => new HsSqlAgent.Provider.PostgreSql.PostgresProvider(),
            SqlAgentToolType.MySQL => new HsSqlAgent.Provider.MySql.MySqlProvider(),
            SqlAgentToolType.Sqlite => new HsSqlAgent.Provider.Sqlite.SqliteProvider(),
            SqlAgentToolType.MsSqlServer => new HsSqlAgent.Provider.SqlServer.MsSqlServerProvider(),
            SqlAgentToolType.Oracle => new HsSqlAgent.Provider.Oracle.OracleProvider(),
            SqlAgentToolType.Firebird => new HsSqlAgent.Provider.Firebird.FirebirdProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        // Firebird pools retain physical attachments and can hold metadata-use
        // locks after a DbConnection is returned to the pool. Disable pooling
        // only for this DDL-heavy integration test, never in production.
        var connectionString = provider == SqlAgentToolType.Firebird
            ? new FirebirdSql.Data.FirebirdClient.FbConnectionStringBuilder(Fixture.ConnectionString)
                { Pooling = false }.ConnectionString
            : Fixture.ConnectionString;
        await using var connection = ProviderStrategy.CreateConnection(connectionString);
        await connection.OpenAsync(token);
        var profile = RuntimeServerProfileVerifier.Capture(provider, connection);
        var serverVersion = profile.ServerVersionIdentity;

        async Task ExecuteAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(token);
        }

        async Task<long> CountRowsAsync()
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
            return Convert.ToInt64(await command.ExecuteScalarAsync(token));
        }

        // Firebird (and other providers) may retain schema metadata locks while
        // a connection is open. Use a fresh connection for post-preview DDL
        // and verification, rather than trying to migrate a table still used
        // by the catalog-capturing connection.
        async Task ExecuteFreshAsync(string sql)
        {
            await using var ddlConnection = nativeProvider.Connections.Create(connectionString);
            await ddlConnection.OpenAsync(token);
            await using var command = ddlConnection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(token);
        }

        async Task<long> CountFreshRowsAsync()
        {
            await using var readConnection = nativeProvider.Connections.Create(connectionString);
            await readConnection.OpenAsync(token);
            await using var command = readConnection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
            return Convert.ToInt64(await command.ExecuteScalarAsync(token));
        }

        async Task<ValidatedDmlPlan> PlanAsync(long id)
        {
            var parsed = CoreSqlTextParser.ParseDml(
                $"INSERT INTO {tableName} (ID, NAME) VALUES ({id}, 'Alice')", provider);
            var snapshot = await freshness.CaptureAsync(connection, provider, parsed, token);
            Assert.NotNull(snapshot);
            var plan = await new DmlPlanFactory(nativeProvider.Metadata)
                .CreateWithMetadataConnectionAsync(
                    connection, connectionString, parsed, provider,
                    new SqlPlanValidationContext(policyVersion).WithSchemaSnapshot(snapshot),
                    maxAffectedRows: 1, cancellationToken: token, targetProfile: profile.TargetProfile);
            Assert.Equal(snapshot.Sha256, plan.SchemaSnapshot?.Sha256);
            Assert.Equal(DmlApprovalMode.InsertValues, plan.ApprovalMode);
            if (provider == SqlAgentToolType.Firebird)
            {
                // 'Default' is a catalog label, never a Firebird SQL qualifier.
                Assert.Equal(tableName, snapshot.Tables[0].Name);
                Assert.DoesNotContain("Default", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
            }
            return plan;
        }

        // Oracle defaults to deferred segment creation. A first INSERT into
        // a segment-less table in SERIALIZABLE isolation can raise ORA-08177:
        // materialize the *test-owned* segment at CREATE TABLE, never relax
        // the real DML transaction's isolation to make this test pass.
        var tableDefinition =
            $"CREATE TABLE {tableName} (ID INTEGER PRIMARY KEY, NAME VARCHAR(80))";
        if (provider == SqlAgentToolType.Oracle)
            tableDefinition += " SEGMENT CREATION IMMEDIATE";
        await ExecuteAsync(tableDefinition);
        try
        {
            var coordinator = new DmlCoordinator(
                nativeProvider.Connections, schemaFreshnessVerifier: freshness);

            var approved = await PlanAsync(1);
            var preview = await coordinator.PreviewAsync(
                connectionString, approved, approvalContext, token, serverVersion);
            var committed = await coordinator.CommitAsync(
                connectionString, approved, preview.Challenge, approvalContext, token, serverVersion);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            Assert.Equal(1L, await CountRowsAsync());

            var stale = await PlanAsync(2);
            var stalePreview = await coordinator.PreviewAsync(
                connectionString, stale, approvalContext, token, serverVersion);

            // Release schema/catalog handles before running a separate DDL session.
            await connection.CloseAsync();
            await ExecuteFreshAsync($"ALTER TABLE {tableName} ADD EXTRA_COL INTEGER");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.CommitAsync(
                    connectionString, stale, stalePreview.Challenge, approvalContext, token, serverVersion));
            Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1L, await CountFreshRowsAsync());
        }
        finally
        {
            await connection.CloseAsync();
            await ExecuteFreshAsync($"DROP TABLE {tableName}");
        }
    }

    [Fact]
    public virtual async Task SqlCore_NativeTypedSchemaQuery_ExecutesAgainstProviderDatabase()
    {
        // Reuses each provider's existing seeded fixture, including SQL Server,
        // Oracle and Firebird Testcontainers, instead of starting extra servers.
        var provider = Strategy.DbType;
        string CatalogName(string identifier) => provider switch
        {
            SqlAgentToolType.Postgres => identifier.ToLowerInvariant(),
            SqlAgentToolType.Oracle or SqlAgentToolType.Firebird =>
                identifier.ToUpperInvariant(),
            _ => identifier
        };

        var id = TestUserIdColumn;
        var name = TestUserNameColumn;
        var sql = $"SELECT {id}, {name} FROM {TestTableName} ORDER BY {id}";
        var snapshot = new SqlSchemaSnapshot(
            provider,
            "provider-native-execution-v1",
            [
                SqlSchemaTable.WithTypes(
                    CatalogName(TestTableName),
                    [
                        new SqlSchemaColumn(
                            CatalogName(id), SqlSchemaValueKind.Numeric),
                        new SqlSchemaColumn(
                            CatalogName(name), SqlSchemaValueKind.Text)
                    ])
            ]);

        var compiled = SqlCoreFacade.CompileQuery(
            sql, provider, provider,
            new SqlPlanValidationContext("native-execution-v1")
                .WithSchemaSnapshot(snapshot),
            new SqlExecutionPlanPolicy(0));
        Assert.Empty(compiled.Parameters);

        await using var connection = ProviderStrategy.CreateConnection(Fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = compiled.Sql;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            Assert.False(reader.IsDBNull(1));
            names.Add(reader.GetString(1));
        }

        Assert.Equal(new[] { "Alice", "Bob", "Charlie" }, names);
    }

    [Fact]
    public virtual async Task SqlCore_AllSixSourceDialects_ExecutePortableQueriesOnProvider()
    {
        // Every existing real-provider fixture supplies one target; running all
        // six sources in this shared base covers all 36 source/target pairs.
        // These are narrowly portable, literal-free queries: no unproven
        // collation, parameter conventions or provider-specific coercion claims.
        var target = Strategy.DbType;
        var sources = new[]
        {
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Firebird
        };

        string CatalogName(SqlAgentToolType dialect, string value) => dialect switch
        {
            SqlAgentToolType.Postgres => value.ToLowerInvariant(),
            SqlAgentToolType.Oracle or SqlAgentToolType.Firebird => value.ToUpperInvariant(),
            _ => value
        };

        var id = TestUserIdColumn;
        var name = TestUserNameColumn;
        var table = TestTableName;
        var cases = new (string Sql, string[] ExpectedNames)[]
        {
            ($"SELECT {id}, {name} FROM {table} ORDER BY {id}",
                ["Alice", "Bob", "Charlie"]),
            ($"SELECT {id}, {name} FROM {table} ORDER BY {id} DESC",
                ["Charlie", "Bob", "Alice"]),
            ($"WITH scoped AS (SELECT {id}, {name} FROM {table}) " +
                $"SELECT {id}, {name} FROM scoped ORDER BY {id}",
                ["Alice", "Bob", "Charlie"]),
            ($"SELECT {id}, {name} FROM {table} WHERE {name} IS NOT NULL ORDER BY {id}",
                ["Alice", "Bob", "Charlie"])
        };

        await using var connection = ProviderStrategy.CreateConnection(Fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var assertions = 0;
        foreach (var source in sources)
        {
            var snapshot = new SqlSchemaSnapshot(
                source, "six-dialect-native-data-v1",
                [
                    SqlSchemaTable.WithTypes(CatalogName(source, table),
                    [
                        new SqlSchemaColumn(CatalogName(source, id), SqlSchemaValueKind.Numeric),
                        new SqlSchemaColumn(CatalogName(source, name), SqlSchemaValueKind.Text)
                    ])
                ]);
            var context = new SqlPlanValidationContext("six-provider-execution-v1")
                .WithSchemaSnapshot(snapshot);

            foreach (var (sql, expectedNames) in cases)
            {
                var compiled = SqlCoreFacade.CompileQuery(
                    sql, source, target, context, new SqlExecutionPlanPolicy(0));

                // This first six-by-six intersection deliberately has no SQL
                // parameters. Parameterized cases need separate provider rules.
                Assert.Empty(compiled.Parameters);
                await using var command = connection.CreateCommand();
                command.CommandText = compiled.Sql;
                await using var reader = await command.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                var actual = new List<string>();
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    Assert.False(reader.IsDBNull(1));
                    actual.Add(reader.GetString(1));
                }
                Assert.Equal(expectedNames, actual);
                assertions++;
            }
        }
        Assert.Equal(24, assertions); // per provider fixture: 6 sources x 4 shapes
    }

    [Fact]
    public virtual async Task GetTablesAsync_ShouldReturnTables()
    {
        var tables = await Strategy.GetTablesAsync(
            Fixture.ConnectionString,
            TestSchemaName,
            TestContext.Current.CancellationToken);
        Assert.Contains(TestTableName, tables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public virtual async Task GetSchemasAsync_ShouldReturnAvailableSchemas()
    {
        var schemas = await Strategy.GetSchemasAsync(
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);
        Assert.Contains(TestSchemaName, schemas, StringComparer.OrdinalIgnoreCase);
    }


    [Fact]
    public virtual async Task FindTablesAsync_ShouldResolveKnownPhysicalTable()
    {
        var lookup = Assert.IsAssignableFrom<IProviderTableLookup>(ProviderStrategy);

        var matches = await lookup.FindTablesAsync(
            Fixture.ConnectionString,
            TestOrdersTableName,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            matches,
            match => string.Equals(
                match.Table,
                TestOrdersTableName,
                StringComparison.OrdinalIgnoreCase));
    }


    [Fact]
    public virtual async Task ConnectionMetadataAsync_ShouldReuseOpenProviderConnection()
    {
        var metadata = Assert.IsAssignableFrom<IProviderConnectionMetadataReader>(ProviderStrategy);
        await using var connection = ProviderStrategy.CreateConnection(Fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var matches = await metadata.FindTablesAsync(
            connection,
            TestOrdersTableName,
            TestContext.Current.CancellationToken);
        var target = Assert.Single(
            matches,
            match => string.Equals(
                match.Table,
                TestOrdersTableName,
                StringComparison.OrdinalIgnoreCase));

        var columns = await metadata.GetColumnsAsync(
            connection,
            target.Schema,
            target.Table,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(columns);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public virtual async Task GetColumnsAsync_ShouldReturnColumnTypes()
    {
        var columns = await Strategy.GetColumnsAsync(
            Fixture.ConnectionString,
            TestSchemaName,
            TestTableName,
            TestContext.Current.CancellationToken);
        Assert.NotEmpty(columns);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldReturnValidJson()
    {
        var definition = new QueryDefinition { TableName = TestTableName, Limit = 1 };
        ValidateQuery(definition);
        var rows = JsonSerializer.Deserialize<List<JsonElement>>(await Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_DifferentialSemanticOracleCorpus_ShouldMatchNativeEngine()
    {
        foreach (var sql in DifferentialSemanticOracleSql())
        {
            var translatedJson = await Strategy.ExecuteRawQueryAsync(
                sql,
                SqlAgentToolType.Postgres,
                Fixture.ConnectionString,
                TestContext.Current.CancellationToken);

            var translatedRows = NormalizeJsonRows(translatedJson);
            var nativeRows = await ExecuteNativeRowsAsync(sql);

            Assert.Equal(
                nativeRows,
                translatedRows);
        }
    }

    private IEnumerable<string> DifferentialSemanticOracleSql()
    {
        yield return
            $"SELECT {TestUserIdColumn} AS item_id, " +
            $"{TestUserNameColumn} AS item_name, " +
            $"{TestUserAgeColumn} + 2 AS next_age " +
            $"FROM {TestTableName} " +
            $"WHERE {TestUserAgeColumn} >= 25 " +
            $"ORDER BY {TestUserIdColumn}";

        yield return
            $"SELECT {TestOrdersUserIdColumn} AS owner_id, " +
            $"COUNT(*) AS order_count " +
            $"FROM {TestOrdersTableName} " +
            $"GROUP BY {TestOrdersUserIdColumn} " +
            $"HAVING COUNT(*) > 0 " +
            $"ORDER BY {TestOrdersUserIdColumn}";

        yield return
            $"SELECT {TestUserIdColumn} AS item_id, " +
            $"CASE WHEN {TestUserAgeColumn} >= 30 " +
            $"THEN 'senior' ELSE 'junior' END AS age_band " +
            $"FROM {TestTableName} " +
            $"ORDER BY {TestUserIdColumn}";
    }

    private async Task<string[]> ExecuteNativeRowsAsync(string sql)
    {
        await using var connection = ProviderStrategy.CreateConnection(Fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();

        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var values = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = NormalizeNativeValue(reader.GetValue(index));
            }

            rows.Add(string.Join("\u001f", values));
        }

        return rows.ToArray();
    }

    private static string[] NormalizeJsonRows(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateArray()
            .Select(row => string.Join(
                "\u001f",
                row.EnumerateObject().Select(property => NormalizeJsonValue(property.Value))))
            .ToArray();
    }

    private static string NormalizeNativeValue(object value) =>
        value switch
        {
            DBNull => "<null>",
            null => "<null>",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "<null>"
        };

    private static string NormalizeJsonValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null => "<null>",
            JsonValueKind.String => value.GetString() ?? "<null>",
            JsonValueKind.Number when value.TryGetDecimal(out var number) =>
                number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => value.ToString()
        };

    [Fact]
    public async Task ExecuteRawQueryAsync_CteWhereOrder_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestUserIdColumn} AS item_id " +
            $"FROM {TestTableName} " +
            $"WHERE {TestUserIdColumn} > 0" +
            $") SELECT item_id FROM recent ORDER BY item_id";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteUnionAllBody_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestUserIdColumn} AS item_id FROM {TestTableName} " +
            $"UNION ALL " +
            $"SELECT {TestUserIdColumn} AS item_id FROM {TestTableName}" +
            $") SELECT item_id FROM recent";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.True(
            rows.Count >= 2,
            $"Expected CTE UNION ALL to return multiple rows for {Strategy.DbType}.");
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteJoin_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT o.{TestOrdersIdColumn} AS item_id " +
            $"FROM {TestOrdersTableName} o " +
            $"JOIN {TestTableName} u " +
            $"ON o.{TestOrdersUserIdColumn} = u.{TestUserIdColumn}" +
            $") SELECT item_id FROM recent";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteGroupHaving_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH totals AS (" +
            $"SELECT {TestOrdersUserIdColumn} AS owner_id, COUNT(*) AS total " +
            $"FROM {TestOrdersTableName} " +
            $"GROUP BY {TestOrdersUserIdColumn} " +
            $"HAVING COUNT(*) > 0" +
            $") SELECT owner_id FROM totals";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteCorrelatedExists_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestOrdersUserIdColumn} AS owner_id " +
            $"FROM {TestOrdersTableName}" +
            $") SELECT r.owner_id FROM recent r " +
            $"WHERE EXISTS (" +
            $"SELECT {TestUserIdColumn} FROM {TestTableName} u " +
            $"WHERE u.{TestUserIdColumn} = r.owner_id" +
            $")";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteWindow_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH ranked AS (" +
            $"SELECT {TestOrdersUserIdColumn} AS owner_id, " +
            $"ROW_NUMBER() OVER (" +
            $"PARTITION BY {TestOrdersUserIdColumn} " +
            $"ORDER BY {TestOrdersIdColumn}" +
            $") AS row_no " +
            $"FROM {TestOrdersTableName}" +
            $") SELECT owner_id, row_no FROM ranked";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        Assert.All(
            rows,
            row => Assert.True(
                TryGetPropertyIgnoreCase(row, "row_no", out _)));
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_ChainedCtes_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH base AS (" +
            $"SELECT {TestUserIdColumn} AS item_id FROM {TestTableName}" +
            $"), recent AS (" +
            $"SELECT item_id FROM base" +
            $") SELECT item_id FROM recent";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CtePaging_ReturnsSecondRow()
    {
        var paging = Strategy.DbType switch
        {
            SqlAgentToolType.Postgres
                or SqlAgentToolType.MySQL
                or SqlAgentToolType.Sqlite =>
                " LIMIT 1 OFFSET 1",
            SqlAgentToolType.MsSqlServer
                or SqlAgentToolType.Oracle
                or SqlAgentToolType.Firebird =>
                " OFFSET 1 ROWS FETCH NEXT 1 ROWS ONLY",
            _ => throw new ArgumentOutOfRangeException(
                nameof(Strategy.DbType),
                Strategy.DbType,
                "Unsupported SQL dialect.")
        };
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestUserIdColumn} AS item_id FROM {TestTableName}" +
            $") SELECT item_id FROM recent ORDER BY item_id" +
            paging;

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        var row = Assert.Single(document.RootElement.EnumerateArray());
        var value = row.EnumerateObject().Single().Value.GetDecimal();
        Assert.Equal(2m, value);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteReferencedInsideSubquery_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestOrdersUserIdColumn} AS owner_id FROM {TestOrdersTableName}" +
            $") SELECT u.{TestUserIdColumn} FROM {TestTableName} u " +
            $"WHERE EXISTS (" +
            $"SELECT r.owner_id FROM recent r " +
            $"WHERE r.owner_id = u.{TestUserIdColumn}" +
            $")";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteJoinedWithPhysicalTable_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestOrdersUserIdColumn} AS owner_id FROM {TestOrdersTableName}" +
            $") SELECT r.owner_id FROM recent r " +
            $"JOIN {TestTableName} u " +
            $"ON r.owner_id = u.{TestUserIdColumn}";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task ExecuteRawQueryAsync_CteRootUnionAll_ShouldCompileRenderAndExecute()
    {
        var sql =
            $"WITH recent AS (" +
            $"SELECT {TestUserIdColumn} AS item_id FROM {TestTableName}" +
            $") SELECT item_id FROM recent " +
            $"UNION ALL " +
            $"SELECT {TestUserIdColumn} AS item_id FROM {TestTableName}";

        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            Strategy.DbType,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.Equal(6, rows.Count);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldReturnDbError_WhenTableNotFound()
    {
        var definition = new QueryDefinition { TableName = "NON_EXISTENT_TABLE_HS" };
        ValidateQuery(definition);
        var error = await Assert.ThrowsAsync<ProviderExecutionException>(() => Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Strategy.DbType, error.ProviderType);
        Assert.Equal("query", error.Operation);
        Assert.Equal(TableNotFoundErrorCode, error.Code);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldReturnDbError_WhenColumnNotFound()
    {
        var definition = new QueryDefinition
        {
            TableName = TestTableName,
            SelectColumns = [new FieldSelectCondition { FieldName = $"{TestTableName}.NON_EXISTENT_COL_HS" }]
        };
        ValidateQuery(definition);
        var error = await Assert.ThrowsAsync<ProviderExecutionException>(() => Strategy.ExecuteQueryAsync(
            definition,
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Strategy.DbType, error.ProviderType);
        Assert.Equal("query", error.Operation);
        Assert.Equal(ColumnNotFoundErrorCode, error.Code);
    }

    [Fact]
    public async Task ExecuteQueryAsync_TimeValue_ShouldBindAsTypedParameter()
    {
        var execution = () => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new ConstantSelectCondition
                    {
                        Constant = new SqlTimeValue(new TimeOnly(9, 30, 15)),
                        Alias = "typed_time"
                    }
                ],
                Limit = 1
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        if (!SupportsStandaloneTime)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(execution);
            Assert.Contains("no standalone TIME data type", error.Message);
            return;
        }
        Assert.NotEqual("[]", await execution());
    }

    [Fact]
    public async Task ExecuteQueryAsync_LocalTimestampValue_ShouldBindAsTypedParameter()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new ConstantSelectCondition
                    {
                        Constant = new SqlLocalDateTimeValue(new DateTime(2026, 8, 21, 9, 30, 15)),
                        Alias = "typed_timestamp"
                    }
                ],
                Limit = 1
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement[0].EnumerateObject().Single().Value.GetString();
        Assert.NotNull(value);
        Assert.True(DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed));
        Assert.Equal(new DateTime(2026, 8, 21, 9, 30, 15), parsed);
    }

    [Fact]
    public async Task ExecuteQueryAsync_LegacyDateTimeConstant_ShouldUseTypedParameter()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new ConstantSelectCondition
                    {
                        Constant = new DateTime(2026, 8, 21, 9, 30, 15, DateTimeKind.Unspecified),
                        Alias = "legacy_timestamp"
                    }
                ],
                Limit = 1
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual("[]", json);
    }

    [Fact]
    public async Task ExecuteQueryAsync_OffsetTimestampValue_ShouldBindAsTypedParameter()
    {
        var execution = () => Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                SelectColumns =
                [
                    new ConstantSelectCondition
                    {
                        Constant = new SqlOffsetDateTimeValue(
                            new DateTimeOffset(2026, 8, 21, 9, 30, 15, TimeSpan.FromHours(8))),
                        Alias = "typed_offset_timestamp"
                    }
                ],
                Limit = 1
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);

        if (!SupportsOffsetTimestamp)
        {
            await Assert.ThrowsAnyAsync<Exception>(execution);
            return;
        }
        Assert.NotEqual("[]", await execution());
    }

    [Fact]
    public async Task ExecuteQueryAsync_DateDiffDay_ShouldReturnStartToEndDifference()
    {
        var json = await Strategy.ExecuteRawQueryAsync(
            $"SELECT DATEDIFF(DAY, CAST('2026-08-20' AS DATE), CAST('2026-08-22' AS DATE)) AS day_count FROM {TestTableName}",
            SqlAgentToolType.MsSqlServer,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2m, document.RootElement[0].EnumerateObject().Single().Value.GetDecimal());
    }

    [Fact]
    public async Task ExecuteQueryAsync_DateAddDay_ShouldReturnExpectedDate()
    {
        var json = await Strategy.ExecuteRawQueryAsync(
            $"SELECT DATEADD(DAY, 2, CAST('2026-08-20' AS DATE)) AS due_date FROM {TestTableName}",
            SqlAgentToolType.MsSqlServer,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement[0].EnumerateObject().Single().Value.GetString();
        Assert.NotNull(value);
        Assert.StartsWith("2026-08-22", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteQueryAsync_PortableDateFormat_ShouldPreserveMinutes()
    {
        const string formatSqlPrefix = "SELECT FORMAT(CAST('2026-08-22 13:45:09' AS DATETIME2), 'yyyy-MM-dd HH:mm:ss') AS formatted FROM ";
        var sql = formatSqlPrefix + TestTableName;
        if (!SupportsPortableDateFormatting)
        {
            var error = await Assert.ThrowsAsync<SqlCompilationException>(() => Strategy.ExecuteRawQueryAsync(
                sql,
                SqlAgentToolType.MsSqlServer,
                Fixture.ConnectionString,
                TestContext.Current.CancellationToken));
            Assert.Contains("portable date formatting", error.Message);
            return;
        }
        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            SqlAgentToolType.MsSqlServer,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("2026-08-22 13:45:09", document.RootElement[0].EnumerateObject().Single().Value.GetString());
    }

    [Fact]
    public async Task ExecuteQueryAsync_ToDate_ShouldTranslatePortableFormat()
    {
        var sql = $"SELECT TO_DATE('2026/08/22', 'YYYY/MM/DD') AS parsed_date FROM {TestTableName}";
        if (!SupportsFormattedDateParsing)
        {
            var error = await Assert.ThrowsAsync<SqlCompilationException>(() => Strategy.ExecuteRawQueryAsync(
                sql,
                SqlAgentToolType.Oracle,
                Fixture.ConnectionString,
                TestContext.Current.CancellationToken));
            Assert.Contains("formatted date parsing", error.Message);
            return;
        }
        var json = await Strategy.ExecuteRawQueryAsync(
            sql,
            SqlAgentToolType.Oracle,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement[0].EnumerateObject().Single().Value.GetString();
        Assert.NotNull(value);
        Assert.StartsWith("2026-08-22", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteQueryAsync_DateParts_ShouldReturnNumbers()
    {
        var json = await Strategy.ExecuteRawQueryAsync(
            $"SELECT EXTRACT(YEAR FROM DATE '2026-08-22') AS y, EXTRACT(MONTH FROM DATE '2026-08-22') AS m, EXTRACT(DAY FROM DATE '2026-08-22') AS d FROM {TestTableName}",
            SqlAgentToolType.Postgres,
            Fixture.ConnectionString,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var values = document.RootElement[0].EnumerateObject().Select(x => x.Value.GetDecimal()).ToArray();
        Assert.Equal([2026m, 8m, 22m], values);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportFullQueryDefinitionStructure()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                Alias = "u",
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = $"u.{TestUserIdColumn}", Alias = "user_id" },
                    new FieldSelectCondition { FieldName = $"u.{TestUserNameColumn}", Alias = "user_name" }
                ],
                WhereColumnsAndValues =
                [
                    new BasicWhereCondition { FieldName = $"u.{TestUserIdColumn}", Operator = ">", Value = 0 }
                ],
                OrderByColumns =
                [
                    new FieldOrderByCondition { FieldName = $"u.{TestUserIdColumn}", Direction = SortDirection.Asc }
                ],
                Limit = 2
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportSubQuerySelectCondition()
    {
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestTableName,
                Alias = "u",
                SelectColumns =
                [
                    new FieldSelectCondition { FieldName = $"u.{TestUserNameColumn}", Alias = "uname" },
                    new SubQuerySelectCondition
                    {
                        TableName = TestOrdersTableName,
                        SelectColumns =
                        [
                            new FunctionSelectCondition
                            {
                                FunctionName = "COUNT",
                                Arguments = [new FieldSelectCondition { FieldName = TestOrdersIdColumn }]
                            }
                        ],
                        WhereColumnsAndValues =
                        [
                            new ColumnCompareWhereCondition
                            {
                                LeftFieldName = TestOrdersUserIdColumn,
                                Operator = "=",
                                RightFieldName = $"u.{TestUserIdColumn}"
                            }
                        ],
                        Alias = "order_count"
                    }
                ],
                Limit = 1
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
        Assert.True(TryGetPropertyIgnoreCase(rows[0], "order_count", out _));
    }

    [Fact]
    public virtual async Task ExecuteQueryAsync_ShouldSupportRoundedAggregatedNestedArithmeticExpression()
    {
        const string alias = "total_sales";
        const string tableAlias = "od";
        var json = await Strategy.ExecuteQueryAsync(
            new QueryDefinition
            {
                TableName = TestOrderDetailsTableName,
                Alias = tableAlias,
                SelectColumns =
                [
                    new FunctionSelectCondition
                    {
                        Alias = alias,
                        FunctionName = "ROUND",
                        Arguments =
                        [
                            new FunctionSelectCondition
                            {
                                FunctionName = "SUM",
                                Arguments =
                                [
                                    new OperationSelectCondition
                                    {
                                        Left = new OperationSelectCondition
                                        {
                                            Left = new FieldSelectCondition { FieldName = $"{tableAlias}.{TestOrderDetailsUnitPriceColumn}" },
                                            Operator = ArithmeticOperator.Multiply,
                                            Right = new FieldSelectCondition { FieldName = $"{tableAlias}.{TestOrderDetailsQuantityColumn}" }
                                        },
                                        Operator = ArithmeticOperator.Multiply,
                                        Right = new OperationSelectCondition
                                        {
                                            Left = new ConstantSelectCondition { Constant = 1 },
                                            Operator = ArithmeticOperator.Subtract,
                                            Right = new FieldSelectCondition { FieldName = $"{tableAlias}.{TestOrderDetailsDiscountColumn}" }
                                        }
                                    }
                                ]
                            },
                            new ConstantSelectCondition { Constant = 2 }
                        ]
                    }
                ]
            },
            Fixture.ConnectionString,
            cancellationToken: TestContext.Current.CancellationToken);
        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json);
        Assert.NotNull(rows);
        Assert.Single(rows);
        Assert.True(TryGetPropertyIgnoreCase(rows[0], alias, out var totalSales));
        Assert.InRange(totalSales.GetDecimal(), 37.65m, 37.66m);
    }

    // Provider-specific files still own these integration shapes when identifier casing or provider
    // syntax needs a specialized fixture. Keep virtual hooks without base Facts to avoid duplicate
    // executions while preserving their override contracts.
    public virtual Task ExecuteQueryAsync_ShouldSupportOffsetWithoutLimit() => Task.CompletedTask;
    public virtual Task ExecuteQueryAsync_ShouldSupportSubQueryWhereCondition() => Task.CompletedTask;
    public virtual Task ExecuteQueryAsync_ShouldSupportExistsSubQueryWhereCondition() => Task.CompletedTask;
    public virtual Task ExecuteQueryAsync_ShouldSupportBetweenInHaving() => Task.CompletedTask;
    public virtual Task ExecuteQueryAsync_ShouldSupportWhereNotIsOrIsNot() => Task.CompletedTask;
    public virtual Task ExecuteQueryAsync_ShouldSupportGroupHavingCondition() => Task.CompletedTask;

    protected static void ValidateQuery(QueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _ = QueryDefinitionCoreMapper.Map(definition);
    }

    protected static void ValidateDml(DmlDefinition definition) =>
        ArgumentNullException.ThrowIfNull(definition);

    private static bool TryGetPropertyIgnoreCase(JsonElement row, string propertyName, out JsonElement value)
    {
        foreach (var property in row.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
