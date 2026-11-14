using System.Data.Common;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.Provider.Abstractions;
using Moq;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Independent real PostgreSQL 16 transaction and metadata oracle for G3.
/// The runtime uses driver-neutral DbConnection and only the F# compiled plan;
/// the fixture executes setup/teardown SQL out of band.
/// </summary>
public sealed class PostgresDdlLiveFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("ddl_live")
        .WithUsername("ddl_user")
        .WithPassword("DdlTestPass123!")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() =>
        await _container.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() =>
        await _container.DisposeAsync();
}

public sealed class PostgresDdlLiveTests(PostgresDdlLiveFixture fixture)
    : IClassFixture<PostgresDdlLiveFixture>
{
    private sealed class Connections : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new NpgsqlConnection(connectionString);
    }

    private sealed class Approver(
        Func<DdlApprovalRequest, CancellationToken, ValueTask<DdlApprovalDecision>> callback)
        : IDdlApprovalProvider
    {
        public ValueTask<DdlApprovalDecision> RequestAsync(
            DdlApprovalRequest request, CancellationToken token = default) =>
            callback(request, token);
    }

    private static IDdlApprovalProvider Approve() =>
        new Approver((request, _) => ValueTask.FromResult(
            new DdlApprovalDecision(DdlApprovalState.Approved,
                request.RequestId, request.ApprovalFingerprint, "human:reviewer")));

    private ISqlProvider Provider()
    {
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(p => p.Type).Returns(SqlAgentToolType.Postgres);
        provider.SetupGet(p => p.Connections).Returns(new Connections());
        return provider.Object;
    }

    private static PostgresDdlRuntime Runtime(string schema) =>
        new(new PostgresDdlExecutionOptions
        {
            Enabled = true,
            AllowedSchemas = new HashSet<string>(StringComparer.Ordinal) { schema },
            AllowCreateTable = true,
            AllowAddColumn = true,
            AllowCreateIndex = true,
            PolicyRevision = "live-test-v1"
        });

    private static DdlActorContext Actor() =>
        new("mcp-key:777", "db-management:99", "ddl_live");

    private async Task<string> NewSchemaAsync()
    {
        var schema = "ddl_live_" + Guid.NewGuid().ToString("N");
        await using var db = new NpgsqlConnection(fixture.ConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", db);
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return schema;
    }

    private async Task DropSchemaAsync(string schema)
    {
        await using var db = new NpgsqlConnection(fixture.ConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", db);
        await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<bool> TableExistsAsync(string schema)
    {
        await using var db = new NpgsqlConnection(fixture.ConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var query = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class c " +
            "JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace " +
            "WHERE n.nspname=@schema AND c.relname='widgets')", db);
        query.Parameters.AddWithValue("schema", schema);
        return (bool)(await query.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task PreviewOnlyNeverExecutesAndIndependentApprovalRejects()
    {
        var schema = await NewSchemaAsync();
        try
        {
            var runtime = Runtime(schema);
            var sql = $"CREATE TABLE {schema}.widgets (id INTEGER NOT NULL)";
            var preview = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), previewOnly: true, approvalProvider: null,
                TestContext.Current.CancellationToken);
            Assert.Equal(DdlRuntimeStatus.Preview, preview.Status);
            Assert.False(preview.CommitConfirmed);
            Assert.Null(preview.Preview?.Before.TableOid);
            Assert.NotNull(preview.Preview?.Before.SchemaOid);
            Assert.Equal(64, preview.Preview!.Before.ServerIdentity.Length);
            Assert.True(preview.Preview!.Before.ServerIdentity.All(Uri.IsHexDigit));
            Assert.False(await TableExistsAsync(schema));

            var rejected = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), previewOnly: false,
                approvalProvider: new Approver((request, _) =>
                    ValueTask.FromResult(new DdlApprovalDecision(DdlApprovalState.Rejected,
                        request.RequestId, request.ApprovalFingerprint, "human:reviewer"))),
                TestContext.Current.CancellationToken);
            Assert.Equal(DdlRuntimeStatus.Rejected, rejected.Status);
            Assert.False(await TableExistsAsync(schema));
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task ApprovedCreateAddAndIndex_CommitOnceAndMatchPhysicalSchema()
    {
        var schema = await NewSchemaAsync();
        try
        {
            var runtime = Runtime(schema);
            var provider = Provider();
            foreach (var ddl in new[]
            {
                $"CREATE TABLE {schema}.widgets (id INTEGER NOT NULL)",
                $"ALTER TABLE {schema}.widgets ADD COLUMN nick VARCHAR(40)",
                $"CREATE INDEX idx_widgets_nick ON {schema}.widgets (nick)"
            })
            {
                var result = await runtime.RunAsync(provider, fixture.ConnectionString,
                    ddl, Actor(), previewOnly: false, Approve(),
                    TestContext.Current.CancellationToken);
                Assert.Equal(DdlRuntimeStatus.Committed, result.Status);
                Assert.True(result.CommitConfirmed);
                Assert.NotNull(result.Preview);
                Assert.True(result.Preview!.HumanReadableDiff.Contains("Expected columns:", StringComparison.Ordinal));
            }
            Assert.True(await TableExistsAsync(schema));
            await using var db = new NpgsqlConnection(fixture.ConnectionString);
            await db.OpenAsync(TestContext.Current.CancellationToken);
            await using var columns = new NpgsqlCommand(
                "SELECT attname::text FROM pg_catalog.pg_attribute a " +
                "JOIN pg_catalog.pg_class c ON c.oid=a.attrelid " +
                "JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace " +
                "WHERE n.nspname=@schema AND c.relname='widgets' " +
                "AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum", db);
            columns.Parameters.AddWithValue("schema", schema);
            var actualColumns = new List<string>();
            await using (var reader = await columns.ExecuteReaderAsync(TestContext.Current.CancellationToken))
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                    actualColumns.Add(reader.GetString(0));
            Assert.Equal(["id", "nick"], actualColumns);
            await using var index = new NpgsqlCommand(
                "SELECT COUNT(*) FROM pg_catalog.pg_indexes WHERE schemaname=@schema " +
                "AND tablename='widgets' AND indexname='idx_widgets_nick'", db);
            index.Parameters.AddWithValue("schema", schema);
            Assert.Equal(1L, (long)(await index.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task SchemaDriftBetweenPreviewAndCommit_FailsClosedWithoutSecondWrite()
    {
        var schema = await NewSchemaAsync();
        try
        {
            var sql = $"CREATE TABLE {schema}.widgets (id INTEGER NOT NULL)";
            var runtime = Runtime(schema);
            var approval = new Approver(async (request, token) =>
            {
                await using var db = new NpgsqlConnection(fixture.ConnectionString);
                await db.OpenAsync(token);
                await using var concurrent = new NpgsqlCommand(
                    $"CREATE TABLE {schema}.widgets (different TEXT)", db);
                await concurrent.ExecuteNonQueryAsync(token);
                return new DdlApprovalDecision(DdlApprovalState.Approved,
                    request.RequestId, request.ApprovalFingerprint, "human:reviewer");
            });
            var result = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), previewOnly: false, approval,
                TestContext.Current.CancellationToken);
            Assert.Equal(DdlRuntimeStatus.Rejected, result.Status);
            Assert.Equal("ddl.schema_drift", result.ErrorCode);
            Assert.True(await TableExistsAsync(schema));
            // The concurrently created table is not overwritten by the
            // approved, different shape in this transaction.
            Assert.Null(result.Preview!.Before.TableOid);
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task ForgedApprovalFingerprintAndWrongDatabaseIdentity_CannotCommit()
    {
        var schema = await NewSchemaAsync();
        try
        {
            var runtime = Runtime(schema);
            var sql = $"CREATE TABLE {schema}.widgets (id INTEGER)";
            var wrongDb = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, new DdlActorContext("mcp-key:777", "db-management:99", "wrong_db"),
                previewOnly: false, Approve(), TestContext.Current.CancellationToken);
            Assert.Equal(DdlRuntimeStatus.Rejected, wrongDb.Status);
            Assert.False(await TableExistsAsync(schema));
            var forged = await runtime.RunAsync(Provider(), fixture.ConnectionString,
                sql, Actor(), previewOnly: false,
                new Approver((request, _) => ValueTask.FromResult(
                    new DdlApprovalDecision(DdlApprovalState.Approved,
                        request.RequestId, new string('0', 64), "human:reviewer"))),
                TestContext.Current.CancellationToken);
            Assert.Equal(DdlRuntimeStatus.Rejected, forged.Status);
            Assert.False(await TableExistsAsync(schema));
        }
        finally { await DropSchemaAsync(schema); }
    }
}
