using System.Data.Common;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Enums;
using Moq;
using Npgsql;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// G6/S16 PostgreSQL 16 live proof: 2–4 independent CREATE TABLEs must be
/// approved as one immutable unit and committed in one database transaction.
/// </summary>
public sealed class PostgresDdlBatchLiveTests(PostgresDdlLiveFixture fixture)
    : IClassFixture<PostgresDdlLiveFixture>
{
    private sealed class Connections : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new NpgsqlConnection(connectionString);
    }
    private sealed class Approver(
        Func<DdlApprovalRequest, CancellationToken, ValueTask<DdlApprovalDecision>> response)
        : IDdlApprovalProvider
    {
        public ValueTask<DdlApprovalDecision> RequestAsync(
            DdlApprovalRequest request, CancellationToken cancellationToken = default) =>
            response(request, cancellationToken);
    }
    private ISqlProvider Provider()
    {
        var mock = new Mock<ISqlProvider>(MockBehavior.Strict);
        mock.SetupGet(p => p.Type).Returns(SqlAgentToolType.Postgres);
        mock.SetupGet(p => p.Connections).Returns(new Connections());
        return mock.Object;
    }
    private static DdlActorContext Actor(string principal = "mcp-key:777") =>
        new(principal, "db-management:99", "ddl_live");
    private static IDdlApprovalProvider Decision(DdlApprovalState state) =>
        new Approver((request, _) => ValueTask.FromResult(
            new DdlApprovalDecision(state, request.RequestId,
                request.ApprovalFingerprint, "human:independent-reviewer")));
    private static PostgresDdlRuntime Runtime(string schema, bool batchEnabled = true,
        bool dependentEnabled = false) =>
        new(new PostgresDdlExecutionOptions
        {
            Enabled = true,
            AllowCreateTable = true,
            AllowCreateTableBatch = batchEnabled,
            AllowDependentDdlBatch = dependentEnabled,
            AllowAddColumn = dependentEnabled,
            AllowCreateIndex = dependentEnabled,
            AllowedSchemas = new HashSet<string>(StringComparer.Ordinal) { schema },
            AllowedBatchPrincipals = new HashSet<string>(StringComparer.Ordinal) { "mcp-key:777" },
            PolicyRevision = "g6-s16-atomic-test"
        });
    private async Task<string> SetupAsync()
    {
        var schema = "ddl_batch_" + Guid.NewGuid().ToString("N");
        await CommandAsync("CREATE SCHEMA \"" + schema + "\"");
        return schema;
    }
    private Task CleanupAsync(string schema) =>
        CommandAsync("DROP SCHEMA \"" + schema + "\" CASCADE");
    private async Task CommandAsync(string sql)
    {
        await using var c = new NpgsqlConnection(fixture.ConnectionString);
        await c.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
    private async Task<bool> ExistsAsync(string schema, string name)
    {
        await using var c = new NpgsqlConnection(fixture.ConnectionString);
        await c.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT FROM pg_catalog.pg_class c "
            + "JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace "
            + "WHERE n.nspname=@schema AND c.relname=@name AND c.relkind='r')", c);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("name", name);
        return (bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
    private static string Script(string schema, params string[] names) =>
        string.Join(";\n", names.Select(n =>
            $"CREATE TABLE {schema}.{n} (id INTEGER NOT NULL)")) + ";";

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task PreviewOneApprovalOneAtomicCommit_ExactPhysicalTables(int count)
    {
        var schema = await SetupAsync();
        try
        {
            var names = Enumerable.Range(1, count).Select(i => "item" + i).ToArray();
            var sql = Script(schema, names);
            var runtime = Runtime(schema);
            var ct = TestContext.Current.CancellationToken;
            var preview = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), previewOnly: true, approvalProvider: null, ct);
            Assert.Equal(DdlRuntimeStatus.Preview, preview.Status);
            Assert.Null(preview.Preview);
            Assert.NotNull(preview.BatchPreview);
            Assert.Equal(count, preview.BatchPreview!.Steps.Count);
            Assert.Equal(64, preview.BatchPreview.Plan.PlanFingerprint.Length);
            Assert.Equal(count, preview.BatchPreview.Before.Count);
            Assert.Equal(count, preview.BatchPreview.ExpectedColumns.Count);
            Assert.Contains("Atomic PostgreSQL DDL batch", preview.BatchPreview.HumanReadableDiff);
            var mcp = McpDdlToolResult.FromRuntime(preview);
            Assert.Equal(count, mcp.BatchStatements?.Count);
            Assert.True(mcp.BatchAtomicityProofRequired);
            Assert.False(mcp.CommitConfirmed);
            foreach (var name in names) Assert.False(await ExistsAsync(schema, name));

            var requested = 0;
            var approval = new Approver((request, _) =>
            {
                requested++;
                Assert.Equal(preview.BatchPreview.Plan.PlanFingerprint, request.PlanFingerprint);
                Assert.Contains("item1", request.HumanReadableDiff);
                Assert.Contains("item" + count, request.HumanReadableDiff);
                return ValueTask.FromResult(new DdlApprovalDecision(DdlApprovalState.Approved,
                    request.RequestId, request.ApprovalFingerprint, "human:reviewer"));
            });
            var committed = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), false, approval, ct);
            Assert.Equal(DdlRuntimeStatus.Committed, committed.Status);
            Assert.True(committed.CommitConfirmed);
            Assert.Equal(1, requested);
            Assert.NotNull(committed.BatchPreview);
            foreach (var name in names) Assert.True(await ExistsAsync(schema, name));
        }
        finally { await CleanupAsync(schema); }
    }

    [Fact]
    public async Task DefaultOffWrongKeyRejectAndForgedOrRejectedApprovalNeverWrite()
    {
        var schema = await SetupAsync();
        try
        {
            var sql = Script(schema, "a", "b");
            var ct = TestContext.Current.CancellationToken;
            var off = await Runtime(schema, false).RunAsync(
                Provider(), fixture.ConnectionString, sql, Actor(), false,
                Decision(DdlApprovalState.Approved), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, off.Status);
            var wrongKey = await Runtime(schema).RunAsync(
                Provider(), fixture.ConnectionString, sql, Actor("mcp-key:999"), false,
                Decision(DdlApprovalState.Approved), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, wrongKey.Status);
            Assert.Equal("ddl.batch_principal_denied", wrongKey.ErrorCode);
            var denied = await Runtime(schema).RunAsync(
                Provider(), fixture.ConnectionString, sql, Actor(), false,
                Decision(DdlApprovalState.Rejected), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, denied.Status);
            var forged = await Runtime(schema).RunAsync(
                Provider(), fixture.ConnectionString, sql, Actor(), false,
                new Approver((request, _) => ValueTask.FromResult(new DdlApprovalDecision(
                    DdlApprovalState.Approved, request.RequestId, new string('0',64),
                    "human:reviewer"))), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, forged.Status);
            Assert.False(await ExistsAsync(schema, "a"));
            Assert.False(await ExistsAsync(schema, "b"));
        }
        finally { await CleanupAsync(schema); }
    }

    [Fact]
    public async Task ExternalSchemaDriftDuringApprovalNeverPartiallyCreatesTables()
    {
        var schema = await SetupAsync();
        try
        {
            var sql = Script(schema, "a", "b");
            var ct = TestContext.Current.CancellationToken;
            var runtime = Runtime(schema);
            var result = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), false,
                new Approver(async (request, _) =>
                {
                    await CommandAsync("CREATE TABLE \"" + schema + "\".\"b\" (other TEXT)");
                    return new DdlApprovalDecision(DdlApprovalState.Approved,
                        request.RequestId, request.ApprovalFingerprint, "human:reviewer");
                }), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, result.Status);
            Assert.False(await ExistsAsync(schema, "a"));
            Assert.True(await ExistsAsync(schema, "b"));
        }
        finally { await CleanupAsync(schema); }
    }

    [Fact]
    public async Task RealConcurrentNameConflictDuringSecondNativeCreate_RollsBackFirst()
    {
        var schema = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var script = Script(schema, "a", "b");
            // Rival session owns an uncommitted 'b' definition. In normal
            // READ COMMITTED preview and precondition reads it remains absent,
            // but native CREATE b must wait for rival DDL to COMMIT and fail.
            await using var rival = new NpgsqlConnection(fixture.ConnectionString);
            await rival.OpenAsync(ct);
            await using var rivalTransaction = await rival.BeginTransactionAsync(ct);
            var held = false;
            var release = Task.CompletedTask;
            var approval = new Approver(async (request, token) =>
            {
                await using var rivalCreate = new NpgsqlCommand(
                    $"CREATE TABLE \"{schema}\".\"b\" (rival TEXT)", rival, rivalTransaction);
                await rivalCreate.ExecuteNonQueryAsync(token);
                held = true;

                // Release exactly when PostgreSQL reports the Host blocked on
                // the native CREATE b conflict. Not an arbitrary timing race.
                release = Task.Run(async () =>
                {
                    await using var monitor = new NpgsqlConnection(fixture.ConnectionString);
                    await monitor.OpenAsync(ct);
                    for (var i = 0; i < 100; i++)
                    {
                        await using var probe = new NpgsqlCommand(
                            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity "
                            + "WHERE datname=current_database() AND pid<>pg_backend_pid() "
                            + "AND state='active' AND wait_event_type='Lock' "
                            + "AND query LIKE @target)", monitor);
                        probe.Parameters.AddWithValue("target",
                            "%CREATE TABLE%\"" + schema + "\".\"b\"%");
                        if ((bool)(await probe.ExecuteScalarAsync(ct))!)
                        {
                            await rivalTransaction.CommitAsync(ct);
                            return;
                        }
                        await Task.Delay(100, ct);
                    }
                    // If the Host never reaches native statement 2, abort the
                    // rival transaction so cleanup is never left blocked.
                    await rivalTransaction.RollbackAsync(CancellationToken.None);
                    throw new TimeoutException(
                        "Host never reached the expected PostgreSQL native DDL lock race.");
                }, ct);
                return new DdlApprovalDecision(DdlApprovalState.Approved,
                    request.RequestId, request.ApprovalFingerprint, "human:reviewer");
            });
            var result = await Runtime(schema).RunAsync(Provider(), fixture.ConnectionString,
                script, Actor(), false, approval, ct);
            if (held) await release;
            Assert.Equal(DdlRuntimeStatus.Failed, result.Status);
            Assert.Equal("ddl.batch_rolled_back", result.ErrorCode);
            Assert.False(await ExistsAsync(schema, "a"));
            Assert.True(await ExistsAsync(schema, "b"));
        }
        finally { await CleanupAsync(schema); }
    }

    [Fact]
    public async Task FailedSecondStatementRollsBackFirstAtDatabaseTransactionBoundary()
    {
        var schema = await SetupAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await using var db = new NpgsqlConnection(fixture.ConnectionString);
            await db.OpenAsync(ct);
            await using (var tx = await db.BeginTransactionAsync(ct))
            {
                await using (var first = new NpgsqlCommand(
                    $"CREATE TABLE \"{schema}\".\"a\" (id INTEGER)", db, tx))
                    await first.ExecuteNonQueryAsync(ct);
                await using (var second = new NpgsqlCommand(
                    $"CREATE TABLE \"{schema}\".\"a\" (id INTEGER)", db, tx))
                    await Assert.ThrowsAsync<PostgresException>(
                        () => second.ExecuteNonQueryAsync(ct));
                await tx.RollbackAsync(ct);
            }
            Assert.False(await ExistsAsync(schema, "a"));
        }
        finally { await CleanupAsync(schema); }
    }

    private static string DependentScript(string schema) =>
        $"CREATE TABLE {schema}.pipeline (id INTEGER NOT NULL);" +
        $" ALTER TABLE {schema}.pipeline ADD COLUMN extra INTEGER NULL;" +
        $" CREATE INDEX ix_pipeline_extra ON {schema}.pipeline(extra)";

    [Fact]
    public async Task DependentCreateThenAddThenIndex_OneApprovalOnePhysicalAtomicCommit()
    {
        var schema = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var sql = DependentScript(schema);
            var runtime = Runtime(schema, dependentEnabled: true);
            var preview = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), previewOnly: true, approvalProvider: null, ct);
            Assert.Equal(DdlRuntimeStatus.Preview, preview.Status);
            Assert.NotNull(preview.BatchPreview);
            Assert.Equal(HsSqlAgent.SqlCore.Ddl.DdlBatchTopology.DependentCreateChain,
                preview.BatchPreview.Plan.Topology);
            Assert.Equal(3, preview.BatchPreview.Steps.Count);
            Assert.Equal(1, preview.BatchPreview.ExpectedColumns[0].Count);
            Assert.Equal(2, preview.BatchPreview.ExpectedColumns[1].Count);
            Assert.Equal(2, preview.BatchPreview.ExpectedColumns[2].Count);
            Assert.Contains("hypothetical", preview.BatchPreview.HumanReadableDiff,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(await ExistsAsync(schema, "pipeline"));

            var approvals = 0;
            var approval = new Approver((request, _) =>
            {
                approvals++;
                Assert.Contains("ADD COLUMN", request.HumanReadableDiff);
                Assert.Contains("CREATE INDEX", request.HumanReadableDiff);
                return ValueTask.FromResult(new DdlApprovalDecision(
                    DdlApprovalState.Approved, request.RequestId,
                    request.ApprovalFingerprint, "human:separate-reviewer"));
            });
            var committed = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), previewOnly: false, approval, ct);
            Assert.True(committed.Status == DdlRuntimeStatus.Committed,
                "Dependent chain should Commit, actual " + committed.Status
                + " (" + committed.ErrorCode + "): " + committed.Message);
            Assert.True(committed.CommitConfirmed);
            Assert.Equal(1, approvals);
            Assert.True(await ExistsAsync(schema, "pipeline"));

            await using var c = new NpgsqlConnection(fixture.ConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM pg_catalog.pg_attribute a "
                + "JOIN pg_catalog.pg_class c ON c.oid=a.attrelid "
                + "JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace "
                + "WHERE n.nspname=@schema AND c.relname='pipeline' AND a.attnum>0 "
                + "AND NOT a.attisdropped) = 2 "
                + "AND EXISTS (SELECT 1 FROM pg_catalog.pg_class c "
                + "JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace "
                + "WHERE n.nspname=@schema AND c.relname='ix_pipeline_extra' "
                + "AND c.relkind='i')", c);
            cmd.Parameters.AddWithValue("schema", schema);
            Assert.True((bool)(await cmd.ExecuteScalarAsync(ct))!);
        }
        finally { await CleanupAsync(schema); }
    }

    [Fact]
    public async Task DependentBatchRequiresExtraGateAndIndependentApproval()
    {
        var schema = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var sql = DependentScript(schema);
            var off = await Runtime(schema).RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), false, Decision(DdlApprovalState.Approved), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, off.Status);
            Assert.Equal("ddl.batch_dependency_disabled", off.ErrorCode);
            var denied = await Runtime(schema, dependentEnabled: true).RunAsync(
                Provider(), fixture.ConnectionString, sql, Actor(), false,
                Decision(DdlApprovalState.Rejected), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, denied.Status);
            var forged = await Runtime(schema, dependentEnabled: true).RunAsync(
                Provider(), fixture.ConnectionString, sql, Actor(), false,
                new Approver((request, _) => ValueTask.FromResult(new DdlApprovalDecision(
                    DdlApprovalState.Approved, request.RequestId, new string('F', 64),
                    "human:reviewer"))), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, forged.Status);
            Assert.False(await ExistsAsync(schema, "pipeline"));
        }
        finally { await CleanupAsync(schema); }
    }

    [Fact]
    public async Task DependentBatchExternalSchemaDriftRejectsEntireChainWithoutWrite()
    {
        var schema = await SetupAsync();
        try
        {
            var sql = DependentScript(schema);
            var result = await Runtime(schema, dependentEnabled: true).RunAsync(
                Provider(), fixture.ConnectionString, sql, Actor(), false,
                new Approver(async (request, _) =>
                {
                    await CommandAsync("CREATE TABLE \"" + schema + "\".pipeline (outside TEXT)");
                    return new DdlApprovalDecision(DdlApprovalState.Approved,
                        request.RequestId, request.ApprovalFingerprint, "human:reviewer");
                }), TestContext.Current.CancellationToken);
            Assert.Equal(DdlRuntimeStatus.Rejected, result.Status);
            Assert.True(await ExistsAsync(schema, "pipeline"));
            await using var c = new NpgsqlConnection(fixture.ConnectionString);
            await c.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM pg_catalog.pg_attribute a "
                + "JOIN pg_catalog.pg_class c ON c.oid=a.attrelid "
                + "JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace "
                + "WHERE n.nspname=@schema AND c.relname='pipeline' AND a.attnum>0 "
                + "AND NOT a.attisdropped", c);
            cmd.Parameters.AddWithValue("schema", schema);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally { await CleanupAsync(schema); }
    }

    [Fact]
    public async Task DependentBatchExistingIndexNameNeverLeavesPartialSchema()
    {
        var schema = await SetupAsync();
        try
        {
            await CommandAsync("CREATE TABLE \"" + schema + "\".other (id INTEGER)");
            await CommandAsync("CREATE INDEX ix_pipeline_extra ON \"" + schema + "\".other(id)");
            var result = await Runtime(schema, dependentEnabled: true).RunAsync(
                Provider(), fixture.ConnectionString, DependentScript(schema),
                Actor(), false, Decision(DdlApprovalState.Approved),
                TestContext.Current.CancellationToken);
            Assert.True(result.Status is DdlRuntimeStatus.Rejected or DdlRuntimeStatus.Failed);
            Assert.False(await ExistsAsync(schema, "pipeline"));
        }
        finally { await CleanupAsync(schema); }
    }

}
