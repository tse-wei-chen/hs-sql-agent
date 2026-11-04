using Admin.Service.Models;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// PG16 live RED: the independently approved single-row conflict-skip may
/// disclose only its physically proven key, and only after a confirmed commit.
/// </summary>
public sealed class TypedDmlPostgresDoNothingReturningTests(PostgresDdlLiveFixture fixture)
    : IClassFixture<PostgresDdlLiveFixture>
{
    private static readonly SecurityPolicyModel Policy = new() { DmlMaxAffectedRows = 1 };
    private static readonly DmlApprovalExecutionContext Context =
        new("mcp-user:g9-skip-returning", "db-management:1", SqlAgentToolType.Postgres, "ddl_live");

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ProvenKeyReturning_OnlyActualInsertedRowAfterCommit(bool primaryKey, bool existing)
    {
        await WithTableAsync(primaryKey
            ? "id BIGINT PRIMARY KEY, name TEXT NOT NULL"
            : "id BIGINT NOT NULL UNIQUE, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, ct) =>
            {
                if (existing)
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES(7,'original')", ct);
                var session = await PreviewAsync(provider, cs, schema, runtime, ct, "RETURNING id");
                Assert.Equal(DmlApprovalMode.InsertConflictDoNothing, session.Plan.ApprovalMode);
                Assert.True(session.Plan.MutationCommand.ReturnsRows);
                Assert.Equal(existing ? 0 : 1, session.Preview.AffectedRows);
                var result = await runtime.CommitAsync(provider, cs, session,
                    Policy, Allowed(schema), Context, ct);
                Assert.True(result.Committed);
                Assert.Equal(existing ? 0 : 1, result.AffectedRows);
                if (existing)
                    Assert.Empty(result.ReturnedRows);
                else
                {
                    var row = Assert.Single(result.ReturnedRows);
                    Assert.Single(row);
                    Assert.Equal(7L, Convert.ToInt64(row["id"]));
                }
                Assert.Equal(existing ? "original" : "new-name", await ScalarAsync(cs,
                    $"SELECT name FROM {schema}.users WHERE id=7", ct));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    runtime.CommitAsync(provider, cs, session, Policy, Allowed(schema), Context, ct));
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictChangedAfterPreview_DoesNotReturnOrMutate(bool presentAtPreview)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, ct) =>
            {
                if (presentAtPreview)
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES(7,'original')", ct);
                var session = await PreviewAsync(provider, cs, schema, runtime, ct, "RETURNING id");
                Assert.Equal(presentAtPreview ? 0 : 1, session.Preview.AffectedRows);
                if (presentAtPreview)
                    await ExecAsync(cs, $"DELETE FROM {schema}.users WHERE id=7", ct);
                else
                    await ExecAsync(cs, $"INSERT INTO {schema}.users VALUES(7,'racer')", ct);
                var result = await runtime.CommitAsync(provider, cs, session,
                    Policy, Allowed(schema), Context, ct);
                Assert.False(result.Committed);
                Assert.Empty(result.ReturnedRows);
                Assert.Equal(presentAtPreview ? "0" : "1", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", ct));
            });
    }

    [Fact]
    public async Task UniqueIndexRevokedAfterPreview_FailsClosedWithoutReturnedData()
    {
        await WithTableAsync("id BIGINT NOT NULL, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, ct) =>
            {
                await ExecAsync(cs, $"CREATE UNIQUE INDEX users_key_uq ON {schema}.users(id)", ct);
                var session = await PreviewAsync(provider, cs, schema, runtime, ct, "RETURNING id");
                await ExecAsync(cs, $"DROP INDEX {schema}.users_key_uq", ct);
                await Assert.ThrowsAnyAsync<Exception>(() => runtime.CommitAsync(
                    provider, cs, session, Policy, Allowed(schema), Context, ct));
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", ct));
            });
    }

    [Theory]
    [InlineData("RETURNING name")]
    [InlineData("RETURNING id,name")]
    [InlineData("RETURNING id,id")]
    [InlineData("RETURNING *")]
    [InlineData("RETURNING id AS renamed")]
    [InlineData("RETURNING users.id")]
    [InlineData("RETURNING id+1")]
    [InlineData("RETURNING ctid")]
    [InlineData("RETURNING xmax")]
    [InlineData("RETURNING \"ID\"")]
    public async Task UnapprovedProjectionNeverMintsApproval(string returning)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, name TEXT NOT NULL",
            async (provider, cs, schema, runtime, ct) =>
            {
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, ct, returning));
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", ct));
            });
    }

    private static async Task<TypedDmlApprovalSession> PreviewAsync(
        PostgresProvider provider, string cs, string schema, TypedDmlRuntime runtime,
        CancellationToken ct, string returning)
    {
        var sql = $"INSERT INTO {schema}.users (id,name) VALUES (7,'new-name') " +
            $"ON CONFLICT(id) DO NOTHING {returning}";
        var parsed = await runtime.ParseDmlWithVerifiedRuntimeProfileAsync(
            provider, cs, sql, SqlAgentToolType.Postgres, ct);
        return await runtime.PreviewAsync(provider, cs, parsed, Policy,
            Allowed(schema), Context, ct);
    }

    private static IReadOnlySet<string> Allowed(string schema) =>
        new HashSet<string>(StringComparer.Ordinal) { schema + ".users" };

    private async Task WithTableAsync(string definition,
        Func<PostgresProvider, string, string, TypedDmlRuntime, CancellationToken, Task> action)
    {
        var cs = fixture.ConnectionString;
        var ct = TestContext.Current.CancellationToken;
        var schema = "g9_skip_ret_" + Guid.NewGuid().ToString("N");
        var provider = new PostgresProvider();
        var snapshot = new NativeDmlSchemaSnapshotService();
        var runtime = new TypedDmlRuntime(
            schemaSnapshotSource: snapshot, schemaFreshnessVerifier: snapshot);
        await ExecAsync(cs, $"CREATE SCHEMA {schema}", ct);
        try
        {
            await ExecAsync(cs, $"CREATE TABLE {schema}.users ({definition})", ct);
            await action(provider, cs, schema, runtime, ct);
        }
        finally
        {
            await ExecAsync(cs, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
        }
    }

    private static async Task ExecAsync(string cs, string sql, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> ScalarAsync(string cs, string sql, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, db);
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct))!;
    }
}
