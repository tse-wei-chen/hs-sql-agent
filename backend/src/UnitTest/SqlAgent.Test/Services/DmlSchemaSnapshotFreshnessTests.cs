using System.Collections.Immutable;
using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DmlSchemaSnapshotFreshnessTests
{
    private const string ApprovalContext = "schema-freshness-approval-v1";
    private const string PolicyVersion = "schema-freshness-policy-v1";

    [Fact]
    public void SnapshotSha256_UsesTheExactDigestEmittedByTheCompiler()
    {
        var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.Sqlite, "live-schema-v1",
            [new SqlSchemaTable("users", ["id", "name"])]);
        var reordered = new SqlSchemaSnapshot(SqlAgentToolType.Sqlite, "live-schema-v1",
            [new SqlSchemaTable("users", ["name", "id"])]);
        var changed = new SqlSchemaSnapshot(SqlAgentToolType.Sqlite, "live-schema-v2",
            [new SqlSchemaTable("users", ["id", "name"])]);

        Assert.Equal(Digest(CreatePlan(1, "Alice")), snapshot.Sha256);
        Assert.Equal(snapshot.Sha256, reordered.Sha256);
        Assert.NotEqual(snapshot.Sha256, changed.Sha256);
    }

    [Fact]
    public async Task InsertApproval_WithSnapshot_RejectsMissingVerifierBeforeMutation()
    {
        await WithDatabaseAsync(async (connectionString, serverVersion) =>
        {
            var plan = CreatePlan(1, "Alice");
            var coordinator = new DmlCoordinator(new SqliteConnectionFactory());
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, ApprovalContext, TestContext.Current.CancellationToken, serverVersion);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.CommitAsync(connectionString, plan, preview.Challenge, ApprovalContext,
                    TestContext.Current.CancellationToken, serverVersion));

            Assert.Contains("no live schema freshness verifier", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0L, await CountRowsAsync(connectionString));
        });
    }

    [Fact]
    public async Task InsertApproval_WithSnapshot_RejectsSchemaDriftAfterPreview()
    {
        await WithDatabaseAsync(async (connectionString, serverVersion) =>
        {
            var plan = CreatePlan(1, "Alice");
            var verifier = new ChangingVerifier { CurrentDigest = new string('0', 64) };
            var coordinator = new DmlCoordinator(new SqliteConnectionFactory(), schemaFreshnessVerifier: verifier);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, ApprovalContext, TestContext.Current.CancellationToken, serverVersion);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.CommitAsync(connectionString, plan, preview.Challenge, ApprovalContext,
                    TestContext.Current.CancellationToken, serverVersion));
            Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, verifier.ReadCount);
            Assert.True(verifier.SawActiveTransaction);
            Assert.Equal(0L, await CountRowsAsync(connectionString));
        });
    }

    [Fact]
    public async Task InsertApproval_WithMatchingLiveProof_CommitsAndPreventsChallengeReplay()
    {
        await WithDatabaseAsync(async (connectionString, serverVersion) =>
        {
            var plan = CreatePlan(1, "Alice");
            var verifier = new ChangingVerifier { CurrentDigest = Digest(plan) };
            var coordinator = new DmlCoordinator(new SqliteConnectionFactory(), schemaFreshnessVerifier: verifier);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, ApprovalContext, TestContext.Current.CancellationToken, serverVersion);
            var result = await coordinator.CommitAsync(connectionString, plan, preview.Challenge, ApprovalContext,
                TestContext.Current.CancellationToken, serverVersion);

            Assert.True(result.Committed);
            Assert.Equal(1L, await CountRowsAsync(connectionString));
            Assert.Equal(1, verifier.ReadCount);
            Assert.True(verifier.SawActiveTransaction);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, ApprovalContext,
                TestContext.Current.CancellationToken, serverVersion));
            Assert.Equal(1, verifier.ReadCount);
        });
    }

    [Fact]
    public async Task AtomicApproval_SchemaDriftBeforeSecondStatement_RollsBackFirst()
    {
        await WithDatabaseAsync(async (connectionString, serverVersion) =>
        {
            var first = CreatePlan(1, "Alice");
            var second = CreatePlan(2, "Bob");
            var previewCoordinator = new DmlCoordinator(new SqliteConnectionFactory());
            var a = await previewCoordinator.PreviewAsync(
                connectionString, first, ApprovalContext, TestContext.Current.CancellationToken, serverVersion);
            var b = await previewCoordinator.PreviewAsync(
                connectionString, second, ApprovalContext, TestContext.Current.CancellationToken, serverVersion);

            var verifier = new ChangingVerifier
            {
                CurrentDigest = Digest(first),
                DriftAfterFirstRead = true
            };
            var atomic = new DmlAtomicTransactionCoordinator(
                new SqliteConnectionFactory(), schemaFreshnessVerifier: verifier);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                atomic.CommitAsync(connectionString, [first, second], [a, b],
                    TestContext.Current.CancellationToken, serverVersion));

            Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, verifier.ReadCount);
            Assert.True(verifier.SawActiveTransaction);
            Assert.Equal(0L, await CountRowsAsync(connectionString));
        });
    }

    [Fact]
    public async Task AtomicApproval_WithSnapshotAndNoVerifier_RejectsBeforeFirstStatement()
    {
        await WithDatabaseAsync(async (connectionString, serverVersion) =>
        {
            var plan = CreatePlan(1, "Alice");
            var preview = await new DmlCoordinator(new SqliteConnectionFactory()).PreviewAsync(
                connectionString, plan, ApprovalContext, TestContext.Current.CancellationToken, serverVersion);
            var atomic = new DmlAtomicTransactionCoordinator(new SqliteConnectionFactory());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                atomic.CommitAsync(connectionString, [plan], [preview],
                    TestContext.Current.CancellationToken, serverVersion));
            Assert.Equal(0L, await CountRowsAsync(connectionString));
        });
    }

    private static ValidatedDmlPlan CreatePlan(long id, string name)
    {
        var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.Sqlite, "live-schema-v1",
            [new SqlSchemaTable("users", ["id", "name"])]);
        var context = new SqlPlanValidationContext(PolicyVersion).WithSchemaSnapshot(snapshot);
        var parsed = CoreSqlTextParser.ParseDml(
            $"INSERT INTO users (id, name) VALUES ({id}, '{name}')", SqlAgentToolType.Sqlite);
        var compiled = CoreDmlCompiler.CreateDefault().Compile(
            parsed, SqlAgentToolType.Sqlite, context);
        var rows = ImmutableArray.Create(ImmutableDictionary.CreateRange<string, object?>(
            StringComparer.OrdinalIgnoreCase,
            [new KeyValuePair<string, object?>("id", id),
             new KeyValuePair<string, object?>("name", name)]));
        return new ValidatedDmlPlan(
            DmlOperation.Insert, "users", compiled,
            MatchQueryCommand: null,
            RowIdentityColumns: ImmutableArray<string>.Empty,
            RowIdentityAssurance: DmlRowIdentityAssurance.CountOnly,
            PlanFingerprint: DmlFingerprintService.ComputePlanFingerprint(compiled, PolicyVersion),
            PolicyVersion: PolicyVersion,
            ApprovalTtl: TimeSpan.FromMinutes(5),
            MaxAffectedRows: 1,
            ApprovalMode: DmlApprovalMode.InsertValues,
            InsertRows: rows,
            SchemaSnapshot: snapshot);
    }

    private static string Digest(ValidatedDmlPlan plan) =>
        Assert.Single(Assert.Single(plan.MutationCommand.CompileEvidence!.Assurances
            .Where(item => item.Kind == "sqlcore.schema_snapshot")).Details
            .Where(detail => detail.Name == "sha256")).Value;

    private static async Task WithDatabaseAsync(Func<string, string, Task> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hs-sql-schema-freshness-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            string serverVersion;
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                serverVersion = connection.ServerVersion.Trim();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await action(connectionString, serverVersion);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task<long> CountRowsAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users";
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private sealed class SqliteConnectionFactory : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new SqliteConnection(connectionString);
    }

    private sealed class ChangingVerifier : IDmlSchemaSnapshotFreshnessVerifier
    {
        public string? CurrentDigest { get; set; }
        public bool DriftAfterFirstRead { get; set; }
        public int ReadCount { get; private set; }
        public bool SawActiveTransaction { get; private set; }

        public Task<string?> ReadCurrentSchemaDigestAsync(
            DbConnection connection, DbTransaction transaction,
            SqlAgentToolType provider, SqlSchemaSnapshot expectedSnapshot,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(SqlAgentToolType.Sqlite, provider);
            Assert.Equal("live-schema-v1", expectedSnapshot.Version);
            Assert.Same(connection, transaction.Connection);
            SawActiveTransaction = true;
            ReadCount++;
            var current = CurrentDigest;
            if (DriftAfterFirstRead && ReadCount == 1)
                CurrentDigest = new string('f', 64);
            return Task.FromResult(current);
        }
    }
}
