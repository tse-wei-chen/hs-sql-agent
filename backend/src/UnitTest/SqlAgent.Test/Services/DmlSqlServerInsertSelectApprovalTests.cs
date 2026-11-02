using HsSqlAgent.Provider.SqlServer;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;
using Microsoft.Data.SqlClient;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>Actual SQL Server 2022 physical source and target, no mock catalog or locks.</summary>
public sealed class DmlSqlServerInsertSelectApprovalTests
{
    private const string Approval = "sqlserver2022-physical-source-m09-approval-v1";

    [Fact]
    public async Task TwoPhysicalTables_ApprovedSourceValues_CommitOneNativeInsert()
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id = 1";
            var plan = await CreatePlanAsync(provider, native, cs, target, source, sql);
            Assert.Equal(DmlApprovalMode.InsertSelect, plan.ApprovalMode);
            Assert.Equal(2, plan.SchemaSnapshot!.Tables.Count);
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            Assert.Equal(1, preview.AffectedRows);
            Assert.Equal("Carol", Assert.Single(preview.Rows)["name"]);
            var committed = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            Assert.Empty(committed.ReturnedRows);
            Assert.Equal(3, await ScalarAsync(cs, $"SELECT count(*) FROM dbo.[{target}]"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
        });
    }

    [Fact]
    public async Task ApprovedSourceValueDrift_FailsClosedWithoutInserting()
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, source,
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(cs, $"UPDATE dbo.[{source}] SET name = 'Eve' WHERE id = 1");
            var result = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version);
            Assert.False(result.Committed);
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT count(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task SourceIndexDrift_RejectsOldApprovalBeforeMutation()
    {
        await WithTablesAsync(async (cs, target, source, version) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var plan = await CreatePlanAsync(provider, native, cs, target, source,
                $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id = 1");
            var coordinator = new DmlCoordinator(provider.Connections, schemaFreshnessVerifier: native);
            var preview = await coordinator.PreviewAsync(
                cs, plan, Approval, TestContext.Current.CancellationToken, version);
            await ExecuteAsync(cs, $"CREATE INDEX [ix_source_name] ON dbo.[{source}] ([name])");
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.CommitAsync(
                cs, plan, preview.Challenge, Approval, TestContext.Current.CancellationToken, version));
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT count(*) FROM dbo.[{target}]"));
        });
    }

    [Fact]
    public async Task MissingIndependentSourceGrant_DeniesBeforeExecutablePlan()
    {
        await WithTablesAsync(async (cs, target, source, _) =>
        {
            var provider = new MsSqlServerProvider();
            var native = new NativeDmlSchemaSnapshotService();
            var sql = $"INSERT INTO dbo.[{target}] (name) SELECT name FROM dbo.[{source}] WHERE id = 1";
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                CreatePlanAsync(provider, native, cs, target, source, sql, grantSource: false));
            Assert.Equal(2, await ScalarAsync(cs, $"SELECT count(*) FROM dbo.[{target}]"));
        });
    }

    [Theory]
    [InlineData("INSERT INTO dbo.t (name) SELECT name FROM dbo.t WHERE id = 1")]
    [InlineData("INSERT INTO dbo.t (name) SELECT name FROM dbo.s WHERE id = 1 OR id = 2")]
    [InlineData("INSERT INTO dbo.t (name) SELECT upper(name) FROM dbo.s WHERE id = 1")]
    [InlineData("INSERT INTO dbo.t (name) OUTPUT INSERTED.id SELECT name FROM dbo.s WHERE id = 1")]
    public void UnprovenHostShapes_DoNotGainSqlServerSourceApproval(string sql)
    {
        try
        {
            var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer);
            var insert = Assert.IsType<HsSqlAgent.SqlCore.Core.Ast.InsertStatement>(parsed.Statement);
            Assert.ThrowsAny<Exception>(() => NativeInsertSelectSafety.RequireShape(
                insert, SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer, 2));
        }
        catch (HsSqlAgent.SqlCore.Core.Compilation.SqlCompilationException)
        {
            // A stricter F# syntax rejection is also fail-closed.
        }
    }

    private static async Task<ValidatedDmlPlan> CreatePlanAsync(
        MsSqlServerProvider provider, NativeDmlSchemaSnapshotService native, string cs,
        string target, string source, string sql, bool grantSource = true)
    {
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer);
        await using var connection = provider.Connections.Create(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var snapshot = await native.CaptureAsync(
            connection, SqlAgentToolType.MsSqlServer, parsed, TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        var grants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo." + target };
        if (grantSource) grants.Add("dbo." + source);
        return await new DmlPlanFactory(provider.Metadata).CreateWithMetadataConnectionAsync(
            connection, cs, parsed, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver2022-m09-v1", grants).WithSchemaSnapshot(snapshot),
            maxAffectedRows: 2, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task WithTablesAsync(Func<string, string, string, string, Task> action)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("M09 SQL Server requires actual TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("Requires TEST_SQLSERVER_CONNECTION.");
        }
        var tag = Guid.NewGuid().ToString("N")[..12];
        var target = "m09_dst_" + tag;
        var source = "m09_src_" + tag;
        await ExecuteAsync(cs!, $"CREATE TABLE dbo.[{target}] (id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, name NVARCHAR(64) NOT NULL)");
        try
        {
            await ExecuteAsync(cs!, $"CREATE TABLE dbo.[{source}] (id INT NOT NULL PRIMARY KEY, name NVARCHAR(64) NOT NULL)");
            try
            {
                await ExecuteAsync(cs!, $"INSERT INTO dbo.[{target}] (name) VALUES ('Alice'),('Bob')");
                await ExecuteAsync(cs!, $"INSERT INTO dbo.[{source}] (id,name) VALUES (1,'Carol'),(2,'Dave')");
                await using var conn = new SqlConnection(cs);
                await conn.OpenAsync(TestContext.Current.CancellationToken);
                await action(cs!, target, source, conn.ServerVersion.Trim());
            }
            finally { await ExecuteAsync(cs!, $"DROP TABLE IF EXISTS dbo.[{source}]"); }
        }
        finally { await ExecuteAsync(cs!, $"DROP TABLE IF EXISTS dbo.[{target}]"); }
    }

    private static async Task ExecuteAsync(string cs, string sql)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(sql, conn);
        command.CommandTimeout = 10;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarAsync(string cs, string sql)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(sql, conn);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
