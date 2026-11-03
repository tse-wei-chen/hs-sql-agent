using HsSqlAgent.Provider.SqlServer;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.SqlParsing;
using Microsoft.Data.SqlClient;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Native SQL Server 2022 MERGE proof: an actual enforced key, not a caller-authored
/// assurance, and a serializable target key-range lock on an unmatched source row.
/// SqlCore produces SQL only; this test intentionally does NOT grant Host DML approval.
/// </summary>
public sealed class SqlCoreSqlServerMergeHoldLockLiveTests
{
    [Theory]
    [InlineData("update")]
    [InlineData("insert")]
    [InlineData("delete")]
    public async Task NativeSingleSourceMerge_WithLivePrimaryKey_RendersHoldLockAndMutatesExactlyOneRow(
        string action)
    {
        await WithDatabaseAsync(async (cs, connection, table, token) =>
        {
            await ExecuteAsync(connection,
                $"INSERT INTO dbo.[{table}] (id, quantity) VALUES (1, 10)", token);
            var key = await new DmlUniqueKeyResolver(new MsSqlServerProvider())
                .ResolveAsync(cs, "dbo", table, ["id"], token);
            Assert.True(key.MatchedKey.IsPrimaryKey);
            var sql = action switch
            {
                "update" => $"MERGE INTO dbo.[{table}] AS t " +
                    "USING (VALUES (1, 20)) AS s (id, quantity) ON t.id = s.id " +
                    "WHEN MATCHED THEN UPDATE SET quantity = s.quantity;",
                "insert" => $"MERGE INTO dbo.[{table}] AS t " +
                    "USING (VALUES (2, 20)) AS s (id, quantity) ON t.id = s.id " +
                    "WHEN NOT MATCHED THEN INSERT (id, quantity) VALUES (s.id, s.quantity);",
                _ => $"MERGE INTO dbo.[{table}] AS t " +
                    "USING (VALUES (1, 20)) AS s (id, quantity) ON t.id = s.id " +
                    "WHEN MATCHED THEN DELETE;"
            };
            var compiled = Compile(sql, key);
            Assert.Contains(" WITH (HOLDLOCK) AS ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(SqlStatementKind.Merge, compiled.Kind);
            Assert.Equal(2, compiled.Parameters.Length);
            await using (var tx = (SqlTransaction)await connection.BeginTransactionAsync(token))
            {
                await ExecuteCompiledAsync(connection, tx, compiled, token);
                await tx.CommitAsync(token);
            }

            Assert.Equal(action == "delete" ? 0 : 1,
                await CountAsync(connection, table, 1, token));
            Assert.Equal(action == "insert" ? 1 : 0,
                await CountAsync(connection, table, 2, token));
            if (action == "update")
                Assert.Equal(20, await ScalarAsync(connection,
                    $"SELECT quantity FROM dbo.[{table}] WHERE id = 1", token));
            if (action == "insert")
                Assert.Equal(20, await ScalarAsync(connection,
                    $"SELECT quantity FROM dbo.[{table}] WHERE id = 2", token));
        });
    }

    [Fact]
    public async Task NativeCompositeUniqueKey_MergeTouchesOnlyTheMatchedPhysicalTuple()
    {
        await WithDatabaseAsync(async (cs, connection, table, token) =>
        {
            await ExecuteAsync(connection, $"ALTER TABLE dbo.[{table}] ADD tenant_id INT NOT NULL DEFAULT(1)", token);
            await ExecuteAsync(connection, $"ALTER TABLE dbo.[{table}] DROP CONSTRAINT [" +
                await PrimaryKeyNameAsync(connection, table, token) + "]", token);
            await ExecuteAsync(connection,
                $"CREATE UNIQUE INDEX [ux_{table}] ON dbo.[{table}] (tenant_id, id)", token);
            await ExecuteAsync(connection, $"INSERT INTO dbo.[{table}] (tenant_id,id,quantity) VALUES (1,1,10),(2,1,30)", token);
            var key = await new DmlUniqueKeyResolver(new MsSqlServerProvider())
                .ResolveAsync(cs, "dbo", table, ["id", "tenant_id"], token);
            Assert.False(key.MatchedKey.IsPrimaryKey);
            Assert.Equal(2, key.MatchedKey.Columns.Count);
            var compiled = Compile(
                $"MERGE INTO dbo.[{table}] AS t " +
                "USING (VALUES (1, 1, 77)) AS s (tenant_id, id, quantity) " +
                "ON t.tenant_id = s.tenant_id AND t.id = s.id " +
                "WHEN MATCHED THEN UPDATE SET quantity = s.quantity;",
                key);
            Assert.Contains(" WITH (HOLDLOCK) AS ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            await using (var tx = (SqlTransaction)await connection.BeginTransactionAsync(token))
            {
                await ExecuteCompiledAsync(connection, tx, compiled, token);
                await tx.CommitAsync(token);
            }
            Assert.Equal(77, await ScalarAsync(connection,
                $"SELECT quantity FROM dbo.[{table}] WHERE tenant_id=1 AND id=1", token));
            Assert.Equal(30, await ScalarAsync(connection,
                $"SELECT quantity FROM dbo.[{table}] WHERE tenant_id=2 AND id=1", token));
        });
    }

    [Fact]
    public async Task NativeSingleSourceMerge_HoldLockKeepsAnUnmatchedKeyRangeUntilTransactionEnds()
    {
        await WithDatabaseAsync(async (cs, connection, table, token) =>
        {
            var key = await new DmlUniqueKeyResolver(new MsSqlServerProvider())
                .ResolveAsync(cs, "dbo", table, ["id"], token);
            var compiled = Compile(
                $"MERGE INTO dbo.[{table}] AS t " +
                "USING (VALUES (42, 20)) AS s (id, quantity) ON t.id = s.id " +
                "WHEN MATCHED THEN UPDATE SET quantity = s.quantity;", key);
            Assert.Contains(" WITH (HOLDLOCK) AS ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            await using (var holding = (SqlTransaction)await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, token))
            {
                await ExecuteCompiledAsync(connection, holding, compiled, token);
                await using var competing = new SqlConnection(cs);
                await competing.OpenAsync(token);
                await using (var blocked = new SqlCommand(
                    $"SET LOCK_TIMEOUT 900; INSERT INTO dbo.[{table}] (id,quantity) VALUES (42,99)",
                    competing))
                {
                    blocked.CommandTimeout = 8;
                    var error = await Assert.ThrowsAsync<SqlException>(
                        () => blocked.ExecuteNonQueryAsync(token));
                    Assert.Equal(1222, error.Number);
                }
                await holding.RollbackAsync(token);
            }
            await ExecuteAsync(connection,
                $"INSERT INTO dbo.[{table}] (id,quantity) VALUES (42,99)", token);
            Assert.Equal(1, await CountAsync(connection, table, 42, token));
        });
    }

    [Fact]
    public async Task NativeMerge_UnenforcedTargetKeyCannotMintMetadataBackedAuthority()
    {
        await WithDatabaseAsync(async (cs, connection, table, token) =>
        {
            await ExecuteAsync(connection, $"ALTER TABLE dbo.[{table}] DROP CONSTRAINT [" +
                await PrimaryKeyNameAsync(connection, table, token) + "]", token);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new DmlUniqueKeyResolver(new MsSqlServerProvider())
                    .ResolveAsync(cs, "dbo", table, ["id"], token));
            Assert.Contains("simple enforced unique key", error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Throws<SqlCompilationException>(() =>
                CoreDmlCompiler.CreateDefault().Compile(
                    CoreSqlTextParser.ParseDml(
                        $"MERGE INTO dbo.[{table}] AS t USING (VALUES (1,20)) AS s (id,quantity) " +
                        "ON t.id=s.id WHEN MATCHED THEN UPDATE SET quantity=s.quantity;",
                        SqlAgentToolType.MsSqlServer),
                    SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext("m10-live-merge-negative-v1")));
            Assert.Equal(0, await ScalarAsync(connection,
                $"SELECT COUNT(*) FROM dbo.[{table}]", token));
        });
    }

    private static CompiledSqlCommand Compile(string sql, DmlUniqueKeyResolution key) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer),
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("m10-sqlserver-live-merge-holdlock-v1"),
            conflictTargetAssurance: key.ToConflictTargetAssurance());

    private static async Task ExecuteCompiledAsync(SqlConnection connection,
        SqlTransaction tx, CompiledSqlCommand compiled, CancellationToken token)
    {
        await using var command = new SqlCommand(compiled.Sql, connection, tx);
        command.CommandTimeout = 10;
        foreach (var parameter in compiled.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int> CountAsync(SqlConnection connection, string table,
        int key, CancellationToken token) =>
        await ScalarAsync(connection,
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE id={key}", token);

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql,
        CancellationToken token)
    {
        await using var cmd = new SqlCommand(sql, connection);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    private static async Task<string> PrimaryKeyNameAsync(SqlConnection connection,
        string table, CancellationToken token)
    {
        await using var cmd = new SqlCommand(
            "SELECT kc.name FROM sys.key_constraints AS kc " +
            "JOIN sys.tables AS t ON t.object_id=kc.parent_object_id " +
            "JOIN sys.schemas AS s ON t.schema_id=s.schema_id " +
            "WHERE kc.type='PK' AND s.name='dbo' AND t.name=@name", connection);
        cmd.Parameters.AddWithValue("@name", table);
        return (string)(await cmd.ExecuteScalarAsync(token)
            ?? throw new InvalidOperationException("Missing real primary key."));
    }

    private static async Task ExecuteAsync(SqlConnection connection,
        string sql, CancellationToken token)
    {
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 10 };
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static async Task WithDatabaseAsync(
        Func<string, SqlConnection, string, CancellationToken, Task> test)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "M10 native MERGE lock proof requires real TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("Requires actual SQL Server 2022.");
        }
        var token = TestContext.Current.CancellationToken;
        var table = "m10_merge_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = new SqlConnection(cs);
        for (int retries = 0; ; retries++)
        {
            try { await connection.OpenAsync(token); break; }
            catch (SqlException) when (retries < 24 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }
        await ExecuteAsync(connection, $"CREATE TABLE dbo.[{table}] (" +
            "id INT NOT NULL PRIMARY KEY, quantity INT NOT NULL)", token);
        try { await test(cs!, connection, table, token); }
        finally { await ExecuteAsync(connection,
            $"DROP TABLE IF EXISTS dbo.[{table}]", token); }
    }
}
