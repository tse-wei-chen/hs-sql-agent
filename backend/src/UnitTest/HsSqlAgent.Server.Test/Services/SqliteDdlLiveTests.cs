using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.Sqlite;
using Moq;
using System.Data.Common;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// G4 SQLite real provider contract: filesystem identity, schema cookie,
/// BEGIN IMMEDIATE freshness, native index SQL, and independent DDL approval.
/// Initially RED: SqliteDdlRuntime does not exist.
/// </summary>
public sealed class SqliteDdlLiveTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(),
        "hs_sqlite_ddl_" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string _connectionString;
    private readonly Mock<ISqlProvider> _provider = new(MockBehavior.Strict);

    public SqliteDdlLiveTests()
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _file,
            Pooling = false
        }.ToString();
        _provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Sqlite);
        _provider.SetupGet(x => x.Connections).Returns(
            new SqliteDdlConnectionFactory(_connectionString));
    }

    private sealed class SqliteDdlConnectionFactory(string configured) : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString)
        {
            Assert.Equal(configured, connectionString);
            return new SqliteConnection(connectionString);
        }
    }

    private DdlActorContext Actor() => new("mcp-key:4", "db-management:8", _file);

    private static SqliteDdlExecutionOptions Enabled() => new()
    {
        Enabled = true,
        AllowCreateTable = true,
        AllowAddColumn = true,
        AllowCreateIndex = true,
        PolicyRevision = "sqlite-g4-live-tests"
    };

    private static IDdlApprovalProvider Approved() =>
        new Approval((request, _) => ValueTask.FromResult(
            new DdlApprovalDecision(DdlApprovalState.Approved, request.RequestId,
                request.ApprovalFingerprint, "reviewer:separate-person")));

    private sealed class Approval(
        Func<DdlApprovalRequest, CancellationToken, ValueTask<DdlApprovalDecision>> respond)
        : IDdlApprovalProvider
    {
        public ValueTask<DdlApprovalDecision> RequestAsync(
            DdlApprovalRequest request, CancellationToken cancellationToken = default) =>
            respond(request, cancellationToken);
    }

    public async ValueTask InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_file)) File.Delete(_file);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void NativeIndexRenderer_ExecutesOnMainDatabase_NotInvalidQualifiedTable()
    {
        var options = new DdlCompilationOptions(SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite);
        var table = SqlCoreFacade.CompileDdl(
            "CREATE TABLE main.widgets (id INTEGER NOT NULL, nick VARCHAR(40))", options);
        var index = SqlCoreFacade.CompileDdl(
            "CREATE INDEX idx_widgets_nick ON main.widgets (nick)", options);
        Assert.DoesNotContain("ON \"main\".\"widgets\"", index.Command.Sql,
            StringComparison.Ordinal);
        Assert.Contains("ON \"WIDGETS\"", index.Command.Sql, StringComparison.Ordinal);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = table.Command.Sql;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = index.Command.Sql;
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task DefaultDisabledOrMissingIndependentApproval_CannotOpenWriteConnection()
    {
        var disabled = new SqliteDdlRuntime();
        Assert.False(disabled.Enabled);
        var result = await disabled.RunAsync(_provider.Object, _connectionString,
            "CREATE TABLE main.widgets (id INTEGER)", Actor(), false, null,
            TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, result.Status);
        Assert.False(result.CommitConfirmed);

        var active = new SqliteDdlRuntime(Enabled());
        var denied = await active.RunAsync(_provider.Object, _connectionString,
            "CREATE TABLE main.widgets (id INTEGER)", Actor(), false, null,
            TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, denied.Status);
        Assert.False(denied.CommitConfirmed);
    }

    [Fact]
    public async Task PreviewOnly_NoMutation_AndIndependentRejectIsReadOnly()
    {
        var runtime = new SqliteDdlRuntime(Enabled());
        const string sql = "CREATE TABLE main.widgets (id INTEGER NOT NULL)";
        var preview = await runtime.RunAsync(_provider.Object, _connectionString,
            sql, Actor(), true, null, TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Preview, preview.Status);
        Assert.NotNull(preview.Preview);
        Assert.Equal("main", preview.Preview!.Before.SchemaName);
        Assert.Null(preview.Preview.Before.TableDefinition);
        Assert.Equal(1, preview.Preview.ExpectedColumns.Count);
        Assert.False(preview.CommitConfirmed);
        Assert.False(await HasTableAsync("widgets"));

        var rejected = await runtime.RunAsync(_provider.Object, _connectionString,
            sql, Actor(), false, new Approval((r, _) => ValueTask.FromResult(
                new DdlApprovalDecision(DdlApprovalState.Rejected, r.RequestId,
                    r.ApprovalFingerprint, "reviewer:human"))),
            TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, rejected.Status);
        Assert.False(await HasTableAsync("widgets"));
    }

    [Fact]
    public async Task ApprovedCreateAddIndex_CommitAndPhysicalSchemaParity()
    {
        var runtime = new SqliteDdlRuntime(Enabled());
        foreach (var sql in new[]
        {
            "CREATE TABLE main.widgets (id INTEGER NOT NULL)",
            "ALTER TABLE main.widgets ADD COLUMN nick VARCHAR(40)",
            "CREATE INDEX idx_widgets_nick ON main.widgets (nick)"
        })
        {
            var committed = await runtime.RunAsync(_provider.Object, _connectionString,
                sql, Actor(), false, Approved(), TestContext.Current.CancellationToken);
            Assert.Equal(DdlRuntimeStatus.Committed, committed.Status);
            Assert.True(committed.CommitConfirmed);
            Assert.NotNull(committed.Preview);
        }
        Assert.True(await HasTableAsync("widgets"));
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('widgets') WHERE name COLLATE NOCASE IN ('id','nick')";
        Assert.Equal(2L, Convert.ToInt64(
            await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND name='idx_widgets_nick' COLLATE NOCASE";
        Assert.Equal(1L, Convert.ToInt64(
            await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ApprovalTimeSchemaDrift_RejectsWithoutOverwrite()
    {
        var runtime = new SqliteDdlRuntime(Enabled());
        const string sql = "CREATE TABLE main.widgets (id INTEGER)";
        var approval = new Approval(async (req, ct) =>
        {
            await using var another = new SqliteConnection(_connectionString);
            await another.OpenAsync(ct);
            await using var cmd = another.CreateCommand();
            cmd.CommandText = "CREATE TABLE main.widgets (unexpected TEXT)";
            await cmd.ExecuteNonQueryAsync(ct);
            return new DdlApprovalDecision(DdlApprovalState.Approved,
                req.RequestId, req.ApprovalFingerprint, "reviewer:human");
        });
        var result = await runtime.RunAsync(_provider.Object, _connectionString,
            sql, Actor(), false, approval, TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, result.Status);
        Assert.Equal("ddl.schema_drift", result.ErrorCode);
        Assert.True(await HasTableAsync("widgets"));
    }

    [Fact]
    public async Task TamperedApprovalAndWrongFileTarget_FailClosed()
    {
        var runtime = new SqliteDdlRuntime(Enabled());
        const string sql = "CREATE TABLE main.widgets (id INTEGER)";
        var wrong = await runtime.RunAsync(_provider.Object, _connectionString,
            sql, new DdlActorContext("mcp-key:4", "db-management:8", "other.db"),
            false, Approved(), TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, wrong.Status);
        var tamper = await runtime.RunAsync(_provider.Object, _connectionString,
            sql, Actor(), false, new Approval((req, _) => ValueTask.FromResult(
                new DdlApprovalDecision(DdlApprovalState.Approved,
                    req.RequestId, new string('A', 64), "reviewer:human"))),
            TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, tamper.Status);
        Assert.False(await HasTableAsync("widgets"));
    }

    [Fact]
    public async Task PhysicalObjectCollision_DiffersOnlyByAsciiCase_IsRejected()
    {
        await using (var db = new SqliteConnection(_connectionString))
        {
            await db.OpenAsync(TestContext.Current.CancellationToken);
            await using var setup = db.CreateCommand();
            setup.CommandText = "CREATE TABLE widgets (id INTEGER NOT NULL)";
            await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var runtime = new SqliteDdlRuntime(Enabled());
        var result = await runtime.RunAsync(_provider.Object, _connectionString,
            "CREATE TABLE main.WIDGETS (id INTEGER)", Actor(), false, Approved(),
            TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, result.Status);
        Assert.Equal("ddl.physical_precondition", result.ErrorCode);
        var duplicateColumn = await runtime.RunAsync(_provider.Object, _connectionString,
            "ALTER TABLE main.widgets ADD COLUMN ID INTEGER", Actor(), false, Approved(),
            TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, duplicateColumn.Status);
        Assert.Equal("ddl.physical_precondition", duplicateColumn.ErrorCode);
        Assert.True(await HasTableAsync("WIDGETS"));
    }

    [Fact]
    public async Task NativeCompiledDdl_InBeginImmediateRollback_RestoresSchemaCookie()
    {
        var plan = SqlCoreFacade.CompileDdl(
            "CREATE TABLE main.rollback_target (id INTEGER NOT NULL)",
            new DdlCompilationOptions(SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite));
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        async Task<long> CookieAsync()
        {
            await using var query = db.CreateCommand();
            query.CommandText = "PRAGMA main.schema_version";
            return Convert.ToInt64(
                await query.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        var before = await CookieAsync();
        await using (var tx = await db.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, TestContext.Current.CancellationToken))
        {
            await using var execute = db.CreateCommand();
            execute.Transaction = (SqliteTransaction)tx;
            execute.CommandText = plan.Command.Sql;
            await execute.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await tx.RollbackAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(before, await CookieAsync());
        await using var existence = db.CreateCommand();
        existence.CommandText =
            "SELECT COUNT(*) FROM main.sqlite_schema WHERE name='ROLLBACK_TARGET'";
        Assert.Equal(0L, Convert.ToInt64(
            await existence.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("CREATE TABLE widgets (id INTEGER)")]
    [InlineData("DROP TABLE main.widgets")]
    [InlineData("CREATE UNIQUE INDEX idx ON main.widgets (id)")]
    [InlineData("ALTER TABLE main.widgets DROP COLUMN id")]
    [InlineData("CREATE TABLE temp.widgets (id INTEGER)")]
    [InlineData("CREATE TABLE main.widgets (id INTEGER); DROP TABLE main.widgets")]
    public async Task UnprovenOrOutOfScopeDdlNeverExecutes(string sql)
    {
        var runtime = new SqliteDdlRuntime(Enabled());
        var result = await runtime.RunAsync(_provider.Object, _connectionString,
            sql, Actor(), false, Approved(), TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, result.Status);
    }

    private async Task<bool> HasTableAsync(string table)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM main.sqlite_schema WHERE type='table' AND name=@name COLLATE NOCASE)";
        cmd.Parameters.AddWithValue("@name", table);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)) == 1L;
    }
}
