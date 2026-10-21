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
