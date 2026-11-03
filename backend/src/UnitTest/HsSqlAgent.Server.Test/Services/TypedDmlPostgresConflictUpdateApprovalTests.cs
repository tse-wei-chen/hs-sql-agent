using Admin.Service.Models;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real PostgreSQL 16 Testcontainers Host proof. An approved UPDATE branch
/// must bind BOTH the old physical row and the proposed new value; it must
/// never silently downgrade to INSERT-only payload approval.
/// </summary>
public sealed class TypedDmlPostgresConflictUpdateApprovalTests(PostgresDdlLiveFixture fixture)
    : IClassFixture<PostgresDdlLiveFixture>
{
    private static readonly SecurityPolicyModel Policy = new() { DmlMaxAffectedRows = 1 };
    private static readonly DmlApprovalExecutionContext Context =
        new("mcp-user:m11", "db-management:1", SqlAgentToolType.Postgres, "ddl_live");

    [Theory]
    [InlineData(false, "INSERT", null)]
    [InlineData(true, "UPDATE", "old-name")]
    public async Task NativePg_SoleKey_OneRowPreviewAndCommitBindsBeforeAfter(
        bool existing, string expectedAction, string? beforeName)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                if (existing)
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES (7, 'old-name')", token);

                var session = await PreviewAsync(provider, cs, schema, runtime, token);
                Assert.Equal("InsertConflictDoUpdate", session.Plan.ApprovalMode.ToString());
                Assert.Equal(1, session.Preview.AffectedRows);
                var change = Assert.Single(session.Preview.Rows);
                Assert.Equal(expectedAction, change["__action"]);
                Assert.Equal(beforeName, change["__before_name"]);
                Assert.Equal("new-name", change["__after_name"]);
                Assert.NotNull(session.Preview.Challenge.RowSetFingerprint);

                var committed = await runtime.CommitAsync(provider, cs, session, Policy,
                    Allowed(schema), Context, token);
                Assert.True(committed.Committed);
                Assert.Equal(1, committed.AffectedRows);
                Assert.Equal("new-name", await ScalarAsync(cs,
                    $"SELECT name FROM {schema}.users WHERE id = 7", token));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    runtime.CommitAsync(provider, cs, session, Policy,
                        Allowed(schema), Context, token));
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePg_ChangedConflictOrOldRow_RejectsApprovedWrite(bool existing)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                if (existing)
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES (7, 'old-name')", token);
                var session = await PreviewAsync(provider, cs, schema, runtime, token);
                if (existing)
                    await ExecAsync(cs, $"UPDATE {schema}.users SET name='racer' WHERE id=7", token);
                else
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES (7, 'racer')", token);

                var result = await runtime.CommitAsync(provider, cs, session,
                    Policy, Allowed(schema), Context, token);
                Assert.False(result.Committed);
                Assert.Equal(0, result.AffectedRows);
                Assert.Equal("racer", await ScalarAsync(cs,
                    $"SELECT name FROM {schema}.users WHERE id = 7", token));
            });
    }

    [Fact]
    public async Task NativePg_IndexRevokedAfterPreview_PreventsUpdate()
    {
        await WithTableAsync("id BIGINT NOT NULL, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                await ExecAsync(cs, $"CREATE UNIQUE INDEX users_id_uq ON {schema}.users(id)", token);
                await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES (7, 'old-name')", token);
                var session = await PreviewAsync(provider, cs, schema, runtime, token);
                await ExecAsync(cs, $"DROP INDEX {schema}.users_id_uq", token);
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    runtime.CommitAsync(provider, cs, session,
                        Policy, Allowed(schema), Context, token));
                Assert.Equal("old-name", await ScalarAsync(cs,
                    $"SELECT name FROM {schema}.users WHERE id=7", token));
            });
    }

    [Theory]
    [InlineData("DO UPDATE SET id=EXCLUDED.id")]
    [InlineData("DO UPDATE SET name='forced'")]
    [InlineData("DO UPDATE SET name=upper(EXCLUDED.name)")]
    [InlineData("DO UPDATE SET name=EXCLUDED.name WHERE users.name <> EXCLUDED.name")]
    [InlineData("DO UPDATE SET name=EXCLUDED.name RETURNING id")]
    [InlineData("DO UPDATE SET name=EXCLUDED.name, id=EXCLUDED.id")]
    public async Task NativePg_UnprovenUpdateFormsNeverAuthorize(string action)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                var sql = $"INSERT INTO {schema}.users (id, name) VALUES (7,'new-name') " +
                    "ON CONFLICT (id) " + action;
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token, sql));
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    [Theory]
    [InlineData("id BIGINT NOT NULL UNIQUE, name TEXT NOT NULL UNIQUE")]
    [InlineData("id BIGINT PRIMARY KEY, name TEXT")]
    [InlineData("id BIGINT PRIMARY KEY, name TEXT NOT NULL, side_effect TEXT")]
    [InlineData("id BIGINT PRIMARY KEY, name TEXT NOT NULL CHECK (name <> '')")]
    [InlineData("id TEXT PRIMARY KEY, name TEXT NOT NULL")]
    public async Task NativePg_UnsafePhysicalTableNeverAuthorizes(string columns)
    {
        await WithTableAsync(columns, async (provider, cs, schema, runtime, token) =>
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                PreviewAsync(provider, cs, schema, runtime, token));
            Assert.Equal("0", await ScalarAsync(cs,
                $"SELECT count(*)::text FROM {schema}.users", token));
        });
    }

    private static IReadOnlySet<string> Allowed(string schema) =>
        new HashSet<string>(StringComparer.Ordinal) { schema + ".users" };

    private static async Task<TypedDmlApprovalSession> PreviewAsync(
        PostgresProvider provider, string cs, string schema,
        TypedDmlRuntime runtime, CancellationToken token, string? sql = null)
    {
        sql ??= $"INSERT INTO {schema}.users (id, name) VALUES (7,'new-name') " +
            "ON CONFLICT (id) DO UPDATE SET name=EXCLUDED.name";
        var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
            provider, cs, sql, SqlAgentToolType.Postgres, token);
        return await runtime.PreviewAsync(provider, cs, parsed,
            Policy, Allowed(schema), Context, token);
    }

    private async Task WithTableAsync(string columns,
        Func<PostgresProvider, string, string, TypedDmlRuntime, CancellationToken, Task> action)
    {
        var cs = fixture.ConnectionString;
        var token = TestContext.Current.CancellationToken;
        var schema = "m11_pg_" + Guid.NewGuid().ToString("N");
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
