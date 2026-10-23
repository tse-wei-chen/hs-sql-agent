using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlAtomicTransactionCoordinatorTests
{
    private const string ApprovalContextFingerprint = "transaction-approval-context-v1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_WhenSecondStatementFails_RollsBackFirstStatement(
        bool cleanupAlsoFails)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hs-sql-agent-atomic-dml-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";

        try
        {
            string serverVersionIdentity;
            await using (var setup = new SqliteConnection(connectionString))
            {
                await setup.OpenAsync(TestContext.Current.CancellationToken);
                serverVersionIdentity = setup.ServerVersion.Trim();
                await using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var connectionFactory = new SqliteConnectionFactory();
            var previewCoordinator = new DmlCoordinator(connectionFactory);
            var first = CreateInsertPlan(1L, "Alice", "policy-atomic-v1");
            var second = CreateInsertPlan(1L, "Duplicate", "policy-atomic-v1");

            var firstPreview = await previewCoordinator.PreviewAsync(
                connectionString,
                first,
                ApprovalContextFingerprint,
                TestContext.Current.CancellationToken,
                serverVersionIdentity);
            var secondPreview = await previewCoordinator.PreviewAsync(
                connectionString,
                second,
                ApprovalContextFingerprint,
                TestContext.Current.CancellationToken,
                serverVersionIdentity);

            var coordinator = new DmlAtomicTransactionCoordinator(
                cleanupAlsoFails ? new DisposeFailureSqliteConnectionFactory() : connectionFactory);
            // A failed connection DisposeAsync must not mask the original
            // duplicate-key SQL exception or turn it into a different result.
            await Assert.ThrowsAsync<SqliteException>(() => coordinator.CommitAsync(
                connectionString,
                [first, second],
                [firstPreview, secondPreview],
                TestContext.Current.CancellationToken,
                serverVersionIdentity));

            await using var verify = new SqliteConnection(connectionString);
            await verify.OpenAsync(TestContext.Current.CancellationToken);
            await using var verifyCommand = verify.CreateCommand();
            verifyCommand.CommandText = "SELECT COUNT(*) FROM users";
            var count = Convert.ToInt32(
                await verifyCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, count);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Theory]
    [InlineData("swapped")]
    [InlineData("row-count")]
    [InlineData("table")]
    [InlineData("policy")]
    [InlineData("plan-binding")]
    public async Task Commit_RejectsUnboundChildApprovalBeforeOpeningDatabase(string mutation)
    {
        var factory = new SqliteConnectionFactory();
        var previewCoordinator = new DmlCoordinator(factory);
        var first = CreateInsertPlan(1, "Alice", "policy-bound-v1");
        var second = CreateInsertPlan(2, "Bob", "policy-bound-v1");
        const string serverVersion = "3.46.0";
        var firstPreview = await previewCoordinator.PreviewAsync(
            "Data Source=:memory:", first, ApprovalContextFingerprint,
            TestContext.Current.CancellationToken, serverVersion);
        var secondPreview = await previewCoordinator.PreviewAsync(
            "Data Source=:memory:", second, ApprovalContextFingerprint,
            TestContext.Current.CancellationToken, serverVersion);

        var mismatch = mutation switch
        {
            "swapped" => secondPreview with { Challenge = firstPreview.Challenge },
            "row-count" => secondPreview with { AffectedRows = 2 },
            "table" => secondPreview with { TableName = "other_table" },
            "policy" => secondPreview with
            {
                Challenge = secondPreview.Challenge with { PolicyVersion = "another-policy" }
            },
            "plan-binding" => secondPreview with
            {
                Challenge = secondPreview.Challenge with { ApprovalPlanBinding = null }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        var coordinator = new DmlAtomicTransactionCoordinator(factory);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(
                "Data Source=:memory:", [first, second], [firstPreview, mismatch],
                TestContext.Current.CancellationToken, serverVersion));

        Assert.Contains("preview and approval challenge do not match", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("max-rows")]
    [InlineData("approval-mode")]
    public async Task Commit_RejectsPlansNotAdmissibleForSingleDmlBeforeDatabaseAccess(string mutation)
    {
        var factory = new SqliteConnectionFactory();
        var original = CreateInsertPlan(1, "Alice", "policy-validation-v1");
        const string serverVersion = "3.46.0";
        var preview = await new DmlCoordinator(factory).PreviewAsync(
            "Data Source=:memory:", original, ApprovalContextFingerprint,
            TestContext.Current.CancellationToken, serverVersion);

        var invalid = mutation switch
        {
            "operation" => original with { Operation = DmlOperation.Delete },
            "max-rows" => original with { MaxAffectedRows = -1 },
            "approval-mode" => original with { ApprovalMode = DmlApprovalMode.RowSetMutation },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        var coordinator = new DmlAtomicTransactionCoordinator(factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(
                "Data Source=:memory:", [invalid], [preview],
                TestContext.Current.CancellationToken, serverVersion));
    }

    [Fact]
    public async Task SingleDmlMutationFailure_PreservesPrimarySqlErrorWhenConnectionDisposeFails()
    {
        var databasePath = Path.Combine(Path.GetTempPath(),
            $"hs-sql-agent-failed-single-cleanup-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={databasePath};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            string version;
            await using (var setup = new SqliteConnection(cs))
            {
                await setup.OpenAsync(ct);
                version = setup.ServerVersion.Trim();
                await using var command = setup.CreateCommand();
                command.CommandText =
                    "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL); " +
                    "INSERT INTO users (id, name) VALUES (5, 'Original');";
                await command.ExecuteNonQueryAsync(ct);
            }

            var challengeStore = new InMemoryDmlApprovalChallengeStore();
            var plan = CreateInsertPlan(5, "Duplicate", "policy-failed-cleanup-v1");
            var preview = await new DmlCoordinator(new SqliteConnectionFactory(),
                challengeStore: challengeStore).PreviewAsync(
                cs, plan, ApprovalContextFingerprint, ct, version);

            var coordinator = new DmlCoordinator(new DisposeFailureSqliteConnectionFactory(),
                challengeStore: challengeStore);
            var error = await Assert.ThrowsAsync<SqliteException>(() =>
                coordinator.CommitAsync(cs, plan, preview.Challenge,
                    ApprovalContextFingerprint, ct, version));
            Assert.Equal(19, error.SqliteErrorCode);

            await using var verify = new SqliteConnection(cs);
            await verify.OpenAsync(ct);
            await using var query = verify.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM users";
            Assert.Equal(1L, (long)(await query.ExecuteScalarAsync(ct))!);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedTargetCommit_SurvivesConnectionDisposeFailure(
        bool atomicBatch)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"hs-sql-agent-commit-cleanup-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            string serverVersion;
            await using (var setup = new SqliteConnection(connectionString))
            {
                await setup.OpenAsync(ct);
                serverVersion = setup.ServerVersion.Trim();
                await using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)";
                await command.ExecuteNonQueryAsync(ct);
            }

            var challengeStore = new InMemoryDmlApprovalChallengeStore();
            var plan = CreateInsertPlan(17L, "Confirmed", "policy-cleanup-v1");
            var preview = await new DmlCoordinator(new SqliteConnectionFactory(),
                challengeStore: challengeStore).PreviewAsync(
                connectionString, plan, ApprovalContextFingerprint, ct, serverVersion);

            // This connection is a real SQLite database; only its DisposeAsync
            // drops the acknowledgement AFTER inner DisposeAsync succeeds.
            var faultFactory = new DisposeFailureSqliteConnectionFactory();
            DmlCommitResult committed = atomicBatch
                ? await new DmlAtomicTransactionCoordinator(faultFactory).CommitAsync(
                    connectionString, [plan], [preview], ct, serverVersion)
                : await new DmlCoordinator(faultFactory, challengeStore: challengeStore)
                    .CommitAsync(connectionString, plan, preview.Challenge,
                        ApprovalContextFingerprint, ct, serverVersion);

            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);

            await using var verify = new SqliteConnection(connectionString);
            await verify.OpenAsync(ct);
            await using var query = verify.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM users WHERE id = 17";
            Assert.Equal(1L, (long)(await query.ExecuteScalarAsync(ct))!);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetEnlistedWitness_IsCommittedAtomicallyWithApprovedDml(bool atomicBatch)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"hs-sql-agent-target-witness-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            string serverVersion;
            await using (var setup = new SqliteConnection(connectionString))
            {
                await setup.OpenAsync(ct);
                serverVersion = setup.ServerVersion.Trim();
                await using var ddl = setup.CreateCommand();
                ddl.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);" +
                    "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
                    "REQUEST_ID VARCHAR(64) PRIMARY KEY, EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL);";
                await ddl.ExecuteNonQueryAsync(ct);
            }

            var factory = new SqliteConnectionFactory();
            var challengeStore = new InMemoryDmlApprovalChallengeStore();
            var witness = new RelationalDmlTargetCommitWitness();
            var token = new DmlTargetCommitWitnessEvidence("request-unique-1", new string('A', 64));
            var plan = CreateInsertPlan(11L, "Original", "policy-target-witness");
            var preview = await new DmlCoordinator(factory,
                challengeStore: challengeStore).PreviewAsync(
                    connectionString, plan, ApprovalContextFingerprint, ct, serverVersion);

            var first = atomicBatch
                ? await new DmlAtomicTransactionCoordinator(factory,
                    targetCommitWitness: witness).CommitAsync(
                        connectionString, [plan], [preview], ct, serverVersion, token)
                : await new DmlCoordinator(factory, challengeStore: challengeStore,
                    targetCommitWitness: witness).CommitAsync(
                        connectionString, plan, preview.Challenge,
                        ApprovalContextFingerprint, ct, serverVersion, token);
            Assert.True(first.Committed);
            Assert.Equal(1, first.AffectedRows);

            await using (var inspection = new SqliteConnection(connectionString))
            {
                await inspection.OpenAsync(ct);
                Assert.Equal(token.EvidenceFingerprint,
                    await witness.ReadFingerprintAsync(inspection, SqlAgentToolType.Sqlite,
                        token.RequestId, ct));
                await using var count = inspection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM users";
                Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(ct))!);
            }

            // A fresh approval may have new SQL, but the target table's
            // PRIMARY KEY prevents reusing a durable approval request ID.
            var duplicatePlan = CreateInsertPlan(12L, "Second", "policy-target-witness");
            var secondPreview = await new DmlCoordinator(factory).PreviewAsync(
                connectionString, duplicatePlan, ApprovalContextFingerprint, ct, serverVersion);
            Func<Task<DmlCommitResult>> duplicate = atomicBatch
                ? () => new DmlAtomicTransactionCoordinator(factory,
                    targetCommitWitness: witness).CommitAsync(
                        connectionString, [duplicatePlan], [secondPreview], ct, serverVersion, token)
                : () => new DmlCoordinator(factory, challengeStore: new InMemoryDmlApprovalChallengeStore(),
                    targetCommitWitness: witness).CommitAsync(
                        connectionString, duplicatePlan, secondPreview.Challenge,
                        ApprovalContextFingerprint, ct, serverVersion, token);

            // The one-time challenge is also necessary for the single-statement
            // route. Register the second challenge before testing target PK.
            if (!atomicBatch)
            {
                var freshChallengeStore = new InMemoryDmlApprovalChallengeStore();
                await freshChallengeStore.RegisterAsync(secondPreview.Challenge, ct);
                duplicate = () => new DmlCoordinator(factory, challengeStore: freshChallengeStore,
                    targetCommitWitness: witness).CommitAsync(
                        connectionString, duplicatePlan, secondPreview.Challenge,
                        ApprovalContextFingerprint, ct, serverVersion, token);
            }

            await Assert.ThrowsAsync<SqliteException>(duplicate);
            await using var verify = new SqliteConnection(connectionString);
            await verify.OpenAsync(ct);
            await using var query = verify.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM users";
            Assert.Equal(1L, (long)(await query.ExecuteScalarAsync(ct))!);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetEnlistedWitness_MissingTableRejectsAndRollsBackDml(bool atomicBatch)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"hs-sql-agent-missing-witness-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            string version;
            await using (var setup = new SqliteConnection(cs))
            {
                await setup.OpenAsync(ct);
                version = setup.ServerVersion.Trim();
                await using var ddl = setup.CreateCommand();
                ddl.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)";
                await ddl.ExecuteNonQueryAsync(ct);
            }

            var factory = new SqliteConnectionFactory();
            var store = new InMemoryDmlApprovalChallengeStore();
            var plan = CreateInsertPlan(1L, "No witness table", "policy-required-witness");
            var preview = await new DmlCoordinator(factory, challengeStore: store)
                .PreviewAsync(cs, plan, ApprovalContextFingerprint, ct, version);
            var witness = new RelationalDmlTargetCommitWitness();
            var token = new DmlTargetCommitWitnessEvidence("request-missing", new string('B', 64));

            if (atomicBatch)
                await Assert.ThrowsAsync<SqliteException>(() =>
                    new DmlAtomicTransactionCoordinator(factory, targetCommitWitness: witness)
                        .CommitAsync(cs, [plan], [preview], ct, version, token));
            else
                await Assert.ThrowsAsync<SqliteException>(() =>
                    new DmlCoordinator(factory, challengeStore: store, targetCommitWitness: witness)
                        .CommitAsync(cs, plan, preview.Challenge, ApprovalContextFingerprint, ct, version, token));

            await using var verify = new SqliteConnection(cs);
            await verify.OpenAsync(ct);
            await using var query = verify.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM users";
            Assert.Equal(0L, (long)(await query.ExecuteScalarAsync(ct))!);
            await Assert.ThrowsAsync<SqliteException>(() =>
                witness.ReadFingerprintAsync(verify, SqlAgentToolType.Sqlite,
                    "request-missing", ct)); // absence of witness table is not a negative proof
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WitnessRequestedWithoutRegistration_IsRejectedBeforeExecutingSql()
    {
        var factory = new SqliteConnectionFactory();
        var plan = CreateInsertPlan(1L, "No registration", "policy-missing-registration");
        var preview = await new DmlCoordinator(factory).PreviewAsync(
            "Data Source=:memory:", plan, ApprovalContextFingerprint,
            TestContext.Current.CancellationToken, "3.46.0");
        var token = new DmlTargetCommitWitnessEvidence("request-unconfigured", new string('C', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DmlAtomicTransactionCoordinator(factory).CommitAsync(
                "Data Source=:memory:", [plan], [preview],
                TestContext.Current.CancellationToken, "3.46.0", token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationCommittedFirst_PreventsLateWorkerFromMutatingTarget(bool batch)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hs-sql-agent-fenced-before-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        var fence = new RelationalDmlTargetExecutionFence();
        var witness = new RelationalDmlTargetCommitWitness();
        var evidence = new DmlTargetCommitWitnessEvidence("request-revoked-first", new string('A', 64));
        try
        {
            var version = await ProvisionFencedSqliteAsync(connectionString, ct);
            await using (var revoker = new SqliteConnection(connectionString))
            {
                await revoker.OpenAsync(ct);
                await fence.RevokeAsync(revoker, SqlAgentToolType.Sqlite, evidence, ct);
                Assert.Equal(RelationalDmlTargetExecutionFence.RevocationKind,
                    (await fence.ReadAsync(revoker, SqlAgentToolType.Sqlite, evidence.RequestId, ct))?.EntryKind);
            }

            var factory = new SqliteConnectionFactory();
            var store = new InMemoryDmlApprovalChallengeStore();
            var plan = CreateInsertPlan(101L, "Late", "policy-fence-v1");
            var preview = await new DmlCoordinator(factory, challengeStore: store).PreviewAsync(
                connectionString, plan, ApprovalContextFingerprint, ct, version);

            if (batch)
                await Assert.ThrowsAsync<SqliteException>(() =>
                    new DmlAtomicTransactionCoordinator(factory,
                        targetCommitWitness: witness, targetExecutionFence: fence)
                        .CommitAsync(connectionString, [plan], [preview], ct, version,
                            evidence, requireExecutionFence: true));
            else
                await Assert.ThrowsAsync<SqliteException>(() =>
                    new DmlCoordinator(factory, challengeStore: store,
                        targetCommitWitness: witness, targetExecutionFence: fence)
                        .CommitAsync(connectionString, plan, preview.Challenge,
                            ApprovalContextFingerprint, ct, version, evidence,
                            requireExecutionFence: true));

            await using var verify = new SqliteConnection(connectionString);
            await verify.OpenAsync(ct);
            await using var count = verify.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM users";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(ct))!);
            Assert.Null(await witness.ReadFingerprintAsync(
                verify, SqlAgentToolType.Sqlite, evidence.RequestId, ct));
            Assert.Equal(RelationalDmlTargetExecutionFence.RevocationKind,
                (await fence.ReadAsync(verify, SqlAgentToolType.Sqlite, evidence.RequestId, ct))?.EntryKind);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmittedWorkerCommit_IsAtomicWithWitness_AndCannotBeRevoked(bool batch)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hs-sql-agent-admitted-first-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        var fence = new RelationalDmlTargetExecutionFence();
        var witness = new RelationalDmlTargetCommitWitness();
        var evidence = new DmlTargetCommitWitnessEvidence("request-committed-first", new string('B', 64));
        try
        {
            var version = await ProvisionFencedSqliteAsync(connectionString, ct);
            var factory = new SqliteConnectionFactory();
            var store = new InMemoryDmlApprovalChallengeStore();
            var plan = CreateInsertPlan(102L, "Committed", "policy-fence-v1");
            var preview = await new DmlCoordinator(factory, challengeStore: store).PreviewAsync(
                connectionString, plan, ApprovalContextFingerprint, ct, version);
            var result = batch
                ? await new DmlAtomicTransactionCoordinator(factory,
                    targetCommitWitness: witness, targetExecutionFence: fence)
                    .CommitAsync(connectionString, [plan], [preview], ct, version,
                        evidence, requireExecutionFence: true)
                : await new DmlCoordinator(factory, challengeStore: store,
                    targetCommitWitness: witness, targetExecutionFence: fence)
                    .CommitAsync(connectionString, plan, preview.Challenge,
                        ApprovalContextFingerprint, ct, version, evidence,
                        requireExecutionFence: true);
            Assert.True(result.Committed);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            var admitted = await fence.ReadAsync(connection, SqlAgentToolType.Sqlite,
                evidence.RequestId, ct);
            Assert.NotNull(admitted);
            Assert.Equal(RelationalDmlTargetExecutionFence.AdmissionKind, admitted.EntryKind);
            Assert.Equal(evidence.EvidenceFingerprint, admitted.EvidenceFingerprint);
            Assert.Equal(evidence.EvidenceFingerprint, await witness.ReadFingerprintAsync(
                connection, SqlAgentToolType.Sqlite, evidence.RequestId, ct));

            // Primary-key collision is NOT a successful revocation.
            await Assert.ThrowsAsync<SqliteException>(() =>
                fence.RevokeAsync(connection, SqlAgentToolType.Sqlite, evidence, ct));
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM users";
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(ct))!);
            Assert.Equal(RelationalDmlTargetExecutionFence.AdmissionKind,
                (await fence.ReadAsync(connection, SqlAgentToolType.Sqlite, evidence.RequestId, ct))?.EntryKind);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationFailure_RollsBackAdmissionAndAllowsPermanentRevocation(bool batch)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hs-sql-agent-fence-rollback-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        var fence = new RelationalDmlTargetExecutionFence();
        var witness = new RelationalDmlTargetCommitWitness();
        var evidence = new DmlTargetCommitWitnessEvidence("request-sql-failed", new string('C', 64));
        try
        {
            var version = await ProvisionFencedSqliteAsync(connectionString, ct);
            await using (var setup = new SqliteConnection(connectionString))
            {
                await setup.OpenAsync(ct);
                await using var sql = setup.CreateCommand();
                sql.CommandText = "INSERT INTO users (id, name) VALUES (103, 'Existing')";
                await sql.ExecuteNonQueryAsync(ct);
            }

            var factory = new SqliteConnectionFactory();
            var store = new InMemoryDmlApprovalChallengeStore();
            var plan = CreateInsertPlan(103L, "Duplicate", "policy-fence-v1");
            var preview = await new DmlCoordinator(factory, challengeStore: store).PreviewAsync(
                connectionString, plan, ApprovalContextFingerprint, ct, version);
            if (batch)
                await Assert.ThrowsAsync<SqliteException>(() =>
                    new DmlAtomicTransactionCoordinator(factory,
                        targetCommitWitness: witness, targetExecutionFence: fence)
                        .CommitAsync(connectionString, [plan], [preview], ct, version,
                            evidence, requireExecutionFence: true));
            else
                await Assert.ThrowsAsync<SqliteException>(() =>
                    new DmlCoordinator(factory, challengeStore: store,
                        targetCommitWitness: witness, targetExecutionFence: fence)
                        .CommitAsync(connectionString, plan, preview.Challenge,
                            ApprovalContextFingerprint, ct, version, evidence,
                            requireExecutionFence: true));

            await using var revoker = new SqliteConnection(connectionString);
            await revoker.OpenAsync(ct);
            Assert.Null(await fence.ReadAsync(
                revoker, SqlAgentToolType.Sqlite, evidence.RequestId, ct));
            Assert.Null(await witness.ReadFingerprintAsync(
                revoker, SqlAgentToolType.Sqlite, evidence.RequestId, ct));
            await fence.RevokeAsync(revoker, SqlAgentToolType.Sqlite, evidence, ct);
            Assert.Equal(RelationalDmlTargetExecutionFence.RevocationKind,
                (await fence.ReadAsync(revoker, SqlAgentToolType.Sqlite, evidence.RequestId, ct))?.EntryKind);
            await using var count = revoker.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM users";
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(ct))!);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false, "missing-witness")]
    [InlineData(true, "missing-witness")]
    [InlineData(false, "missing-fence-key")]
    [InlineData(true, "missing-fence-key")]
    [InlineData(false, "missing-witness-key")]
    [InlineData(true, "missing-witness-key")]
    public async Task FencedCommit_RejectsPhysicallyInvalidTargetBeforeApplicationMutation(
        bool batch, string invalidSchema)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"hs-sql-agent-preflight-gate-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var serverVersion = await ProvisionFencedSqliteAsync(cs, ct);
            var factory = new SqliteConnectionFactory();
            var store = new InMemoryDmlApprovalChallengeStore();
            var plan = CreateInsertPlan(901L, "Readiness gated",
                "policy-fence-runtime-readiness-v1");
            var preview = await new DmlCoordinator(factory, challengeStore: store)
                .PreviewAsync(cs, plan, ApprovalContextFingerprint, ct, serverVersion);
            await using (var setup = new SqliteConnection(cs))
            {
                await setup.OpenAsync(ct);
                await using var command = setup.CreateCommand();
                command.CommandText = invalidSchema switch
                {
                    "missing-witness" => "DROP TABLE HS_SQL_AGENT_DML_WITNESS",
                    "missing-fence-key" =>
                        "DROP TABLE HS_SQL_AGENT_DML_FENCE;" +
                        "CREATE TABLE HS_SQL_AGENT_DML_FENCE (" +
                        "REQUEST_ID VARCHAR(64) NOT NULL, EVIDENCE_FINGERPRINT CHAR(64) NOT NULL, " +
                        "ENTRY_KIND VARCHAR(16) NOT NULL)",
                    "missing-witness-key" =>
                        "DROP TABLE HS_SQL_AGENT_DML_WITNESS;" +
                        "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
                        "REQUEST_ID VARCHAR(64) NOT NULL, EVIDENCE_FINGERPRINT CHAR(64) NOT NULL)",
                    _ => throw new ArgumentOutOfRangeException(nameof(invalidSchema))
                };
                await command.ExecuteNonQueryAsync(ct);
            }

            var evidence = new DmlTargetCommitWitnessEvidence(
                "runtime-gate-" + Guid.NewGuid().ToString("N"), new string('A', 64));
            var fence = new RelationalDmlTargetExecutionFence();
            var witness = new RelationalDmlTargetCommitWitness();
            var exception = await Record.ExceptionAsync(async () =>
            {
                if (batch)
                    await new DmlAtomicTransactionCoordinator(
                        factory, targetCommitWitness: witness, targetExecutionFence: fence)
                        .CommitAsync(cs, [plan], [preview], ct, serverVersion,
                            evidence, requireExecutionFence: true);
                else
                    await new DmlCoordinator(factory, challengeStore: store,
                        targetCommitWitness: witness, targetExecutionFence: fence)
                        .CommitAsync(cs, plan, preview.Challenge, ApprovalContextFingerprint,
                            ct, serverVersion, evidence, requireExecutionFence: true);
            });
            Assert.NotNull(exception);
            if (invalidSchema != "missing-witness")
                Assert.Contains("unique key", exception.Message, StringComparison.OrdinalIgnoreCase);

            await using var verify = new SqliteConnection(cs);
            await verify.OpenAsync(ct);
            await using var query = verify.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM users";
            Assert.Equal(0L, Convert.ToInt64(await query.ExecuteScalarAsync(ct)));
            query.CommandText = "SELECT COUNT(*) FROM HS_SQL_AGENT_DML_FENCE";
            Assert.Equal(0L, Convert.ToInt64(await query.ExecuteScalarAsync(ct)));
            var recorded = await fence.ReadAsync(
                verify, SqlAgentToolType.Sqlite, evidence.RequestId, ct);
            Assert.Null(recorded);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PostCommitEvidenceVerification_IsNonReplayableWhenEitherProofIsMissing(
        bool missingAdmission, bool missingWitness)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"hs-sql-agent-postcommit-evidence-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await ProvisionFencedSqliteAsync(cs, ct);
            var proof = new DmlTargetCommitWitnessEvidence(
                "postcommit-" + Guid.NewGuid().ToString("N"), new string('B', 64));
            var fence = new RelationalDmlTargetExecutionFence();
            var witness = new RelationalDmlTargetCommitWitness();
            await using var connection = new SqliteConnection(cs);
            await connection.OpenAsync(ct);
            await using (var tx = await connection.BeginTransactionAsync(ct))
            {
                if (!missingAdmission)
                    await fence.AdmitAsync(connection, tx, SqlAgentToolType.Sqlite, proof, ct);
                if (!missingWitness)
                    await witness.WriteAsync(connection, tx, SqlAgentToolType.Sqlite, proof, ct);
                await tx.CommitAsync(ct);
            }

            if (missingAdmission || missingWitness)
            {
                var error = await Assert.ThrowsAsync<DmlCommitOutcomeUnknownException>(() =>
                    DmlTargetCommitEvidenceVerifier.EnsureConfirmedAsync(
                        connection, SqlAgentToolType.Sqlite, proof, fence, witness, ct));
                Assert.Contains("could not both be verified",
                    error.InnerException?.Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                await DmlTargetCommitEvidenceVerifier.EnsureConfirmedAsync(
                    connection, SqlAgentToolType.Sqlite, proof, fence, witness, ct);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransactionBoundAdmission_RejectedIfLostBeforeApplicationMutation(bool deleteAdmission)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"hs-agent-fence-intransaction-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await ProvisionFencedSqliteAsync(cs, ct);
            var admission = new DmlTargetCommitWitnessEvidence(
                "intransaction-" + Guid.NewGuid().ToString("N"), new string('D', 64));
            await using var connection = new SqliteConnection(cs);
            await connection.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await new RelationalDmlTargetExecutionFence().AdmitAsync(
                connection, tx, SqlAgentToolType.Sqlite, admission, ct);
            if (deleteAdmission)
            {
                await using var sabotage = connection.CreateCommand();
                sabotage.Transaction = tx;
                sabotage.CommandText =
                    "DELETE FROM HS_SQL_AGENT_DML_FENCE WHERE REQUEST_ID = $request";
                sabotage.Parameters.AddWithValue("$request", admission.RequestId);
                Assert.Equal(1, await sabotage.ExecuteNonQueryAsync(ct));
                await Assert.ThrowsAsync<DmlCommitOutcomeUnknownException>(() =>
                    RelationalDmlTargetExecutionFence.EnsureAdmittedInTransactionAsync(
                        connection, tx, SqlAgentToolType.Sqlite, admission, ct));
            }
            else
            {
                await RelationalDmlTargetExecutionFence.EnsureAdmittedInTransactionAsync(
                    connection, tx, SqlAgentToolType.Sqlite, admission, ct);
            }
            await tx.RollbackAsync(ct);
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM users";
            Assert.Equal(0L, Convert.ToInt64(await query.ExecuteScalarAsync(ct)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RequestedFenceWithoutWriter_RejectsBeforeAnyTargetConnection()
    {
        var plan = CreateInsertPlan(111L, "Not admitted", "policy-fence-no-writer");
        var preview = await new DmlCoordinator(new SqliteConnectionFactory()).PreviewAsync(
            "Data Source=:memory:", plan, ApprovalContextFingerprint,
            TestContext.Current.CancellationToken, "3.46.0");
        var token = new DmlTargetCommitWitnessEvidence("req-no-fence", new string('D', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DmlAtomicTransactionCoordinator(new SqliteConnectionFactory(),
                targetCommitWitness: new RelationalDmlTargetCommitWitness())
            .CommitAsync("Data Source=:memory:", [plan], [preview],
                TestContext.Current.CancellationToken, "3.46.0", token,
                requireExecutionFence: true));
    }

    private static async Task<string> ProvisionFencedSqliteAsync(
        string connectionString, CancellationToken ct)
    {
        await using var setup = new SqliteConnection(connectionString);
        await setup.OpenAsync(ct);
        var version = setup.ServerVersion.Trim();
        await using var command = setup.CreateCommand();
        command.CommandText =
            "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);" +
            "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
            "REQUEST_ID VARCHAR(64) PRIMARY KEY, EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL);" +
            "CREATE TABLE HS_SQL_AGENT_DML_FENCE (" +
            "REQUEST_ID VARCHAR(64) PRIMARY KEY, EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL, " +
            "ENTRY_KIND VARCHAR(16) NOT NULL CHECK (ENTRY_KIND IN ('Admitted','Revoked')));";
        await command.ExecuteNonQueryAsync(ct);
        return version;
    }

    private static ValidatedDmlPlan CreateInsertPlan(long id, string name, string policyVersion)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            $"INSERT INTO users (id, name) VALUES ({id}, '{name.Replace("'", "''", StringComparison.Ordinal)}')",
            SqlAgentToolType.Sqlite);
        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext(policyVersion));
        var rows = ImmutableArray.Create(
            ImmutableDictionary.CreateRange<string, object?>(
                StringComparer.OrdinalIgnoreCase,
                [
                    new KeyValuePair<string, object?>("id", id),
                    new KeyValuePair<string, object?>("name", name)
                ]));

        return new ValidatedDmlPlan(
            DmlOperation.Insert,
            "users",
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

    private sealed class SqliteConnectionFactory : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new SqliteConnection(connectionString);
    }

    private sealed class DisposeFailureSqliteConnectionFactory : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) =>
            new DisposeFailureSqliteConnection(new SqliteConnection(connectionString));
    }

    private sealed class DisposeFailureSqliteConnection(SqliteConnection inner) : DbConnection
    {
        public override string ConnectionString
        {
            get => inner.ConnectionString;
            set => inner.ConnectionString = value;
        }

        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;

        public override void ChangeDatabase(string databaseName) =>
            inner.ChangeDatabase(databaseName);

        public override void Close() => inner.Close();

        public override void Open() => inner.Open();

        public override Task OpenAsync(CancellationToken cancellationToken) =>
            inner.OpenAsync(cancellationToken);

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            inner.BeginTransaction(isolationLevel);

        // The underlying command and transaction remain enlisted in the
        // same real SQLite connection. Only disposal is fault-injected.
        protected override DbCommand CreateDbCommand() => inner.CreateCommand();

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            throw new IOException("Simulated connection cleanup failed after target COMMIT.");
        }
    }
}
