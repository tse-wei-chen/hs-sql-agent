using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlInsertApprovalCoordinatorTests
{
    [Fact]
    public async Task Preview_InsertValues_UsesImmutablePayloadWithoutOpeningDatabase()
    {
        var factory = new ThrowingConnectionFactory();
        var coordinator = new DmlCoordinator(factory);
        var plan = CreatePlan();

        var preview = await coordinator.PreviewAsync(
            "not-opened",
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(DmlOperation.Insert, preview.Operation);
        Assert.Equal(2, preview.AffectedRows);
        Assert.Equal(2, preview.Rows.Length);
        Assert.Null(preview.Challenge.RowSetFingerprint);
        Assert.Equal(plan.PlanFingerprint, preview.Challenge.PlanFingerprint);
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
        var connectionString = $"Data Source={databasePath}";
        try
        {
            await using (var setup = new SqliteConnection(connectionString))
            {
                await setup.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var coordinator = new DmlCoordinator(new SqliteConnectionFactory());
            var plan = CreatePlan();
            var preview = await coordinator.PreviewAsync(
                connectionString,
                plan,
                TestContext.Current.CancellationToken);

            var result = await coordinator.CommitAsync(
                connectionString,
                plan,
                preview.Challenge,
                TestContext.Current.CancellationToken);

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
                    TestContext.Current.CancellationToken));
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
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CommitAsync(
                "not-opened",
                plan,
                preview.Challenge with { AffectedRows = 1 },
                TestContext.Current.CancellationToken));

        Assert.Contains("immutable payload", error.Message, StringComparison.OrdinalIgnoreCase);
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
