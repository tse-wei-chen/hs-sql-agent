using Admin.Service.Models;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real PG 16 proof: one-row native conflict UPDATE may RETURN only a bounded
/// set of direct, physically attested target columns. The returned result
/// must belong to the committed mutation, never a preview substitution.
/// </summary>
public sealed class TypedDmlPostgresConflictUpdateReturningTests(
    PostgresDdlLiveFixture fixture) : IClassFixture<PostgresDdlLiveFixture>
{
    private static readonly SecurityPolicyModel Policy = new() { DmlMaxAffectedRows = 1 };
    private static readonly DmlApprovalExecutionContext Context =
        new("mcp-user:g9-returning", "db-management:1", SqlAgentToolType.Postgres, "ddl_live");

    [Theory]
    [InlineData(false, "RETURNING id")]
    [InlineData(true, "RETURNING id")]
    [InlineData(false, "RETURNING amount, id")]
    [InlineData(true, "RETURNING amount, id")]
    [InlineData(false, "RETURNING id, name, amount")]
    [InlineData(true, "RETURNING id, name, amount")]
    public async Task DirectReturnedColumns_AreFromOneCommittedInsertOrUpdate(
        bool existing, string returning)
    {
        await WithTableAsync(
            "id BIGINT PRIMARY KEY, name TEXT, amount INTEGER NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                if (existing)
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES (7,'old',5)", token);
                var sql = Upsert(schema, returning);
                var session = await PreviewAsync(provider, cs, schema, runtime, token, sql);
                Assert.Equal(DmlApprovalMode.InsertConflictDoUpdate, session.Plan.ApprovalMode);
                Assert.True(session.Plan.MutationCommand.ReturnsRows);
                Assert.Equal(1, session.Preview.AffectedRows);
                var preview = Assert.Single(session.Preview.Rows);
                Assert.Equal(existing ? "UPDATE" : "INSERT", preview["__action"]);
                Assert.Equal(existing ? "old" : null, preview["__before_name"]);
                Assert.Equal("new", preview["__after_name"]);
                var result = await runtime.CommitAsync(provider, cs, session,
                    Policy, Allowed(schema), Context, token);
                Assert.True(result.Committed);
                Assert.Equal(1, result.AffectedRows);
                var row = Assert.Single(result.ReturnedRows);
                var projection = returning["RETURNING ".Length..].Split(',',
                    StringSplitOptions.TrimEntries);
                Assert.Equal(projection.Length, row.Count);
                foreach (var column in projection)
                {
                    Assert.True(row.ContainsKey(column), "Missing RETURNING column " + column);
                    var expected = column switch
                    {
                        "id" => (object)7L,
                        "name" => "new",
                        "amount" => 42,
                        _ => throw new InvalidOperationException("Unexpected test column")
                    };
                    if (column == "name")
                        Assert.Equal(expected, row[column]);
                    else
                        Assert.Equal(Convert.ToInt64(expected), Convert.ToInt64(row[column]));
                }
                Assert.Equal("new", await ScalarAsync(cs,
                    $"SELECT name FROM {schema}.users WHERE id=7", token));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    runtime.CommitAsync(provider, cs, session,
                        Policy, Allowed(schema), Context, token));
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturningNullableValue_ReportsActualNull(bool existing)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT",
            async (provider, cs, schema, runtime, token) =>
            {
                if (existing)
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES (7,'old')", token);
                var sql = $"INSERT INTO {schema}.users(id,name) VALUES (7,NULL) " +
                    "ON CONFLICT (id) DO UPDATE SET name=EXCLUDED.name RETURNING id,name";
                var session = await PreviewAsync(provider, cs, schema, runtime, token, sql);
                var preview = Assert.Single(session.Preview.Rows);
                Assert.Null(preview["__after_name"]);
                var result = await runtime.CommitAsync(provider, cs, session, Policy,
                    Allowed(schema), Context, token);
                Assert.True(result.Committed);
                var returned = Assert.Single(result.ReturnedRows);
                Assert.Equal(7L, Convert.ToInt64(returned["id"]));
                Assert.Null(returned["name"]);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleOldValueOrNewConflict_CancelsAndReturnsNoData(bool existing)
    {
        await WithTableAsync(
            "id BIGINT PRIMARY KEY, name TEXT, amount INTEGER NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                if (existing)
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES(7,'old',5)", token);
                var session = await PreviewAsync(provider, cs, schema, runtime, token,
                    Upsert(schema, "RETURNING id, amount"));
                if (existing)
                    await ExecAsync(cs, $"UPDATE {schema}.users SET amount=9 WHERE id=7", token);
                else
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES(7,'racer',9)", token);
                var rejected = await runtime.CommitAsync(provider, cs, session,
                    Policy, Allowed(schema), Context, token);
                Assert.False(rejected.Committed);
                Assert.Empty(rejected.ReturnedRows);
                Assert.Equal("9", await ScalarAsync(cs,
                    $"SELECT amount::text FROM {schema}.users WHERE id=7", token));
            });
    }

    [Theory]
    [InlineData("RETURNING *")]
    [InlineData("RETURNING id,id")]
    [InlineData("RETURNING upper(name)")]
    [InlineData("RETURNING id AS alias_id")]
    [InlineData("RETURNING id+1")]
    [InlineData("RETURNING tableoid")]
    [InlineData("RETURNING ctid")]
    [InlineData("RETURNING xmax")]
    [InlineData("RETURNING id, users.name")]
    public async Task UnprovedReturnProjection_RemainsFailClosed(string returning)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT, amount INTEGER NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token,
                        Upsert(schema, returning)));
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    [Fact]
    public async Task PhysicalUniqueIndexDrift_DeniesReturningWithoutWrite()
    {
        await WithTableAsync("id BIGINT NOT NULL, name TEXT, amount INTEGER NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                await ExecAsync(cs, $"CREATE UNIQUE INDEX users_id_uq ON {schema}.users(id)", token);
                await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES(7,'old',5)", token);
                var session = await PreviewAsync(provider, cs, schema, runtime, token,
                    Upsert(schema, "RETURNING id,name"));
                await ExecAsync(cs, $"DROP INDEX {schema}.users_id_uq", token);
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    runtime.CommitAsync(provider, cs, session,
                        Policy, Allowed(schema), Context, token));
                Assert.Equal("old", await ScalarAsync(cs,
                    $"SELECT name FROM {schema}.users WHERE id=7", token));
            });
    }

    private static string Upsert(string schema, string returning) =>
        $"INSERT INTO {schema}.users (id,name,amount) VALUES(7,'new',42) " +
        "ON CONFLICT (id) DO UPDATE SET amount=EXCLUDED.amount, name=EXCLUDED.name " +
        returning;

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
        var schema = "g9_ret_" + Guid.NewGuid().ToString("N");
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

    private static async Task<string> ScalarAsync(string cs, string sql, CancellationToken token)
    {
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(token);
        await using var cmd = new NpgsqlCommand(sql, db);
        return Convert.ToString(await cmd.ExecuteScalarAsync(token))!;
    }
}
