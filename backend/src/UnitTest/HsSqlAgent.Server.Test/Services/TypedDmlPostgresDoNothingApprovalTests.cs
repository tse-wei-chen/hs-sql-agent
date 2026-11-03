using Admin.Service.Models;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real PostgreSQL 16 (Testcontainers) Host approval proof. No mocked driver:
/// physical UNIQUE inventory, target schema, locks and affected-row outcome are real.
/// </summary>
public sealed class TypedDmlPostgresDoNothingApprovalTests(PostgresDdlLiveFixture fixture)
    : IClassFixture<PostgresDdlLiveFixture>
{
    private static readonly SecurityPolicyModel Policy = new() { DmlMaxAffectedRows = 1 };

    [Theory]
    [InlineData(false, false, 1, "new-name")]
    [InlineData(false, true, 0, "original")]
    [InlineData(true, false, 1, "new-name")]
    [InlineData(true, true, 0, "original")]
    public async Task SolePhysicalPkOrUnique_ApprovedInsertAndSkip(
        bool primary, bool existing, int expectedAffected, string expectedName)
    {
        await WithTableAsync(primary
            ? "id BIGINT PRIMARY KEY, name TEXT NOT NULL"
            : "id BIGINT NOT NULL UNIQUE, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                if (existing)
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES (7, 'original')", token);
                var keys = await new DmlUniqueKeyResolver(provider).ResolveAsync(
                    cs, schema, "users", ["id"], token);
                Assert.True(keys.IsSoleEnforcedUniqueKey);
                Assert.False(keys.HasUnsupportedEnforcedUniqueKeys);
                Assert.Equal(primary, keys.MatchedKey.IsPrimaryKey);

                var preview = await PreviewAsync(provider, cs, schema, runtime, token);
                Assert.Equal(DmlApprovalMode.InsertConflictDoNothing, preview.Plan.ApprovalMode);
                Assert.Equal(expectedAffected, preview.Preview.AffectedRows);
                Assert.NotNull(preview.Preview.Challenge.RowSetFingerprint);
                var committed = await runtime.CommitAsync(provider, cs, preview,
                    Policy, Allowed(schema), Context, token);
                Assert.True(committed.Committed);
                Assert.Equal(expectedAffected, committed.AffectedRows);
                Assert.Equal(expectedName, await ScalarAsync(cs,
                    $"SELECT name FROM {schema}.users WHERE id=7", token));

                var replay = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    runtime.CommitAsync(provider, cs, preview, Policy, Allowed(schema), Context, token));
                Assert.Contains("consumed", replay.Message, StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public async Task UniqueIndexRemovedAfterPreview_IsRejectedBeforeDml()
    {
        await WithTableAsync("id BIGINT NOT NULL, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                await ExecAsync(cs,
                    $"CREATE UNIQUE INDEX users_id_uq ON {schema}.users(id)", token);
                var preview = await PreviewAsync(provider, cs, schema, runtime, token);
                Assert.Equal(1, preview.Preview.AffectedRows);
                await ExecAsync(cs, $"DROP INDEX {schema}.users_id_uq", token);
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    runtime.CommitAsync(provider, cs, preview, Policy, Allowed(schema), Context, token));
                Assert.Contains("stale", failure.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictPresenceChangesAfterPreview_CancelsWithoutMutation(bool existsAtPreview)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                if (existsAtPreview)
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES (7,'original')", token);
                var preview = await PreviewAsync(provider, cs, schema, runtime, token);
                Assert.Equal(existsAtPreview ? 0 : 1, preview.Preview.AffectedRows);
                if (existsAtPreview)
                    await ExecAsync(cs, $"DELETE FROM {schema}.users WHERE id = 7", token);
                else
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES (7,'racer')", token);

                var result = await runtime.CommitAsync(provider, cs, preview, Policy,
                    Allowed(schema), Context, token);
                Assert.False(result.Committed);
                Assert.Equal(0, result.AffectedRows);
                Assert.Equal(existsAtPreview ? "0" : "1", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    [Fact]
    public async Task QuotedCaseDistinctUniqueColumn_CannotAuthorizeDifferentUnquotedTarget()
    {
        // PostgreSQL column identity is case sensitive. The generic uniqueness
        // resolver may case-fold for other providers: this Host proof must not.
        await WithTableAsync(
            "id BIGINT NOT NULL, \"ID\" BIGINT NOT NULL UNIQUE, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token));
                Assert.Contains("exact", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    [Fact]
    public async Task UserTriggerCannotHideAdditionalWritesBehindOneRowApproval()
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                await ExecAsync(cs, $"""
                    CREATE FUNCTION {schema}.touch_row() RETURNS trigger
                    LANGUAGE plpgsql AS $ BEGIN RETURN NEW; END; $;
                    CREATE TRIGGER users_touch BEFORE INSERT ON {schema}.users
                    FOR EACH ROW EXECUTE FUNCTION {schema}.touch_row();
                    """, token);
                var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token));
                Assert.Contains("schema", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    [Theory]
    [InlineData("id BIGINT NOT NULL, name TEXT NOT NULL", "unique")]
    [InlineData("id BIGINT NOT NULL UNIQUE, name TEXT NOT NULL UNIQUE", "sole")]
    [InlineData("id BIGINT NOT NULL, name TEXT NOT NULL", "simple enforced")]
    [InlineData("id TEXT NOT NULL UNIQUE, name TEXT NOT NULL", "INT2")]
    public async Task UnsupportedUniqueShape_RefusesApproval(string columns, string reason)
    {
        await WithTableAsync(columns, async (provider, cs, schema, runtime, token) =>
        {
            if (reason == "simple enforced")
                await ExecAsync(cs,
                    $"CREATE UNIQUE INDEX users_id_partial ON {schema}.users(id) WHERE id > 0", token);
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                PreviewAsync(provider, cs, schema, runtime, token));
            Assert.Contains(reason, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("0", await ScalarAsync(cs,
                $"SELECT count(*)::text FROM {schema}.users", token));
        });
    }

    [Theory]
    [InlineData("DO UPDATE SET name=excluded.name")]
    [InlineData("DO NOTHING RETURNING id")]
    public async Task UnprovedConflictForms_NeverBecomeExecutable(string suffix)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                var sql = $"INSERT INTO {schema}.users (id, name) VALUES (7, 'new-name') " +
                    $"ON CONFLICT (id) {suffix}";
                var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
                        provider, cs, sql, SqlAgentToolType.Postgres, token);
                    await runtime.PreviewAsync(provider, cs, parsed,
                        Policy, Allowed(schema), Context, token);
                });
                Assert.True(error is NotSupportedException or InvalidOperationException
                    or SqlCompilationException);
            });
    }

    private static readonly DmlApprovalExecutionContext Context =
        new("mcp-user:m10", "db-management:1", SqlAgentToolType.Postgres, "ddl_live");
    private static IReadOnlySet<string> Allowed(string schema) =>
        new HashSet<string>(StringComparer.Ordinal) { schema + ".users" };

    private static async Task<TypedDmlApprovalSession> PreviewAsync(
        PostgresProvider provider, string cs, string schema, TypedDmlRuntime runtime,
        CancellationToken token)
    {
        var sql = $"INSERT INTO {schema}.users (id, name) VALUES (7, 'new-name') " +
            "ON CONFLICT (id) DO NOTHING";
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
        var schema = "m10_pg_" + Guid.NewGuid().ToString("N");
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
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string> ScalarAsync(string cs, string sql, CancellationToken token)
    {
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(token);
        await using var command = new NpgsqlCommand(sql, db);
        return Convert.ToString(await command.ExecuteScalarAsync(token))!;
    }
}
