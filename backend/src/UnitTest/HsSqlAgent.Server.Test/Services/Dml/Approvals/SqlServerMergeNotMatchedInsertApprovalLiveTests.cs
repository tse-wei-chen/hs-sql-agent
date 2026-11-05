using Admin.Service.Models;
using HsSqlAgent.Provider.SqlServer;
using HsSqlAgent.Server.Services;
using Microsoft.Data.SqlClient;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>Real SQL Server 2022 G9 Host INSERT-only MERGE: absence and source payload must both be approved.</summary>
public sealed class SqlServerMergeNotMatchedInsertApprovalLiveTests
{
    private static readonly SecurityPolicyModel Policy = new() { DmlMaxAffectedRows = 1 };
    private static readonly DmlApprovalExecutionContext Actor =
        new("mcp-user:merge-insert", "db-management:g9", SqlAgentToolType.MsSqlServer, "sqlserver-2022-real");

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task MissingOrExistingPhysicalKey_ApprovesExactInsertOrSkip_OneUse(bool exists, int expectedAffected)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            if (exists)
                await ExecuteAsync(cs, $"INSERT INTO dbo.[{table}](id,quantity,status) VALUES(7,10,3)", ct);
            var session = await PreviewAsync(provider, cs, Sql(table), runtime, ct);
            Assert.Equal("SqlServerMergeNotMatchedInsert", session.Plan.ApprovalMode.ToString());
            Assert.Equal(expectedAffected, session.Preview.AffectedRows);
            Assert.Contains("WITH (HOLDLOCK)", session.Plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(expectedAffected, session.Preview.Rows.Length);
            if (!exists)
            {
                var approved = Assert.Single(session.Preview.Rows);
                Assert.Equal(7, Convert.ToInt32(approved["id"]));
                Assert.Equal(25, Convert.ToInt32(approved["quantity"]));
                Assert.Equal(4, Convert.ToInt32(approved["status"]));
            }
            var result = await runtime.CommitAsync(provider, cs, session, Policy, Allowed(table), Actor, ct);
            Assert.True(result.Committed);
            Assert.Equal(expectedAffected, result.AffectedRows);
            Assert.Empty(result.ReturnedRows);
            Assert.Equal(exists ? 10 : 25,
                await ScalarAsync(cs, $"SELECT quantity FROM dbo.[{table}] WHERE id=7", ct));
            Assert.Equal(exists ? 3 : 4,
                await ScalarAsync(cs, $"SELECT status FROM dbo.[{table}] WHERE id=7", ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitAsync(provider, cs, session, Policy, Allowed(table), Actor, ct));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TargetKeyChangedBetweenPreviewAndCommit_CancelsAllEffects(bool existed)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            if (existed)
                await ExecuteAsync(cs, $"INSERT INTO dbo.[{table}](id,quantity,status) VALUES(7,10,3)", ct);
            var session = await PreviewAsync(provider, cs, Sql(table), runtime, ct);
            await ExecuteAsync(cs, existed
                ? $"DELETE FROM dbo.[{table}] WHERE id=7"
                : $"INSERT INTO dbo.[{table}](id,quantity,status) VALUES(7,19,8)", ct);
            var result = await runtime.CommitAsync(provider, cs, session, Policy, Allowed(table), Actor, ct);
            Assert.False(result.Committed);
            Assert.Empty(result.ReturnedRows);
            Assert.Equal(existed ? 0 : 19,
                await ScalarAsync(cs, $"SELECT COALESCE(MAX(quantity),0) FROM dbo.[{table}]", ct));
        });
    }

    [Fact]
    public async Task PrimaryKeyRevokedAfterApproval_DeniesInsertEvenWhenTargetEmpty()
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            var session = await PreviewAsync(provider, cs, Sql(table), runtime, ct);
            await ExecuteAsync(cs, $"ALTER TABLE dbo.[{table}] DROP CONSTRAINT [PK_{table}]", ct);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                runtime.CommitAsync(provider, cs, session, Policy, Allowed(table), Actor, ct));
            Assert.Equal(0, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{table}]", ct));
        });
    }

    [Fact]
    public async Task PermutedSourceAndInsertColumnOrder_PreservesExactValues()
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            var sql = $"MERGE INTO dbo.[{table}] AS t USING (VALUES(7,4,25)) " +
                "AS s(id,status,quantity) ON t.id=s.id " +
                "WHEN NOT MATCHED THEN INSERT(status,id,quantity) VALUES(s.status,s.id,s.quantity);";
            var session = await PreviewAsync(provider, cs, sql, runtime, ct);
            var approved = Assert.Single(session.Preview.Rows);
            Assert.Equal(25, Convert.ToInt32(approved["quantity"]));
            Assert.Equal(4, Convert.ToInt32(approved["status"]));
            var result = await runtime.CommitAsync(provider, cs, session, Policy, Allowed(table), Actor, ct);
            Assert.True(result.Committed);
            Assert.Equal(25, await ScalarAsync(cs, $"SELECT quantity FROM dbo.[{table}] WHERE id=7", ct));
            Assert.Equal(4, await ScalarAsync(cs, $"SELECT status FROM dbo.[{table}] WHERE id=7", ct));
        });
    }

    [Theory]
    [InlineData("WHEN NOT MATCHED THEN INSERT(id,quantity) VALUES(s.id,s.quantity)")]
    [InlineData("WHEN NOT MATCHED THEN INSERT(id,quantity,status) VALUES(s.id,25,s.status)")]
    [InlineData("WHEN NOT MATCHED THEN INSERT(id,quantity,status) VALUES(s.id,s.quantity+1,s.status)")]
    [InlineData("WHEN NOT MATCHED THEN INSERT(id,quantity,status) VALUES(s.id,s.status,s.quantity)")]
    [InlineData("WHEN NOT MATCHED THEN INSERT(id,quantity,status) VALUES(s.id,s.quantity,s.status) WHEN MATCHED THEN DELETE")]
    [InlineData("WHEN MATCHED THEN UPDATE SET quantity=s.quantity WHEN NOT MATCHED THEN INSERT(id,quantity,status) VALUES(s.id,s.quantity,s.status)")]
    public async Task UnprovedInsertColumnsExpressionsOrMixedActions_AreDenied(string action)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            var sql = Sql(table, action);
            await Assert.ThrowsAnyAsync<Exception>(() => PreviewAsync(provider, cs, sql, runtime, ct));
            Assert.Equal(0, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{table}]", ct));
        });
    }

    [Theory]
    [InlineData("7e0", "25", "4")]
    [InlineData("7.0", "25", "4")]
    [InlineData("7", "25.0", "4")]
    [InlineData("7", "'25'", "4")]
    [InlineData("7", "25+0", "4")]
    [InlineData("7", "NULL", "4")]
    public async Task SourceMustContainExactIntTokens(string key, string quantity, string status)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            var sql = $"MERGE INTO dbo.[{table}] AS t USING (VALUES({key},{quantity},{status})) " +
                "AS s(id,quantity,status) ON t.id=s.id " +
                "WHEN NOT MATCHED THEN INSERT(id,quantity,status) VALUES(s.id,s.quantity,s.status);";
            await Assert.ThrowsAnyAsync<Exception>(() => PreviewAsync(provider, cs, sql, runtime, ct));
            Assert.Equal(0, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{table}]", ct));
        });
    }

    [Theory]
    [InlineData("ON t.id=s.id AND t.status=4")]
    [InlineData("ON t.id=s.id OR t.quantity=25")]
    [InlineData("ON t.id=s.id+0")]
    public async Task AmbiguousMatchPredicate_IsDenied(string predicate)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            var sql = $"MERGE INTO dbo.[{table}] AS t USING (VALUES(7,25,4)) " +
                $"AS s(id,quantity,status) {predicate} " +
                "WHEN NOT MATCHED THEN INSERT(id,quantity,status) VALUES(s.id,s.quantity,s.status);";
            await Assert.ThrowsAnyAsync<Exception>(() => PreviewAsync(provider, cs, sql, runtime, ct));
        });
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX [UX_Q] ON dbo.[{table}](quantity)")]
    [InlineData("CREATE TRIGGER dbo.[tr_{table}] ON dbo.[{table}] AFTER INSERT AS BEGIN SET NOCOUNT ON; END")]
    public async Task ExtraUniqueKeyOrTrigger_CannotHideSideEffects(string ddlTemplate)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            await ExecuteAsync(cs, ddlTemplate.Replace("{table}", table, StringComparison.Ordinal), ct);
            await Assert.ThrowsAnyAsync<Exception>(() => PreviewAsync(provider, cs, Sql(table), runtime, ct));
        });
    }

    [Fact]
    public async Task IncompletePhysicalColumnSet_CannotRelyOnDefaultsOrImplicitValues()
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            var sql = $"MERGE INTO dbo.[{table}] AS t USING (VALUES(7,25)) " +
                "AS s(id,quantity) ON t.id=s.id " +
                "WHEN NOT MATCHED THEN INSERT(id,quantity) VALUES(s.id,s.quantity);";
            await Assert.ThrowsAnyAsync<Exception>(() => PreviewAsync(provider, cs, sql, runtime, ct));
        });
    }

    private static string Sql(string table, string action =
        "WHEN NOT MATCHED THEN INSERT(id,quantity,status) VALUES(s.id,s.quantity,s.status)") =>
        $"MERGE INTO dbo.[{table}] AS t USING (VALUES(7,25,4)) AS s(id,quantity,status) ON t.id=s.id {action};";

    private static async Task<TypedDmlApprovalSession> PreviewAsync(
        MsSqlServerProvider provider, string cs, string sql, TypedDmlRuntime runtime, CancellationToken ct)
    {
        var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
            provider, cs, sql, SqlAgentToolType.MsSqlServer, ct);
        return await runtime.PreviewAsync(provider, cs, parsed, Policy, AllowedFromSql(sql), Actor, ct);
    }

    private static IReadOnlySet<string> AllowedFromSql(string sql)
    {
        var marker = "MERGE INTO dbo.[";
        var start = sql.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Test SQL physical table name missing.");
        var end = sql.IndexOf(']', start + marker.Length);
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "dbo." + sql[(start + marker.Length)..end] };
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
                throw new InvalidOperationException("SQL Server MERGE INSERT Host tests require live SQL Server 2022.");
            Assert.Skip("Requires TEST_SQLSERVER_CONNECTION.");
        }
        var ct = TestContext.Current.CancellationToken;
        var table = "g9_mergeins_" + Guid.NewGuid().ToString("N")[..10];
        await ExecuteWithRetriesAsync(cs!, $"CREATE TABLE dbo.[{table}] (" +
            $"id INT NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY, " +
            "quantity INT NOT NULL, status INT NOT NULL)", ct);
        try
        {
            var snapshot = new NativeDmlSchemaSnapshotService();
            await test(new MsSqlServerProvider(), cs!, table,
                new TypedDmlRuntime(schemaSnapshotSource: snapshot,
                    schemaFreshnessVerifier: snapshot), ct);
        }
        finally
        {
            await ExecuteAsync(cs!, $"DROP TABLE IF EXISTS dbo.[{table}]", CancellationToken.None);
        }
    }

    private static async Task ExecuteWithRetriesAsync(string cs, string sql, CancellationToken ct)
    {
        for (var retry = 0; ; retry++)
        {
            try { await ExecuteAsync(cs, sql, ct); return; }
            catch (SqlException) when (retry < 24 && !ct.IsCancellationRequested)
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
