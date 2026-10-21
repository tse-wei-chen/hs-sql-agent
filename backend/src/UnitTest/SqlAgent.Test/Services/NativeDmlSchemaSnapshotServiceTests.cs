using System.Collections.Immutable;
using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;
using Microsoft.Data.Sqlite;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class NativeDmlSchemaSnapshotServiceTests
{
    private const string Policy = "native-dml-schema-policy-v1";
    private const string Approval = "native-schema-approval-v1";

    [Fact]
    public async Task NativeSqlite_CapturesAndRechecksLiveCatalogOnTheSameCommitTransaction()
    {
        await WithDatabaseAsync(async (connectionString, serverVersion) =>
        {
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(native, connectionString, 1);
            Assert.Equal("main.users", Assert.Single(plan.SchemaSnapshot!.Tables).Name);
            Assert.StartsWith("native-dml-schema-v2:", plan.SchemaSnapshot.Version);

            var coordinator = new DmlCoordinator(
                new SqliteConnectionFactory(), schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, serverVersion);
            var result = await coordinator.CommitAsync(
                connectionString, plan, preview.Challenge, Approval,
                TestContext.Current.CancellationToken, serverVersion);

            Assert.True(result.Committed);
            Assert.Equal(1, result.AffectedRows);
            Assert.Equal(1, await CountRowsAsync(connectionString));
        });
    }

    [Fact]
    public async Task NativeSqlite_AlteredColumnSchemaInvalidatesExistingApproval()
    {
        await WithDatabaseAsync(async (connectionString, serverVersion) =>
        {
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(native, connectionString, 1);
            var coordinator = new DmlCoordinator(
                new SqliteConnectionFactory(), schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                connectionString, plan, Approval, TestContext.Current.CancellationToken, serverVersion);

            await using (var migration = new SqliteConnection(connectionString))
            {
                await migration.OpenAsync(TestContext.Current.CancellationToken);
                await using var change = migration.CreateCommand();
                change.CommandText = "ALTER TABLE users ADD COLUMN extra_flag INTEGER";
                await change.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.CommitAsync(
                    connectionString, plan, preview.Challenge, Approval,
                    TestContext.Current.CancellationToken, serverVersion));
            Assert.Contains("stale", rejected.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await CountRowsAsync(connectionString));
        });
    }

    [Fact]
    public async Task NativeSqlite_AtomicApprovalRollsBackIfSchemaChangedBeforeExecution()
    {
        await WithDatabaseAsync(async (connectionString, serverVersion) =>
        {
            var native = new NativeDmlSchemaSnapshotService();
            var first = await CreatePlanAsync(native, connectionString, 1);
            var second = await CreatePlanAsync(native, connectionString, 2);
            var previewCoordinator = new DmlCoordinator(new SqliteConnectionFactory());
            var firstPreview = await previewCoordinator.PreviewAsync(
                connectionString, first, Approval, TestContext.Current.CancellationToken, serverVersion);
            var secondPreview = await previewCoordinator.PreviewAsync(
                connectionString, second, Approval, TestContext.Current.CancellationToken, serverVersion);

            await using (var migration = new SqliteConnection(connectionString))
            {
                await migration.OpenAsync(TestContext.Current.CancellationToken);
                await using var change = migration.CreateCommand();
                change.CommandText = "ALTER TABLE users ADD COLUMN migration_version TEXT";
                await change.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var atomic = new DmlAtomicTransactionCoordinator(
                new SqliteConnectionFactory(), schemaFreshnessVerifier: native);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                atomic.CommitAsync(connectionString, [first, second], [firstPreview, secondPreview],
                    TestContext.Current.CancellationToken, serverVersion));
            Assert.Equal(0, await CountRowsAsync(connectionString));
        });
    }

    [Fact]
    public async Task NativeSqlite_DifferentColumnTypeChangesEvenWhenNamesStayTheSame()
    {
        await WithDatabaseAsync(async (connectionString, _) =>
        {
            var native = new NativeDmlSchemaSnapshotService();
            var first = await CaptureAsync(native, connectionString);
            await using (var migration = new SqliteConnection(connectionString))
            {
                await migration.OpenAsync(TestContext.Current.CancellationToken);
                await using var change = migration.CreateCommand();
                change.CommandText = "DROP TABLE users; CREATE TABLE users (id INTEGER PRIMARY KEY, name BLOB NOT NULL)";
                await change.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var changed = await CaptureAsync(native, connectionString);
            Assert.NotEqual(first.Version, changed.Version);
            Assert.NotEqual(first.Sha256, changed.Sha256);
            Assert.Equal(
                first.Tables[0].Columns.ToArray(),
                changed.Tables[0].Columns.ToArray());
        });
    }

    [Theory]
    [InlineData("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT)")]
    [InlineData("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL DEFAULT 'anonymous')")]
    [InlineData("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL CHECK (length(name) > 0))")]
    [InlineData("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, display TEXT GENERATED ALWAYS AS (upper(name)) VIRTUAL)")]
    public async Task NativeSqlite_TableConstraintsAndGeneratedColumnsChangeProofWithSameVisibleColumns(
        string replacement)
    {
        await WithDatabaseAsync(async (connectionString, _) =>
        {
            var native = new NativeDmlSchemaSnapshotService();
            var before = await CaptureAsync(native, connectionString);
            await ExecuteDdlAsync(connectionString, "DROP TABLE users; " + replacement);
            var after = await CaptureAsync(native, connectionString);

            Assert.Equal(before.Tables[0].Columns.ToArray(), after.Tables[0].Columns.ToArray());
            Assert.NotEqual(before.Version, after.Version);
            Assert.NotEqual(before.Sha256, after.Sha256);
        });
    }

    [Fact]
    public async Task NativeSqlite_IndexAndTriggerDefinitionChangesRejectAnApprovedInsert()
    {
        await WithDatabaseAsync(async (connectionString, serverVersion) =>
        {
            var native = new NativeDmlSchemaSnapshotService();
            var indexPlan = await CreatePlanAsync(native, connectionString, 1);
            var coordinator = new DmlCoordinator(
                new SqliteConnectionFactory(), schemaFreshnessVerifier: native);
            var indexPreview = await coordinator.PreviewAsync(
                connectionString, indexPlan, Approval,
                TestContext.Current.CancellationToken, serverVersion);

            await ExecuteDdlAsync(connectionString,
                "CREATE UNIQUE INDEX ix_users_name ON users(name)");
            Assert.NotEqual(indexPlan.SchemaSnapshot!.Sha256,
                (await CaptureAsync(native, connectionString)).Sha256);
            var indexError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.CommitAsync(connectionString, indexPlan,
                    indexPreview.Challenge, Approval,
                    TestContext.Current.CancellationToken, serverVersion));
            Assert.Contains("stale", indexError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0L, await CountRowsAsync(connectionString));

            await ExecuteDdlAsync(connectionString, "DROP INDEX ix_users_name");
            var triggerPlan = await CreatePlanAsync(native, connectionString, 2);
            var triggerPreview = await coordinator.PreviewAsync(
                connectionString, triggerPlan, Approval,
                TestContext.Current.CancellationToken, serverVersion);
            await ExecuteDdlAsync(connectionString, """
                CREATE TRIGGER users_name_upper AFTER INSERT ON users
                BEGIN UPDATE users SET name = upper(name) WHERE id = NEW.id; END
                """);
            Assert.NotEqual(triggerPlan.SchemaSnapshot!.Sha256,
                (await CaptureAsync(native, connectionString)).Sha256);
            var triggerError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.CommitAsync(connectionString, triggerPlan,
                    triggerPreview.Challenge, Approval,
                    TestContext.Current.CancellationToken, serverVersion));
            Assert.Contains("stale", triggerError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0L, await CountRowsAsync(connectionString));
        });
    }

    [Fact]
    public async Task NativeSqlite_UnrelatedTableDdlDoesNotInvalidateTargetProof()
    {
        await WithDatabaseAsync(async (connectionString, _) =>
        {
            var native = new NativeDmlSchemaSnapshotService();
            var before = await CaptureAsync(native, connectionString);
            await ExecuteDdlAsync(connectionString, """
                CREATE TABLE unrelated (id INTEGER PRIMARY KEY, value TEXT);
                CREATE INDEX ix_unrelated_value ON unrelated(value);
                """);
            var after = await CaptureAsync(native, connectionString);
            Assert.Equal(before.Sha256, after.Sha256);
            Assert.Equal(before.Version, after.Version);
        });
    }

    private static async Task ExecuteDdlAsync(string connectionString, string ddl)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ddl;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<SqlSchemaSnapshot> CaptureAsync(
        NativeDmlSchemaSnapshotService native, string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var parsed = CoreSqlTextParser.ParseDml(
            "INSERT INTO main.users (id, name) VALUES (1, 'Alice')", SqlAgentToolType.Sqlite);
        return await native.CaptureAsync(
            connection, SqlAgentToolType.Sqlite, parsed, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Expected native SQLite schema proof.");
    }

    private static async Task<ValidatedDmlPlan> CreatePlanAsync(
        NativeDmlSchemaSnapshotService native, string connectionString, long id)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            $"INSERT INTO main.users (id, name) VALUES ({id}, 'Alice')", SqlAgentToolType.Sqlite);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var snapshot = await native.CaptureAsync(
            connection, SqlAgentToolType.Sqlite, parsed, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Expected native SQLite schema proof.");
        var context = new SqlPlanValidationContext(Policy).WithSchemaSnapshot(snapshot);
        var command = CoreDmlCompiler.CreateDefault().Compile(parsed, SqlAgentToolType.Sqlite, context);
        var rows = ImmutableArray.Create(
            ImmutableDictionary.CreateRange<string, object?>(
                StringComparer.OrdinalIgnoreCase,
                [new KeyValuePair<string, object?>("id", id),
                 new KeyValuePair<string, object?>("name", "Alice")]));
        return new ValidatedDmlPlan(
            DmlOperation.Insert, "main.users", command,
            MatchQueryCommand: null,
            RowIdentityColumns: ImmutableArray<string>.Empty,
            RowIdentityAssurance: DmlRowIdentityAssurance.CountOnly,
            PlanFingerprint: DmlFingerprintService.ComputePlanFingerprint(command, Policy),
            PolicyVersion: Policy, ApprovalTtl: TimeSpan.FromMinutes(5),
            MaxAffectedRows: 1, ApprovalMode: DmlApprovalMode.InsertValues,
            InsertRows: rows, SchemaSnapshot: snapshot);
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hs-sql-native-schema-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            string serverVersion;
            await using (var setup = new SqliteConnection(connectionString))
            {
                await setup.OpenAsync(TestContext.Current.CancellationToken);
                serverVersion = setup.ServerVersion.Trim();
                await using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await test(connectionString, serverVersion);
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
}
