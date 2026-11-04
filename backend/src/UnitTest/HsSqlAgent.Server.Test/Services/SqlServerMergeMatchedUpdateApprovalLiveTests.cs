using Admin.Service.Models;
using HsSqlAgent.Provider.SqlServer;
using HsSqlAgent.Server.Services;
using Microsoft.Data.SqlClient;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>G9 real SQL Server 2022 Host MERGE MATCHED UPDATE: complete value-sensitive approval.</summary>
public sealed class SqlServerMergeMatchedUpdateApprovalLiveTests
{
    private static readonly SecurityPolicyModel Policy = new() { DmlMaxAffectedRows = 1 };
    private static readonly DmlApprovalExecutionContext Actor =
        new("mcp-user:merge-update", "db-management:g9", SqlAgentToolType.MsSqlServer, "sqlserver-2022-real");
    private const string Action = "WHEN MATCHED THEN UPDATE SET quantity=s.quantity, status=s.status";

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task SingleSourceKey_TwoIntColumns_PreviewBeforeAfter_CommitAndReplay(bool exists, int affected)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            if (exists)
                await ExecuteAsync(cs, $"INSERT INTO dbo.[{table}](id,quantity,status) VALUES(7,10,3)", ct);
            var session = await PreviewAsync(provider, cs, table, runtime, ct);
            Assert.Equal("SqlServerMergeMatchedUpdate", session.Plan.ApprovalMode.ToString());
            Assert.Equal(affected, session.Preview.AffectedRows);
            Assert.Contains("WITH (HOLDLOCK)", session.Plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(affected, session.Preview.Rows.Length);
            if (exists)
            {
                var row = Assert.Single(session.Preview.Rows);
                Assert.Equal(7, Convert.ToInt32(row["id"]));
                Assert.Equal(10, Convert.ToInt32(row["__before_quantity"]));
                Assert.Equal(25, Convert.ToInt32(row["__after_quantity"]));
                Assert.Equal(3, Convert.ToInt32(row["__before_status"]));
                Assert.Equal(4, Convert.ToInt32(row["__after_status"]));
            }
            var committed = await runtime.CommitAsync(provider, cs, session, Policy,
                Allowed(table), Actor, ct);
            Assert.True(committed.Committed);
            Assert.Equal(affected, committed.AffectedRows);
            Assert.Empty(committed.ReturnedRows);
            Assert.Equal(exists ? 25 : 0,
                await ScalarAsync(cs, $"SELECT COALESCE(MAX(quantity),0) FROM dbo.[{table}] WHERE id=7", ct));
            Assert.Equal(exists ? 4 : 0,
                await ScalarAsync(cs, $"SELECT COALESCE(MAX(status),0) FROM dbo.[{table}] WHERE id=7", ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitAsync(provider, cs, session, Policy, Allowed(table), Actor, ct));
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task OneOrThreeDistinctPhysicalPayloadColumns_StayBoundToTheirBeforeValues(int payloadCount)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            if (payloadCount == 3)
                await ExecuteAsync(cs, $"ALTER TABLE dbo.[{table}] ADD revision INT NOT NULL", ct);
            await ExecuteAsync(cs, payloadCount == 3
                ? $"INSERT INTO dbo.[{table}](id,quantity,status,revision) VALUES(7,10,3,5)"
                : $"INSERT INTO dbo.[{table}](id,quantity,status) VALUES(7,10,3)", ct);
            var sql = payloadCount == 3
                ? $"MERGE INTO dbo.[{table}] AS t USING (VALUES(7,4,25,8)) AS s(id,status,quantity,revision) " +
                  "ON t.id=s.id WHEN MATCHED THEN UPDATE SET quantity=s.quantity,status=s.status,revision=s.revision;"
                : $"MERGE INTO dbo.[{table}] AS t USING (VALUES(7,25)) AS s(id,quantity) " +
                  "ON t.id=s.id WHEN MATCHED THEN UPDATE SET quantity=s.quantity;";
            var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
                provider, cs, sql, SqlAgentToolType.MsSqlServer, ct);
            var preview = await runtime.PreviewAsync(provider, cs, parsed,
                Policy, Allowed(table), Actor, ct);
            Assert.Equal(1, preview.Preview.AffectedRows);
            var evidence = Assert.Single(preview.Preview.Rows);
            Assert.Equal(10, Convert.ToInt32(evidence["__before_quantity"]));
            Assert.Equal(25, Convert.ToInt32(evidence["__after_quantity"]));
            if (payloadCount == 3)
            {
                Assert.Equal(3, Convert.ToInt32(evidence["__before_status"]));
                Assert.Equal(4, Convert.ToInt32(evidence["__after_status"]));
                Assert.Equal(5, Convert.ToInt32(evidence["__before_revision"]));
                Assert.Equal(8, Convert.ToInt32(evidence["__after_revision"]));
            }
            var committed = await runtime.CommitAsync(provider, cs, preview,
                Policy, Allowed(table), Actor, ct);
            Assert.True(committed.Committed);
            Assert.Equal(1, committed.AffectedRows);
            Assert.Equal(25, await ScalarAsync(cs,
                $"SELECT quantity FROM dbo.[{table}] WHERE id=7", ct));
            if (payloadCount == 3)
                Assert.Equal(8, await ScalarAsync(cs,
                    $"SELECT revision FROM dbo.[{table}] WHERE id=7", ct));
        });
    }

    [Theory]
    [InlineData("quantity", 18)]
    [InlineData("status", 9)]
    public async Task NonKeyBeforeValueChangedAfterPreview_CancelsWithoutOverwriting(string column, int value)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            await ExecuteAsync(cs, $"INSERT INTO dbo.[{table}](id,quantity,status) VALUES(7,10,3)", ct);
            var session = await PreviewAsync(provider, cs, table, runtime, ct);
            await ExecuteAsync(cs, $"UPDATE dbo.[{table}] SET {column}={value} WHERE id=7", ct);
            var rejected = await runtime.CommitAsync(provider, cs, session,
                Policy, Allowed(table), Actor, ct);
            Assert.False(rejected.Committed);
            Assert.Empty(rejected.ReturnedRows);
            Assert.Equal(column == "quantity" ? value : 10,
                await ScalarAsync(cs, $"SELECT quantity FROM dbo.[{table}] WHERE id=7", ct));
            Assert.Equal(column == "status" ? value : 3,
                await ScalarAsync(cs, $"SELECT status FROM dbo.[{table}] WHERE id=7", ct));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PhysicalKeyAppearsOrDisappearsAfterPreview_Cancels(bool originallyExists)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            if (originallyExists)
                await ExecuteAsync(cs, $"INSERT INTO dbo.[{table}](id,quantity,status) VALUES(7,10,3)", ct);
            var session = await PreviewAsync(provider, cs, table, runtime, ct);
            await ExecuteAsync(cs, originallyExists
                ? $"DELETE FROM dbo.[{table}] WHERE id=7"
                : $"INSERT INTO dbo.[{table}](id,quantity,status) VALUES(7,19,8)", ct);
            var result = await runtime.CommitAsync(provider, cs, session,
                Policy, Allowed(table), Actor, ct);
            Assert.False(result.Committed);
            Assert.Empty(result.ReturnedRows);
            Assert.Equal(originallyExists ? 0 : 19,
                await ScalarAsync(cs, $"SELECT COALESCE(MAX(quantity),0) FROM dbo.[{table}] WHERE id=7", ct));
        });
    }

    [Fact]
    public async Task RevokedPhysicalPrimaryKeyAfterApproval_CannotWrite()
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            await ExecuteAsync(cs, $"INSERT INTO dbo.[{table}](id,quantity,status) VALUES(7,10,3)", ct);
            var session = await PreviewAsync(provider, cs, table, runtime, ct);
            await ExecuteAsync(cs, $"ALTER TABLE dbo.[{table}] DROP CONSTRAINT [PK_{table}]", ct);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                runtime.CommitAsync(provider, cs, session, Policy, Allowed(table), Actor, ct));
            Assert.Equal(10, await ScalarAsync(cs, $"SELECT quantity FROM dbo.[{table}] WHERE id=7", ct));
        });
    }

    [Theory]
    [InlineData("WHEN MATCHED THEN UPDATE SET id=s.id")]
    [InlineData("WHEN MATCHED THEN UPDATE SET quantity=quantity+1")]
    [InlineData("WHEN MATCHED THEN UPDATE SET quantity=25")]
    [InlineData("WHEN MATCHED THEN UPDATE SET quantity=s.status")]
    [InlineData("WHEN MATCHED THEN UPDATE SET quantity=s.quantity, quantity=s.quantity")]
    [InlineData("WHEN MATCHED THEN UPDATE SET quantity=s.quantity WHEN NOT MATCHED THEN INSERT(id) VALUES(s.id)")]
    [InlineData("WHEN MATCHED THEN DELETE")]
    public async Task UnsafeAssignmentsAndOtherActions_RejectWithoutWrite(string action)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            var sql = Sql(table, action);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
                    provider, cs, sql, SqlAgentToolType.MsSqlServer, ct);
                await runtime.PreviewAsync(provider, cs, parsed, Policy, Allowed(table), Actor, ct);
                // DELETE is separately supported but NOT this UPDATE proof;
                // an UPDATE source with extra unused columns must not be treated
                // as the existing DELETE-only source shape.
            });
            Assert.Equal(0, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{table}]", ct));
        });
    }

    [Theory]
    [InlineData("25.0")]
    [InlineData("2.5e1")]
    [InlineData("'25'")]
    [InlineData("NULL")]
    [InlineData("25 + 0")]
    public async Task UnprovedProposedValueLiteral_RefusesApproval(string value)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            var sql = $"MERGE INTO dbo.[{table}] AS t USING (VALUES(7,{value},4)) " +
                $"AS s(id,quantity,status) ON t.id=s.id {Action};";
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
                    provider, cs, sql, SqlAgentToolType.MsSqlServer, ct);
                await runtime.PreviewAsync(provider, cs, parsed, Policy, Allowed(table), Actor, ct);
            });
        });
    }

    [Fact]
    public async Task PhysicalPayloadColumnIsNotNativeInt_RefusesApproval()
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            await ExecuteAsync(cs, $"ALTER TABLE dbo.[{table}] ALTER COLUMN status BIGINT NOT NULL", ct);
            await Assert.ThrowsAnyAsync<Exception>(() => PreviewAsync(provider, cs, table, runtime, ct));
        });
    }

    [Fact]
    public async Task PhysicalExtraUniqueKeyOrTrigger_RefusesApproval()
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            await ExecuteAsync(cs, $"CREATE UNIQUE INDEX [UX_{table}_status] ON dbo.[{table}](status)", ct);
            await Assert.ThrowsAnyAsync<Exception>(() => PreviewAsync(provider, cs, table, runtime, ct));
        });
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            await ExecuteAsync(cs, $"CREATE TRIGGER dbo.[tr_{table}] ON dbo.[{table}] AFTER UPDATE AS BEGIN SET NOCOUNT ON; END", ct);
            await Assert.ThrowsAnyAsync<Exception>(() => PreviewAsync(provider, cs, table, runtime, ct));
        });
    }

    private static string Sql(string table, string action = Action) =>
        $"MERGE INTO dbo.[{table}] AS t USING (VALUES(7,25,4)) AS s(id,quantity,status) " +
        $"ON t.id=s.id {action};";

    private static async Task<TypedDmlApprovalSession> PreviewAsync(
        MsSqlServerProvider provider, string cs, string table,
        TypedDmlRuntime runtime, CancellationToken ct)
    {
        var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
            provider, cs, Sql(table), SqlAgentToolType.MsSqlServer, ct);
        return await runtime.PreviewAsync(provider, cs, parsed, Policy, Allowed(table), Actor, ct);
    }

    private static IReadOnlySet<string> Allowed(string table) =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo." + table };

    private static async Task WithTableAsync(
        Func<MsSqlServerProvider, string, string, TypedDmlRuntime, CancellationToken, Task> test)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("MERGE MATCHED UPDATE real SQL Server 2022 required.");
            Assert.Skip("Requires TEST_SQLSERVER_CONNECTION.");
        }
        var ct = TestContext.Current.CancellationToken;
        var table = "g9_mergeup_" + Guid.NewGuid().ToString("N")[..10];
        // Reuse the existing isolated CI SQL Server 2022 instance without relying on
        // mock metadata or custom tenant tables.
        await ExecuteWithRetriesAsync(cs!, $"CREATE TABLE dbo.[{table}] (" +
            $"id INT NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY, " +
            "quantity INT NOT NULL, status INT NOT NULL)", ct);
        try
        {
            var schema = new NativeDmlSchemaSnapshotService();
            await test(new MsSqlServerProvider(), cs!, table,
                new TypedDmlRuntime(schemaSnapshotSource: schema, schemaFreshnessVerifier: schema), ct);
        }
        finally
        {
            await ExecuteAsync(cs!, $"DROP TABLE IF EXISTS dbo.[{table}]", CancellationToken.None);
        }
    }

    private static async Task ExecuteWithRetriesAsync(string cs, string sql, CancellationToken ct)
    {
        for (var attempt=0; ; attempt++)
        {
            try { await ExecuteAsync(cs, sql, ct); break; }
            catch (SqlException) when (attempt < 24 && !ct.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        }
    }

    private static async Task ExecuteAsync(string cs, string sql, CancellationToken ct)
    {
        await using var db = new SqlConnection(cs);
        await db.OpenAsync(ct);
        await using var command = new SqlCommand(sql, db) { CommandTimeout = 15 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ScalarAsync(string cs, string sql, CancellationToken ct)
    {
        await using var db = new SqlConnection(cs);
        await db.OpenAsync(ct);
        await using var command = new SqlCommand(sql, db);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }
}
