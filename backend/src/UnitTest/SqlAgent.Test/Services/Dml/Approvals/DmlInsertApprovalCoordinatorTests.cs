using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlInsertApprovalCoordinatorTests
{
    private const string ApprovalContextFingerprint = "approval-context-v1";
    private const string TestServerVersionIdentity = "test-server-version";

    [Fact]
    public async Task Preview_InsertValues_UsesImmutablePayloadWithoutOpeningDatabase()
    {
        var factory = new ThrowingConnectionFactory();
        var coordinator = new DmlCoordinator(factory);
        var plan = CreatePlan();

        var preview = await coordinator.PreviewAsync(
            "not-opened",
            plan,
            ApprovalContextFingerprint,
            TestContext.Current.CancellationToken,
            TestServerVersionIdentity);

        Assert.Equal(DmlOperation.Insert, preview.Operation);
        Assert.Equal(2, preview.AffectedRows);
        Assert.Equal(2, preview.Rows.Length);
        Assert.Null(preview.Challenge.RowSetFingerprint);
        Assert.Equal(plan.PlanFingerprint, preview.Challenge.PlanFingerprint);
        Assert.Equal(
            ApprovalContextFingerprint,
            preview.Challenge.ApprovalContextFingerprint);
        Assert.Equal(1L, Convert.ToInt64(preview.Rows[0]["id"]));
        Assert.Equal("Alice", preview.Rows[0]["name"]);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task Commit_InsertValues_ExecutesExactApprovedCommandOnce()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hs-sql-agent-insert-{Guid.NewGuid():N}.db");
        // Disable pooling so Windows releases the temporary database before cleanup.
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

            var coordinator = new DmlCoordinator(new SqliteConnectionFactory());
            var plan = CreatePlan();
            var preview = await coordinator.PreviewAsync(
                connectionString,
                plan,
                ApprovalContextFingerprint,
                TestContext.Current.CancellationToken,
                serverVersionIdentity);

            var result = await coordinator.CommitAsync(
                connectionString,
                plan,
                preview.Challenge,
                ApprovalContextFingerprint,
                TestContext.Current.CancellationToken,
                serverVersionIdentity);

            Assert.True(result.Committed);
            Assert.Equal(2, result.AffectedRows);

            await using (var verify = new SqliteConnection(connectionString))
            {
                await verify.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = verify.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM users";
                var count = Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
                Assert.Equal(2, count);
            }

            var replayError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.CommitAsync(
                    connectionString,
                    plan,
                    preview.Challenge,
                    ApprovalContextFingerprint,
                    TestContext.Current.CancellationToken,
                    serverVersionIdentity));
            Assert.Contains("already been consumed", replayError.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Commit_InsertValues_RejectsModifiedApprovedRowCountBeforeConsumption()
    {
        var coordinator = new DmlCoordinator(new ThrowingConnectionFactory());
        var plan = CreatePlan();
        var preview = await coordinator.PreviewAsync(
            "not-opened",
            plan,
            ApprovalContextFingerprint,
            TestContext.Current.CancellationToken,
            TestServerVersionIdentity);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(
                "not-opened",
                plan,
                preview.Challenge with { AffectedRows = 1 },
                ApprovalContextFingerprint,
                TestContext.Current.CancellationToken,
                TestServerVersionIdentity));

        Assert.Contains("immutable payload", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Commit_InsertValues_RejectsChangedApprovalContextBeforeConnectionOpen()
    {
        var factory = new ThrowingConnectionFactory();
        var coordinator = new DmlCoordinator(factory);
        var plan = CreatePlan();
        var preview = await coordinator.PreviewAsync(
            "not-opened",
            plan,
            ApprovalContextFingerprint,
            TestContext.Current.CancellationToken,
            TestServerVersionIdentity);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(
                "not-opened",
                plan,
                preview.Challenge,
                "different-approval-context",
                TestContext.Current.CancellationToken,
                TestServerVersionIdentity));

        Assert.Contains("execution context", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, factory.CreateCount);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("max-rows")]
    [InlineData("ttl")]
    [InlineData("preview-payload")]
    [InlineData("unbound-challenge")]
    public async Task Commit_InsertValues_BindsFullApprovalPlanBeforeAnyDatabaseAccess(string tampering)
    {
        var factory = new ThrowingConnectionFactory();
        var coordinator = new DmlCoordinator(factory);
        var plan = CreatePlan();
        var preview = await coordinator.PreviewAsync(
            "not-opened", plan, ApprovalContextFingerprint,
            TestContext.Current.CancellationToken, TestServerVersionIdentity);
        Assert.False(string.IsNullOrWhiteSpace(preview.Challenge.ApprovalPlanBinding));

        var altered = tampering switch
        {
            "table" => plan with { TableName = "other_users" },
            "max-rows" => plan with { MaxAffectedRows = 3 },
            "ttl" => plan with { ApprovalTtl = TimeSpan.FromMinutes(4) },
            "preview-payload" => plan with
            {
                InsertRows = plan.InsertRows.SetItem(0,
                    plan.InsertRows[0].SetItem("name", "Mallory"))
            },
            "unbound-challenge" => plan,
            _ => throw new ArgumentOutOfRangeException(nameof(tampering))
        };
        var challenge = tampering == "unbound-challenge"
            ? preview.Challenge with { ApprovalPlanBinding = null }
            : preview.Challenge;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync("not-opened", altered, challenge,
                ApprovalContextFingerprint, TestContext.Current.CancellationToken,
                TestServerVersionIdentity));

        Assert.Contains("plan binding", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task FailedMutation_RollsBackWithoutAllowingConsumedApprovalToReplay()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), $"hs-sql-agent-dml-replay-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            string serverVersion;
            await using (var setup = new SqliteConnection(connectionString))
            {
                await setup.OpenAsync(TestContext.Current.CancellationToken);
                serverVersion = setup.ServerVersion.Trim();
                await using var command = setup.CreateCommand();
                command.CommandText = """
                    CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                    INSERT INTO users (id, name) VALUES (1, 'already-exists');
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var coordinator = new DmlCoordinator(new SqliteConnectionFactory());
            var plan = CreatePlan();
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, ApprovalContextFingerprint,
                TestContext.Current.CancellationToken, serverVersion);

            var originalError = await Assert.ThrowsAsync<SqliteException>(() =>
                coordinator.CommitAsync(
                    connectionString, plan, preview.Challenge, ApprovalContextFingerprint,
                    TestContext.Current.CancellationToken, serverVersion));
            Assert.Equal(19, originalError.SqliteErrorCode);

            var replay = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.CommitAsync(
                    connectionString, plan, preview.Challenge, ApprovalContextFingerprint,
                    TestContext.Current.CancellationToken, serverVersion));
            Assert.Contains("already been consumed", replay.Message, StringComparison.OrdinalIgnoreCase);

            await using var verification = new SqliteConnection(connectionString);
            await verification.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = verification.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM users";
            Assert.Equal(1L, (long)(await check.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static ValidatedDmlPlan CreatePlan()
    {
        const string policyVersion = "policy-insert-v1";
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO users (id, name) VALUES (1, 'Alice'), (2, 'Bob')",
            SqlAgentToolType.Sqlite);
        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext(policyVersion));
        var rows = ImmutableArray.Create(
            ImmutableDictionary.CreateRange<string, object?>(
                StringComparer.OrdinalIgnoreCase,
                [new KeyValuePair<string, object?>("id", 1L), new KeyValuePair<string, object?>("name", "Alice")]),
            ImmutableDictionary.CreateRange<string, object?>(
                StringComparer.OrdinalIgnoreCase,
                [new KeyValuePair<string, object?>("id", 2L), new KeyValuePair<string, object?>("name", "Bob")]));

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
            MaxAffectedRows: 2,
            ApprovalMode: DmlApprovalMode.InsertValues,
            InsertRows: rows);
    }

    private sealed class SqliteConnectionFactory : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new SqliteConnection(connectionString);
    }

    private sealed class ThrowingConnectionFactory : IDbConnectionFactory
    {
        public int CreateCount { get; private set; }

        public DbConnection Create(string connectionString)
        {
            CreateCount++;
            throw new InvalidOperationException("INSERT VALUES preview must not open a database connection.");
        }
    }
}
