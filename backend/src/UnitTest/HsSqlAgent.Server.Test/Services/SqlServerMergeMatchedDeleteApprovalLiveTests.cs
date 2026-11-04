using Admin.Service.Models;
using HsSqlAgent.Provider.SqlServer;
using HsSqlAgent.Server.Services;
using Microsoft.Data.SqlClient;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real SQL Server 2022 TDD proof for single-source-key, MATCHED DELETE only.
/// The parser/SqlCore MERGE HOLDLOCK capability alone must never mint Host approval.
/// </summary>
public sealed class SqlServerMergeMatchedDeleteApprovalLiveTests
{
    private static readonly SecurityPolicyModel Policy = new() { DmlMaxAffectedRows = 1 };
    private static readonly DmlApprovalExecutionContext Actor =
        new("mcp-user:merge-red", "db-management:1", SqlAgentToolType.MsSqlServer, "live-sqlserver-2022");

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task OnePhysicalPrimaryKey_MatchedOrMissing_OneUseApproval(bool exists, int affected)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            if (exists)
                await ExecuteAsync(cs, $"INSERT INTO dbo.[{table}](id,quantity) VALUES(7,10)", ct);
            var session = await PreviewAsync(provider, cs, table, runtime, ct);
            Assert.Equal(affected, session.Preview.AffectedRows);
            Assert.Contains("WITH (HOLDLOCK)", session.Plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(affected, session.Preview.Rows.Length);
            var committed = await runtime.CommitAsync(provider, cs, session,
                Policy, Allowed(table), Actor, ct);
            Assert.True(committed.Committed);
            Assert.Equal(affected, committed.AffectedRows);
            Assert.Empty(committed.ReturnedRows);
            Assert.Equal(0, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{table}] WHERE id=7", ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runtime.CommitAsync(provider, cs, session, Policy, Allowed(table), Actor, ct));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConflictChangedAfterPreview_CancelsWithoutDeleting(bool existsAtPreview)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            if (existsAtPreview)
                await ExecuteAsync(cs, $"INSERT INTO dbo.[{table}](id,quantity) VALUES(7,10)", ct);
            var session = await PreviewAsync(provider, cs, table, runtime, ct);
            if (existsAtPreview)
                await ExecuteAsync(cs, $"DELETE FROM dbo.[{table}] WHERE id=7", ct);
            else
                await ExecuteAsync(cs, $"INSERT INTO dbo.[{table}](id,quantity) VALUES(7,99)", ct);

            var rejected = await runtime.CommitAsync(provider, cs, session,
                Policy, Allowed(table), Actor, ct);
            Assert.False(rejected.Committed);
            Assert.Empty(rejected.ReturnedRows);
            Assert.Equal(existsAtPreview ? 0 : 1,
                await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{table}] WHERE id=7", ct));
        });
    }

    [Fact]
    public async Task PrimaryKeyRevokedAfterPreview_NeverCommits()
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            await ExecuteAsync(cs, $"INSERT INTO dbo.[{table}](id,quantity) VALUES(7,10)", ct);
            var session = await PreviewAsync(provider, cs, table, runtime, ct);
            await ExecuteAsync(cs, $"ALTER TABLE dbo.[{table}] DROP CONSTRAINT [PK_{table}]", ct);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                runtime.CommitAsync(provider, cs, session, Policy, Allowed(table), Actor, ct));
            Assert.Equal(1, await ScalarAsync(cs,
                $"SELECT COUNT(*) FROM dbo.[{table}] WHERE id=7", ct));
        });
    }

    [Fact]
    public async Task NoPhysicalPrimaryKey_CannotMintApproval()
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            await ExecuteAsync(cs, $"ALTER TABLE dbo.[{table}] DROP CONSTRAINT [PK_{table}]", ct);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                PreviewAsync(provider, cs, table, runtime, ct));
            Assert.Equal(0, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{table}]", ct));
        });
    }

    [Theory]
    [InlineData("WHEN MATCHED THEN UPDATE SET quantity=s.quantity")]
    [InlineData("WHEN NOT MATCHED THEN INSERT(id) VALUES(s.id)")]
    [InlineData("WHEN MATCHED THEN DELETE WHEN NOT MATCHED THEN INSERT(id) VALUES(s.id)")]
    public async Task UnprovedMergeActions_FailBeforeAnyWrite(string actions)
    {
        await WithTableAsync(async (provider, cs, table, runtime, ct) =>
        {
            var sql = $"MERGE INTO dbo.[{table}] AS t USING (VALUES(7)) AS s(id) " +
                $"ON t.id=s.id {actions};";
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
                    provider, cs, sql, SqlAgentToolType.MsSqlServer, ct);
                await runtime.PreviewAsync(provider, cs, parsed, Policy, Allowed(table), Actor, ct);
            });
            Assert.Equal(0, await ScalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{table}]", ct));
        });
    }

    private static async Task<TypedDmlApprovalSession> PreviewAsync(
        MsSqlServerProvider provider, string cs, string table,
        TypedDmlRuntime runtime, CancellationToken ct)
    {
        var sql = $"MERGE INTO dbo.[{table}] AS t USING (VALUES(7)) AS s(id) " +
            "ON t.id=s.id WHEN MATCHED THEN DELETE;";
        var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
            provider, cs, sql, SqlAgentToolType.MsSqlServer, ct);
        return await runtime.PreviewAsync(provider, cs, parsed,
            Policy, Allowed(table), Actor, ct);
    }

    private static IReadOnlySet<string> Allowed(string table) =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo." + table };

    private static async Task WithTableAsync(
        Func<MsSqlServerProvider, string, string, TypedDmlRuntime, CancellationToken, Task> test)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQL Server MERGE Host proof requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("Live SQL Server 2022 is required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var table = "g9_mergedelete_" + Guid.NewGuid().ToString("N")[..10];
        await using (var db = new SqlConnection(cs))
        {
            for (var retry = 0; ; retry++)
            {
                try { await db.OpenAsync(ct); break; }
                catch (SqlException) when (retry < 24 && !ct.IsCancellationRequested)
                { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            }
        }
        await ExecuteAsync(cs!, $"CREATE TABLE dbo.[{table}] (" +
            $"id INT NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY, quantity INT NOT NULL)", ct);
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
