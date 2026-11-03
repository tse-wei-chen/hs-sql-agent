using Admin.Service.Models;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real PostgreSQL 16 approval tests (Testcontainers, no synthetic metadata).
/// Every updated column must be visible before/after and covered by the same
/// one-time challenge and commit-time old-row fingerprint.
/// </summary>
public sealed class TypedDmlPostgresMultiColumnConflictUpdateApprovalTests(
    PostgresDdlLiveFixture fixture) : IClassFixture<PostgresDdlLiveFixture>
{
    private static readonly SecurityPolicyModel Policy = new() { DmlMaxAffectedRows = 1 };
    private static readonly DmlApprovalExecutionContext Context =
        new("mcp-user:m12", "db-management:1", SqlAgentToolType.Postgres, "ddl_live");
    private static readonly string[] ValueColumns = ["name", "city", "region", "code"];

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task TwoToFourTextColumns_ApprovedInsertOrUpdate(
        int valueColumns, bool existing)
    {
        var columns = ValueColumns.Take(valueColumns).ToArray();
        await WithTableAsync(
            "id BIGINT PRIMARY KEY, " +
            string.Join(", ", columns.Select(c => c + " TEXT NOT NULL")),
            async (provider, cs, schema, runtime, token) =>
            {
                if (existing)
                    await ExecAsync(cs,
                        $"INSERT INTO {schema}.users VALUES (7," +
                        string.Join(", ", columns.Select(c => "'old-" + c + "'")) + ")", token);

                // Reverse SET order to prove the Host never assumes the SQL
                // assignment order equals the INSERT or physical table order.
                var sql = Upsert(schema, columns, reverseAssignments: true);
                var session = await PreviewAsync(provider, cs, runtime, token, sql);
                Assert.Equal(DmlApprovalMode.InsertConflictDoUpdate, session.Plan.ApprovalMode);
                Assert.Equal(columns.Length + 1, session.Plan.RowIdentityColumns.Length);
                Assert.Equal(1, session.Preview.AffectedRows);
                Assert.NotNull(session.Preview.Challenge.RowSetFingerprint);
                var preview = Assert.Single(session.Preview.Rows);
                Assert.Equal(existing ? "UPDATE" : "INSERT", preview["__action"]);
                Assert.Equal(7L, Convert.ToInt64(preview["id"]));
                foreach (var column in columns)
                {
                    Assert.Equal(existing ? "old-" + column : null,
                        preview["__before_" + column]);
                    Assert.Equal("new-" + column, preview["__after_" + column]);
                }

                var committed = await runtime.CommitAsync(provider, cs, session,
                    Policy, Allowed(schema), Context, token);
                Assert.True(committed.Committed);
                Assert.Equal(1, committed.AffectedRows);
                foreach (var column in columns)
                    Assert.Equal("new-" + column, await ScalarAsync(cs,
                        $"SELECT {column} FROM {schema}.users WHERE id=7", token));

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    runtime.CommitAsync(provider, cs, session,
                        Policy, Allowed(schema), Context, token));
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangeOnlySecondOldValueOrConflictBranch_CancelsEntireApproval(
        bool existing)
    {
        await WithTableAsync(
            "id BIGINT PRIMARY KEY, name TEXT NOT NULL, city TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                if (existing)
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES (7,'old-name','old-city')", token);
                var session = await PreviewAsync(provider, cs, runtime, token,
                    Upsert(schema, ["name", "city"]));
                if (existing)
                    await ExecAsync(cs,
                        $"UPDATE {schema}.users SET city='racer-city' WHERE id=7", token);
                else
                    await ExecAsync(cs,
                        $"INSERT INTO {schema}.users VALUES (7,'racer-name','racer-city')", token);
                var committed = await runtime.CommitAsync(provider, cs, session,
                    Policy, Allowed(schema), Context, token);
                Assert.False(committed.Committed);
                Assert.Equal(0, committed.AffectedRows);
                Assert.Equal(existing ? "old-name" : "racer-name", await ScalarAsync(cs,
                    $"SELECT name FROM {schema}.users WHERE id=7", token));
                Assert.Equal("racer-city", await ScalarAsync(cs,
                    $"SELECT city FROM {schema}.users WHERE id=7", token));
            });
    }

    [Theory]
    [InlineData("DO UPDATE SET name=EXCLUDED.name")]
    [InlineData("DO UPDATE SET name=EXCLUDED.name, city=EXCLUDED.name")]
    [InlineData("DO UPDATE SET name=EXCLUDED.name, name=EXCLUDED.name")]
    [InlineData("DO UPDATE SET name=EXCLUDED.name, city='constant'")]
    [InlineData("DO UPDATE SET name=EXCLUDED.name, city=city")]
    [InlineData("DO UPDATE SET name=EXCLUDED.name, city=EXCLUDED.city RETURNING id")]
    [InlineData("DO UPDATE SET name=EXCLUDED.name, city=EXCLUDED.city WHERE users.id=7")]
    [InlineData("DO UPDATE SET id=EXCLUDED.id, city=EXCLUDED.city")]
    public async Task UnprovedPartialOrMismatchedAssignments_AreRejected(string action)
    {
        await WithTableAsync(
            "id BIGINT PRIMARY KEY, name TEXT NOT NULL, city TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                var sql = $"INSERT INTO {schema}.users (id,name,city) VALUES (7,'new-name','new-city') " +
                    "ON CONFLICT (id) " + action;
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, runtime, token, sql));
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    [Theory]
    [InlineData("id BIGINT PRIMARY KEY, name TEXT NOT NULL, city TEXT")]
    [InlineData("id BIGINT PRIMARY KEY, name TEXT NOT NULL, city VARCHAR(50) NOT NULL")]
    [InlineData("id BIGINT PRIMARY KEY, name TEXT NOT NULL, city TEXT NOT NULL, hidden TEXT")]
    [InlineData("id BIGINT PRIMARY KEY, name TEXT NOT NULL, city TEXT NOT NULL CHECK (city <> '')")]
    [InlineData("id BIGINT PRIMARY KEY, name TEXT NOT NULL, city TEXT NOT NULL DEFAULT 'x'")]
    [InlineData("id BIGINT PRIMARY KEY, name TEXT NOT NULL, city TEXT NOT NULL UNIQUE")]
    public async Task UnsafePhysicalDefinition_DeniesEntireMultiColumnUpdate(string definition)
    {
        await WithTableAsync(definition, async (provider, cs, schema, runtime, token) =>
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                PreviewAsync(provider, cs, runtime, token, Upsert(schema, ["name", "city"])));
            Assert.Equal("0", await ScalarAsync(cs,
                $"SELECT count(*)::text FROM {schema}.users", token));
        });
    }

    [Fact]
    public async Task FiveUpdatedColumns_ExceedsBoundedCapability()
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT NOT NULL, city TEXT NOT NULL, " +
            "region TEXT NOT NULL, code TEXT NOT NULL, extra TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                var sql = Upsert(schema, ["name", "city", "region", "code", "extra"]);
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, runtime, token, sql));
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    [Fact]
    public async Task OneIndexRevokedAfterPreview_RejectsWithoutPartialUpdate()
    {
        await WithTableAsync("id BIGINT NOT NULL, name TEXT NOT NULL, city TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                await ExecAsync(cs, $"CREATE UNIQUE INDEX users_id_uq ON {schema}.users(id)", token);
                await ExecAsync(cs,
                    $"INSERT INTO {schema}.users VALUES (7,'old-name','old-city')", token);
                var session = await PreviewAsync(provider, cs, runtime, token,
                    Upsert(schema, ["name", "city"]));
                await ExecAsync(cs, $"DROP INDEX {schema}.users_id_uq", token);
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    runtime.CommitAsync(provider, cs, session,
                        Policy, Allowed(schema), Context, token));
                Assert.Equal("old-city", await ScalarAsync(cs,
                    $"SELECT city FROM {schema}.users WHERE id=7", token));
            });
    }

    private static string Upsert(string schema, IReadOnlyList<string> columns,
        bool reverseAssignments = false)
    {
        var fields = columns.Prepend("id").ToArray();
        var assignments = reverseAssignments ? columns.Reverse() : columns.AsEnumerable();
        return $"INSERT INTO {schema}.users ({string.Join(",", fields)}) VALUES (7," +
            string.Join(",", columns.Select(c => "'new-" + c + "'")) + ") " +
            "ON CONFLICT (id) DO UPDATE SET " +
            string.Join(", ", assignments.Select(c => c + "=EXCLUDED." + c));
    }

    private static Task<TypedDmlApprovalSession> PreviewAsync(
        PostgresProvider provider, string cs, TypedDmlRuntime runtime,
        CancellationToken token, string sql) =>
        PreviewParsedAsync(provider, cs, runtime, token, sql);

    private static async Task<TypedDmlApprovalSession> PreviewParsedAsync(
        PostgresProvider provider, string cs, TypedDmlRuntime runtime,
        CancellationToken token, string sql)
    {
        var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
            provider, cs, sql, SqlAgentToolType.Postgres, token);
        var table = sql.Split(".users", StringSplitOptions.None)[0]
            .Split(" INTO ", StringSplitOptions.None)[1].Trim() + ".users";
        return await runtime.PreviewAsync(provider, cs, parsed,
            Policy, new HashSet<string>(StringComparer.Ordinal) { table }, Context, token);
    }

    private static IReadOnlySet<string> Allowed(string schema) =>
        new HashSet<string>(StringComparer.Ordinal) { schema + ".users" };

    private async Task WithTableAsync(string columns,
        Func<PostgresProvider, string, string, TypedDmlRuntime, CancellationToken, Task> action)
    {
        var cs = fixture.ConnectionString;
        var token = TestContext.Current.CancellationToken;
        var schema = "m12_pg_" + Guid.NewGuid().ToString("N");
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
