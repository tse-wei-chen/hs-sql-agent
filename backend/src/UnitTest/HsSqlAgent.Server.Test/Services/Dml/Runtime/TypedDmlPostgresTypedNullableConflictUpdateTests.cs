using Admin.Service.Models;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real PostgreSQL 16 Testcontainers proof: typed PostgreSQL VALUES literals and
/// SQL NULL are attested against actual pg_attribute types/nullability.
/// The entire old row and proposed values remain inside one approval.
/// </summary>
public sealed class TypedDmlPostgresTypedNullableConflictUpdateTests(
    PostgresDdlLiveFixture fixture) : IClassFixture<PostgresDdlLiveFixture>
{
    private static readonly SecurityPolicyModel Policy = new() { DmlMaxAffectedRows = 1 };
    private static readonly DmlApprovalExecutionContext Context =
        new("mcp-user:m13", "db-management:1", SqlAgentToolType.Postgres, "ddl_live");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedTextAndIntegerValues_ArePreviewedAndCommittedExactly(bool existing)
    {
        await WithTableAsync(
            "id BIGINT PRIMARY KEY, name TEXT, amount BIGINT, count INTEGER NOT NULL, rank SMALLINT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                if (existing)
                    await ExecAsync(cs,
                        $"INSERT INTO {schema}.users VALUES (7, NULL, NULL, -12, 3)", token);
                var sql = $"INSERT INTO {schema}.users (id,name,amount,count,rank) " +
                    "VALUES (7, NULL, 9223372036854775807, 2147483647, -32768) " +
                    "ON CONFLICT (id) DO UPDATE SET rank=EXCLUDED.rank, " +
                    "count=EXCLUDED.count, amount=EXCLUDED.amount, name=EXCLUDED.name";
                var session = await PreviewAsync(provider, cs, schema, runtime, token, sql);
                Assert.Equal(DmlApprovalMode.InsertConflictDoUpdate, session.Plan.ApprovalMode);
                Assert.Equal(1, session.Preview.AffectedRows);
                var row = Assert.Single(session.Preview.Rows);
                Assert.Equal(existing ? "UPDATE" : "INSERT", row["__action"]);
                Assert.Null(row["__before_name"]);
                Assert.Null(row["__after_name"]);
                Assert.Null(row["__before_amount"]);
                Assert.Equal(9223372036854775807L, Convert.ToInt64(row["__after_amount"]));
                Assert.Equal(existing ? -12 : null,
                    existing ? Convert.ToInt32(row["__before_count"]) : (int?)null);
                Assert.Equal(2147483647, Convert.ToInt32(row["__after_count"]));
                Assert.Equal(-32768, Convert.ToInt32(row["__after_rank"]));
                Assert.NotNull(session.Preview.Challenge.RowSetFingerprint);

                var commit = await runtime.CommitAsync(provider, cs, session,
                    Policy, Allowed(schema), Context, token);
                Assert.True(commit.Committed);
                Assert.Equal(1, commit.AffectedRows);
                Assert.Equal("9223372036854775807", await ScalarAsync(cs,
                    $"SELECT amount::text FROM {schema}.users WHERE id=7", token));
                Assert.Equal("2147483647", await ScalarAsync(cs,
                    $"SELECT count::text FROM {schema}.users WHERE id=7", token));
                Assert.Equal("-32768", await ScalarAsync(cs,
                    $"SELECT rank::text FROM {schema}.users WHERE id=7", token));
                Assert.Equal("1", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users WHERE name IS NULL", token));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    runtime.CommitAsync(provider, cs, session,
                        Policy, Allowed(schema), Context, token));
            });
    }

    [Theory]
    [InlineData("NULL", "old-name", null)]
    [InlineData("'new-name'", null, "new-name")]
    [InlineData("NULL", null, null)]
    public async Task NullableText_AllNullTransitionsAreSealedByApproval(
        string proposedSql, string? oldName, string? newName)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT, amount BIGINT",
            async (provider, cs, schema, runtime, token) =>
            {
                await ExecAsync(cs,
                    $"INSERT INTO {schema}.users VALUES (7," +
                    (oldName is null ? "NULL" : "'old-name'") + ",NULL)", token);
                var sql = $"INSERT INTO {schema}.users (id,name,amount) " +
                    $"VALUES (7,{proposedSql},NULL) ON CONFLICT (id) DO UPDATE " +
                    "SET name=EXCLUDED.name, amount=EXCLUDED.amount";
                var session = await PreviewAsync(provider, cs, schema, runtime, token, sql);
                var row = Assert.Single(session.Preview.Rows);
                Assert.Equal("UPDATE", row["__action"]);
                Assert.Equal(oldName, row["__before_name"]);
                Assert.Equal(newName, row["__after_name"]);
                Assert.Null(row["__before_amount"]);
                Assert.Null(row["__after_amount"]);
                var commit = await runtime.CommitAsync(provider, cs, session,
                    Policy, Allowed(schema), Context, token);
                Assert.True(commit.Committed);
                Assert.Equal(newName, await ScalarNullableAsync(cs,
                    $"SELECT name FROM {schema}.users WHERE id=7", token));
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullableOldValueOrConflictAppearsAfterPreview_RejectsWholeChange(
        bool existing)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT, amount BIGINT",
            async (provider, cs, schema, runtime, token) =>
            {
                if (existing)
                    await ExecAsync(cs,
                        $"INSERT INTO {schema}.users VALUES (7,'old-name',NULL)", token);
                var sql = $"INSERT INTO {schema}.users (id,name,amount) " +
                    "VALUES (7,'new-name',42) ON CONFLICT (id) DO UPDATE " +
                    "SET name=EXCLUDED.name, amount=EXCLUDED.amount";
                var preview = await PreviewAsync(provider, cs, schema, runtime, token, sql);
                if (existing)
                    await ExecAsync(cs,
                        $"UPDATE {schema}.users SET amount=17 WHERE id=7", token);
                else
                    await ExecAsync(cs,
                        $"INSERT INTO {schema}.users VALUES (7,'racer',17)", token);
                var commit = await runtime.CommitAsync(provider, cs, preview,
                    Policy, Allowed(schema), Context, token);
                Assert.False(commit.Committed);
                Assert.Equal(0, commit.AffectedRows);
                Assert.Equal("17", await ScalarAsync(cs,
                    $"SELECT amount::text FROM {schema}.users WHERE id=7", token));
                Assert.Equal(existing ? "old-name" : "racer", await ScalarAsync(cs,
                    $"SELECT name FROM {schema}.users WHERE id=7", token));
            });
    }

    [Theory]
    [InlineData("SMALLINT NOT NULL", "32768")]
    [InlineData("SMALLINT NOT NULL", "-32769")]
    [InlineData("INTEGER NOT NULL", "2147483648")]
    [InlineData("INTEGER NOT NULL", "-2147483649")]
    [InlineData("BIGINT NOT NULL", "9223372036854775808")]
    [InlineData("BIGINT NOT NULL", "1.5")]
    [InlineData("INTEGER NOT NULL", "'42'")]
    [InlineData("TEXT NOT NULL", "42")]
    [InlineData("TEXT NOT NULL", "NULL")]
    [InlineData("BIGINT NOT NULL", "NULL")]
    [InlineData("BOOLEAN", "TRUE")]
    [InlineData("NUMERIC(10,2)", "1.50")]
    [InlineData("DATE", "'2026-11-04'")]
    public async Task UnprovedTypeOrRangeFailsBeforePhysicalWrite(
        string dbType, string sqlValue)
    {
        await WithTableAsync($"id BIGINT PRIMARY KEY, name TEXT, amount {dbType}",
            async (provider, cs, schema, runtime, token) =>
            {
                var sql = $"INSERT INTO {schema}.users (id,name,amount) " +
                    $"VALUES (7,'new-name',{sqlValue}) ON CONFLICT (id) DO UPDATE " +
                    "SET name=EXCLUDED.name, amount=EXCLUDED.amount";
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token, sql));
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    [Fact]
    public async Task NullIsLegalOnlyForPhysicallyNullableColumns()
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT, amount INTEGER NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                var sql = $"INSERT INTO {schema}.users (id,name,amount) VALUES (7,NULL,NULL) " +
                    "ON CONFLICT (id) DO UPDATE SET name=EXCLUDED.name, amount=EXCLUDED.amount";
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token, sql));
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    private static IReadOnlySet<string> Allowed(string schema) =>
        new HashSet<string>(StringComparer.Ordinal) { schema + ".users" };

    private static async Task<TypedDmlApprovalSession> PreviewAsync(
        PostgresProvider provider, string cs, string schema, TypedDmlRuntime runtime,
        CancellationToken token, string sql)
    {
        var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
            provider, cs, sql, SqlAgentToolType.Postgres, token);
        return await runtime.PreviewAsync(provider, cs, parsed,
            Policy, Allowed(schema), Context, token);
    }

    private async Task WithTableAsync(string definition,
        Func<PostgresProvider, string, string, TypedDmlRuntime, CancellationToken, Task> action)
    {
        var cs = fixture.ConnectionString;
        var token = TestContext.Current.CancellationToken;
        var schema = "m13_pg_" + Guid.NewGuid().ToString("N");
        var provider = new PostgresProvider();
        var snapshot = new NativeDmlSchemaSnapshotService();
        var runtime = new TypedDmlRuntime(
            schemaSnapshotSource: snapshot, schemaFreshnessVerifier: snapshot);
        await ExecAsync(cs, $"CREATE SCHEMA {schema}", token);
        try
        {
            await ExecAsync(cs, $"CREATE TABLE {schema}.users ({definition})", token);
            await action(provider, cs, schema, runtime, token);
        }
        finally
        {
            await ExecAsync(cs, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
        }
    }

    private static async Task ExecAsync(string cs, string sql, CancellationToken token)
    {
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(token);
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static async Task<string> ScalarAsync(string cs, string sql, CancellationToken token) =>
        Convert.ToString(await ScalarNullableAsync(cs, sql, token))!;

    private static async Task<string?> ScalarNullableAsync(
        string cs, string sql, CancellationToken token)
    {
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(token);
        await using var cmd = new NpgsqlCommand(sql, db);
        var result = await cmd.ExecuteScalarAsync(token);
        return result is null or DBNull ? null : Convert.ToString(result);
    }
}
