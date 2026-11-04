using Admin.Service.Models;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real PostgreSQL 16 (Testcontainers): the native Host may approve only
/// exactly typed, non-null TEXT / BIGINT proposed values. The physical catalog
/// and all old row values are rechecked under the same transaction lock.
/// </summary>
public sealed class TypedDmlPostgresTypedValueConflictUpdateApprovalTests(
    PostgresDdlLiveFixture fixture) : IClassFixture<PostgresDdlLiveFixture>
{
    private static readonly SecurityPolicyModel Policy = new() { DmlMaxAffectedRows = 1 };
    private static readonly DmlApprovalExecutionContext Context =
        new("mcp-user:m13", "db-management:1", SqlAgentToolType.Postgres, "ddl_live");

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    public async Task TextAndBigintMixed_OneToFourFields_BindAllBeforeAfter(
        bool existing, int fields)
    {
        var names = new[] { "name", "quantity", "city", "revision" }.Take(fields).ToArray();
        var types = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = "TEXT NOT NULL",
            ["quantity"] = "BIGINT NOT NULL",
            ["city"] = "TEXT NOT NULL",
            ["revision"] = "BIGINT NOT NULL"
        };
        var beforeValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = "'old-name'",
            ["quantity"] = "5",
            ["city"] = "'old-city'",
            ["revision"] = "9"
        };
        var proposedValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = "'new-name'",
            ["quantity"] = "42",
            ["city"] = "'new-city'",
            ["revision"] = "100"
        };
        await WithTableAsync("id BIGINT PRIMARY KEY, " +
            string.Join(", ", names.Select(name => name + " " + types[name])),
            async (provider, cs, schema, runtime, token) =>
            {
                if (existing)
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES (7," +
                        string.Join(",", names.Select(name => beforeValues[name])) + ")", token);
                var sql = MakeSql(schema, names,
                    names.Select(name => proposedValues[name]).ToArray(), reverseAssignments: true);
                var session = await PreviewAsync(provider, cs, schema, runtime, token, sql);
                Assert.Equal(DmlApprovalMode.InsertConflictDoUpdate, session.Plan.ApprovalMode);
                Assert.Equal(fields + 1, session.Plan.RowIdentityColumns.Length);
                Assert.Equal(1, session.Preview.AffectedRows);
                Assert.NotNull(session.Preview.Challenge.RowSetFingerprint);
                var preview = Assert.Single(session.Preview.Rows);
                Assert.Equal(existing ? "UPDATE" : "INSERT", preview["__action"]);
                Assert.Equal(7L, Convert.ToInt64(preview["id"]));
                foreach (var name in names)
                {
                    var isInteger = name is "quantity" or "revision";
                    var oldValue = existing
                        ? isInteger
                            ? Convert.ToInt64(name == "quantity" ? 5 : 9)
                            : (object)("old-" + name)
                        : null;
                    var newValue = isInteger
                        ? Convert.ToInt64(name == "quantity" ? 42 : 100)
                        : (object)("new-" + name);
                    if (existing && isInteger)
                        Assert.Equal(Convert.ToInt64(oldValue),
                            Convert.ToInt64(preview["__before_" + name]));
                    else
                        Assert.Equal(oldValue, preview["__before_" + name]);
                    if (isInteger)
                        Assert.Equal(Convert.ToInt64(newValue),
                            Convert.ToInt64(preview["__after_" + name]));
                    else
                        Assert.Equal(newValue, preview["__after_" + name]);
                }

                var committed = await runtime.CommitAsync(provider, cs, session,
                    Policy, Allowed(schema), Context, token);
                Assert.True(committed.Committed);
                Assert.Equal(1, committed.AffectedRows);
                foreach (var name in names)
                {
                    var actual = await ScalarAsync(cs,
                        $"SELECT {name}::text FROM {schema}.users WHERE id=7", token);
                    Assert.Equal(proposedValues[name].Trim('\''), actual);
                }

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    runtime.CommitAsync(provider, cs, session,
                        Policy, Allowed(schema), Context, token));
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldBigintOrNewConflictChanges_RejectWholeMutation(bool existing)
    {
        await WithTableAsync(
            "id BIGINT PRIMARY KEY, name TEXT NOT NULL, quantity BIGINT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                if (existing)
                    await ExecAsync(cs,
                        $"INSERT INTO {schema}.users VALUES (7,'old-name',5)", token);
                var session = await PreviewAsync(provider, cs, schema, runtime, token,
                    MakeSql(schema, ["name", "quantity"], ["'new-name'", "42"]));
                if (existing)
                    await ExecAsync(cs, $"UPDATE {schema}.users SET quantity=6 WHERE id=7", token);
                else
                    await ExecAsync(cs,
                        $"INSERT INTO {schema}.users VALUES (7,'racer-name',6)", token);

                var rejected = await runtime.CommitAsync(provider, cs, session,
                    Policy, Allowed(schema), Context, token);
                Assert.False(rejected.Committed);
                Assert.Equal(0, rejected.AffectedRows);
                Assert.Equal("6", await ScalarAsync(cs,
                    $"SELECT quantity::text FROM {schema}.users WHERE id=7", token));
                Assert.Equal(existing ? "old-name" : "racer-name", await ScalarAsync(cs,
                    $"SELECT name FROM {schema}.users WHERE id=7", token));
            });
    }

    [Fact]
    public async Task BigintUniqueIndexRemovedAfterPreview_FailsBeforeCommit()
    {
        await WithTableAsync("id BIGINT NOT NULL, name TEXT NOT NULL, quantity BIGINT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                await ExecAsync(cs, $"CREATE UNIQUE INDEX users_id_uq ON {schema}.users(id)", token);
                await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES (7,'old-name',5)", token);
                var session = await PreviewAsync(provider, cs, schema, runtime, token,
                    MakeSql(schema, ["name", "quantity"], ["'new-name'", "42"]));
                await ExecAsync(cs, $"DROP INDEX {schema}.users_id_uq", token);
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    runtime.CommitAsync(provider, cs, session,
                        Policy, Allowed(schema), Context, token));
                Assert.Equal("5", await ScalarAsync(cs,
                    $"SELECT quantity::text FROM {schema}.users WHERE id=7", token));
            });
    }

    [Theory]
    [InlineData("quantity INTEGER NOT NULL")]
    [InlineData("quantity SMALLINT NOT NULL")]
    [InlineData("quantity NUMERIC(20,0) NOT NULL")]
    [InlineData("quantity DOUBLE PRECISION NOT NULL")]
    [InlineData("quantity BIGINT")]
    [InlineData("quantity TEXT NOT NULL")]
    [InlineData("quantity BIGINT NOT NULL DEFAULT 5")]
    [InlineData("quantity BIGINT NOT NULL CHECK(quantity >= 0)")]
    public async Task UnprovedPhysicalTypeOrConstraint_FailsClosed(string quantityDefinition)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT NOT NULL, " + quantityDefinition,
            async (provider, cs, schema, runtime, token) =>
            {
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token,
                        MakeSql(schema, ["name", "quantity"], ["'new-name'", "42"])));
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    [Theory]
    [InlineData("'42'")]
    [InlineData("42.5")]
    [InlineData("NULL")]
    [InlineData("9223372036854775808")]
    [InlineData("1e10")]
    public async Task UnsafeProposedIntegerLiteral_NeverGetsApproval(string literal)
    {
        await WithTableAsync(
            "id BIGINT PRIMARY KEY, name TEXT NOT NULL, quantity BIGINT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token,
                        MakeSql(schema, ["name", "quantity"], ["'new-name'", literal])));
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    [Theory]
    [InlineData("42")]
    [InlineData("NULL")]
    public async Task UnsafeProposedTextValue_NeverGetsApproval(string value)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token,
                        MakeSql(schema, ["name"], [value])));
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    private static string MakeSql(string schema, IReadOnlyList<string> columns,
        IReadOnlyList<string> values, bool reverseAssignments = false)
    {
        var assignments = reverseAssignments ? columns.Reverse() : columns.AsEnumerable();
        return $"INSERT INTO {schema}.users (id,{string.Join(",", columns)}) VALUES (7," +
            string.Join(",", values) + ") ON CONFLICT (id) DO UPDATE SET " +
            string.Join(",", assignments.Select(name => name + "=EXCLUDED." + name));
    }

    private static async Task<TypedDmlApprovalSession> PreviewAsync(
        PostgresProvider provider, string cs, string schema, TypedDmlRuntime runtime,
        CancellationToken token, string sql)
    {
        var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
            provider, cs, sql, SqlAgentToolType.Postgres, token);
        return await runtime.PreviewAsync(provider, cs, parsed,
            Policy, Allowed(schema), Context, token);
    }

    private static IReadOnlySet<string> Allowed(string schema) =>
        new HashSet<string>(StringComparer.Ordinal) { schema + ".users" };

    private async Task WithTableAsync(string columns,
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
            await ExecAsync(cs, $"CREATE TABLE {schema}.users ({columns})", token);
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
