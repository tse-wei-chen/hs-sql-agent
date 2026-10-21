using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlAtomicTransactionCoordinatorTests
{
    private const string ApprovalContextFingerprint = "transaction-approval-context-v1";

    [Fact]
    public async Task Commit_WhenSecondStatementFails_RollsBackFirstStatement()
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

            var coordinator = new DmlAtomicTransactionCoordinator(connectionFactory);
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
}
