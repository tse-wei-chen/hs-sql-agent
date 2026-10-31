using System.Data.Common;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using Moq;
using Npgsql;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// G6 S15 real PostgreSQL 16 differential: only a proven empty, non-special
/// table can cross the independently-approved destructive DDL boundary.
/// </summary>
public sealed class PostgresDdlDestructiveLiveTests(PostgresDdlLiveFixture fixture)
    : IClassFixture<PostgresDdlLiveFixture>
{
    private sealed class Connections : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new NpgsqlConnection(connectionString);
    }

    private sealed class Approver(
        Func<DdlApprovalRequest, CancellationToken, ValueTask<DdlApprovalDecision>> respond)
        : IDdlApprovalProvider
    {
        public ValueTask<DdlApprovalDecision> RequestAsync(
            DdlApprovalRequest request, CancellationToken token = default) => respond(request, token);
    }

    private ISqlProvider Provider()
    {
        var mock = new Mock<ISqlProvider>(MockBehavior.Strict);
        mock.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        mock.SetupGet(x => x.Connections).Returns(new Connections());
        return mock.Object;
    }

    private static PostgresDdlRuntime Runtime(string schema, bool enableDrop) =>
        new(new PostgresDdlExecutionOptions
        {
            Enabled = true,
            AllowedSchemas = new HashSet<string>(StringComparer.Ordinal) { schema },
            AllowCreateTable = true,
            AllowDropEmptyTable = enableDrop,
            PolicyRevision = "g6-postgres-destructive-live"
        });

    private static IDdlApprovalProvider Approval(DdlApprovalState state) =>
        new Approver((request, _) => ValueTask.FromResult(
            new DdlApprovalDecision(state, request.RequestId,
                request.ApprovalFingerprint, "human:independent-reviewer")));

    private static DdlActorContext Actor() =>
        new("mcp-key:777", "db-management:99", "ddl_live");

    private async Task<string> SetupAsync()
    {
        var schema = "ddl_g6_" + Guid.NewGuid().ToString("N");
        await using var c = new NpgsqlConnection(fixture.ConnectionString);
        await c.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "CREATE SCHEMA \"" + schema + "\"; CREATE TABLE \"" + schema
            + "\".widgets (id INTEGER)", c);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return schema;
    }

    private async Task ExecAsync(string sql)
    {
        await using var c = new NpgsqlConnection(fixture.ConnectionString);
        await c.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, c);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<bool> ExistsAsync(string schema)
    {
        await using var c = new NpgsqlConnection(fixture.ConnectionString);
        await c.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM pg_catalog.pg_class c "
            + "JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace "
            + "WHERE n.nspname=@schema AND c.relname='widgets')", c);
        command.Parameters.AddWithValue("schema", schema);
        return (bool)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private Task CleanupAsync(string schema) =>
        ExecAsync("DROP SCHEMA \"" + schema + "\" CASCADE");

    [Fact]
    public async Task EmptyDropRequiresSeparatePolicyAndHumanApprovalThenConfirmsPhysicalAbsence()
    {
        var schema = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        var sql = "DROP TABLE " + schema + ".widgets RESTRICT";
        try
        {
            var denied = await Runtime(schema, enableDrop: false).RunAsync(
                Provider(), fixture.ConnectionString, sql, Actor(), false,
                Approval(DdlApprovalState.Approved), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, denied.Status);
            Assert.True(await ExistsAsync(schema));

            var runtime = Runtime(schema, enableDrop: true);
            var preview = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), true, null, ct);
            Assert.Equal(DdlRuntimeStatus.Preview, preview.Status);
            Assert.False(preview.CommitConfirmed);
            Assert.True(preview.Preview!.StaticPreview.Plan.Impact.MayLoseData);
            Assert.Equal(DdlSchemaOperation.DropTable,
                preview.Preview.StaticPreview.Plan.Changes[0].Operation);
            Assert.Contains("DELETE", preview.Preview.HumanReadableDiff,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(await ExistsAsync(schema));

            var rejected = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), false, Approval(DdlApprovalState.Rejected), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, rejected.Status);
            Assert.True(await ExistsAsync(schema));

            var forged = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), false,
                new Approver((request, _) => ValueTask.FromResult(
                    new DdlApprovalDecision(DdlApprovalState.Approved,
                        request.RequestId, new string('0', 64), "human:reviewer"))), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, forged.Status);
            Assert.True(await ExistsAsync(schema));

            var committed = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), false, Approval(DdlApprovalState.Approved), ct);
            Assert.Equal(DdlRuntimeStatus.Committed, committed.Status);
            Assert.True(committed.CommitConfirmed);
            Assert.False(await ExistsAsync(schema));
        }
        finally { await CleanupAsync(schema); }
    }

    [Fact]
    public async Task NonEmptyOrConcurrentInsertNeverDropsRows()
    {
        var schema = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        var sql = "DROP TABLE " + schema + ".widgets";
        try
        {
            await ExecAsync("INSERT INTO \"" + schema + "\".widgets VALUES (7)");
            var runtime = Runtime(schema, enableDrop: true);
            var nonempty = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), false, Approval(DdlApprovalState.Approved), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, nonempty.Status);
            Assert.True(await ExistsAsync(schema));

            await ExecAsync("DELETE FROM \"" + schema + "\".widgets");
            var populatedAfterPreview = await runtime.RunAsync(
                Provider(), fixture.ConnectionString, sql, Actor(), false,
                new Approver(async (request, token) =>
                {
                    await ExecAsync("INSERT INTO \"" + schema + "\".widgets VALUES (42)");
                    return new DdlApprovalDecision(DdlApprovalState.Approved,
                        request.RequestId, request.ApprovalFingerprint, "human:reviewer");
                }), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, populatedAfterPreview.Status);
            Assert.True(await ExistsAsync(schema));
            await using var c = new NpgsqlConnection(fixture.ConnectionString);
            await c.OpenAsync(ct);
            await using var count = new NpgsqlCommand(
                "SELECT COUNT(*) FROM \"" + schema + "\".widgets", c);
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(ct))!);
        }
        finally { await CleanupAsync(schema); }
    }

    [Fact]
    public async Task DependentOrSpecialTableIsNotExecutableAndNativePostgresDropRollsBack()
    {
        var schema = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        var sql = "DROP TABLE " + schema + ".widgets";
        try
        {
            await ExecAsync("CREATE VIEW \"" + schema
                + "\".dependent AS SELECT id FROM \"" + schema + "\".widgets");
            var guarded = await Runtime(schema, true).RunAsync(
                Provider(), fixture.ConnectionString, sql, Actor(), false,
                Approval(DdlApprovalState.Approved), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, guarded.Status);
            Assert.True(await ExistsAsync(schema));
            await ExecAsync("DROP VIEW \"" + schema + "\".dependent");

            // Reject physical schema dependencies even when no data rows exist.
            await ExecAsync("CREATE INDEX idx_drop_guard ON \"" + schema + "\".widgets(id)");
            var indexed = await Runtime(schema, true).RunAsync(
                Provider(), fixture.ConnectionString, sql, Actor(), false,
                Approval(DdlApprovalState.Approved), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, indexed.Status);
            Assert.True(await ExistsAsync(schema));
            await ExecAsync("DROP INDEX \"" + schema + "\".idx_drop_guard");

            await ExecAsync("ALTER TABLE \"" + schema
                + "\".widgets ADD COLUMN with_default INTEGER DEFAULT 9");
            var defaulted = await Runtime(schema, true).RunAsync(
                Provider(), fixture.ConnectionString, sql, Actor(), false,
                Approval(DdlApprovalState.Approved), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, defaulted.Status);
            Assert.True(await ExistsAsync(schema));
            await ExecAsync("ALTER TABLE \"" + schema + "\".widgets DROP COLUMN with_default");


            // Independent database behavior oracle: PostgreSQL DROP is
            // transactional; a direct rollback must restore the table.
            await using var c = new NpgsqlConnection(fixture.ConnectionString);
            await c.OpenAsync(ct);
            await using (var tx = await c.BeginTransactionAsync(ct))
            {
                await using (var cmd = new NpgsqlCommand(
                    "DROP TABLE \"" + schema + "\".widgets RESTRICT", c, tx))
                    await cmd.ExecuteNonQueryAsync(ct);
                await tx.RollbackAsync(ct);
            }
            Assert.True(await ExistsAsync(schema));
        }
        finally { await CleanupAsync(schema); }
    }
}
