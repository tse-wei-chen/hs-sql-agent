using Admin.Service.Models;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using Npgsql;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real PG 16 Host safety regression: F# exact-NUMERIC normalization must
/// never launder an exponent/fractional source token into an authorized
/// plain integer literal. PostgreSQL quoted identifiers must not alias
/// different row-fingerprint identities through case-insensitive lookups.
/// </summary>
public sealed class TypedDmlPostgresIntegerTokenAndCaseFoldTests(
    PostgresDdlLiveFixture fixture) : IClassFixture<PostgresDdlLiveFixture>
{
    private static readonly SecurityPolicyModel Policy = new() { DmlMaxAffectedRows = 1 };
    private static readonly DmlApprovalExecutionContext Context =
        new("mcp-user:m13-fence", "db-management:1", SqlAgentToolType.Postgres, "ddl_live");

    [Theory]
    [InlineData("1e10")]
    [InlineData("4.2e1")]
    [InlineData("42.0")]
    [InlineData("4.20e1")]
    [InlineData("1E+1")]
    public async Task IntegralButNonIntegerSourceToken_NeverMintsNativeApproval(string source)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, amount BIGINT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                var sql = $"INSERT INTO {schema}.users (id,amount) VALUES (7,{source}) " +
                    "ON CONFLICT (id) DO UPDATE SET amount=EXCLUDED.amount";
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token, sql));
                Assert.Equal("0", await ScalarAsync(cs,
                    $"SELECT count(*)::text FROM {schema}.users", token));
            });
    }

    [Theory]
    [InlineData("SMALLINT NOT NULL", "3e1")]
    [InlineData("INTEGER NOT NULL", "1e2")]
    [InlineData("BIGINT NOT NULL", "42.00")]
    public async Task AllIntegerFamilies_RejectExponentOrScaleEvenWhenExact(
        string definition, string literal)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, amount " + definition,
            async (provider, cs, schema, runtime, token) =>
            {
                var sql = $"INSERT INTO {schema}.users (id,amount) VALUES (7,{literal}) " +
                    "ON CONFLICT (id) DO UPDATE SET amount=EXCLUDED.amount";
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token, sql));
            });
    }

    [Theory]
    [InlineData("42")]
    [InlineData("2147483648")]
    [InlineData("-12")]
    [InlineData("9223372036854775807")]
    public async Task GenuinePlainIntegerToken_StillApprovesAndCommits(string integer)
    {
        await WithTableAsync("id BIGINT PRIMARY KEY, amount BIGINT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                var sql = $"INSERT INTO {schema}.users (id,amount) VALUES (7,{integer}) " +
                    "ON CONFLICT (id) DO UPDATE SET amount=EXCLUDED.amount";
                var session = await PreviewAsync(provider, cs, schema, runtime, token, sql);
                Assert.Equal(DmlApprovalMode.InsertConflictDoUpdate, session.Plan.ApprovalMode);
                Assert.Equal(1, session.Preview.AffectedRows);
                var before = Assert.Single(session.Preview.Rows);
                Assert.Equal(long.Parse(integer),
                    Convert.ToInt64(before["__after_amount"]));
                var committed = await runtime.CommitAsync(provider, cs, session,
                    Policy, Allowed(schema), Context, token);
                Assert.True(committed.Committed);
                Assert.Equal(integer, await ScalarAsync(cs,
                    $"SELECT amount::text FROM {schema}.users WHERE id=7", token));
            });
    }

    [Theory]
    [InlineData("ID")]
    [InlineData("Id")]
    public async Task DistinctQuotedColumnNameCannotAliasKeyDuringRowFingerprint(string quoted)
    {
        await WithTableAsync($"id BIGINT PRIMARY KEY, \"{quoted}\" BIGINT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                var sql = $"INSERT INTO {schema}.users (id,\"{quoted}\") VALUES (7,42) " +
                    $"ON CONFLICT (id) DO UPDATE SET \"{quoted}\"=EXCLUDED.\"{quoted}\"";
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token, sql));
            });
    }

    [Fact]
    public async Task CaseFoldedSyntheticActionColumnCannotOverwritePreviewField()
    {
        await WithTableAsync("\"__ACTION\" BIGINT PRIMARY KEY, amount BIGINT NOT NULL",
            async (provider, cs, schema, runtime, token) =>
            {
                const string key = "\"__ACTION\"";
                var sql = $"INSERT INTO {schema}.users ({key},amount) VALUES (7,42) " +
                    $"ON CONFLICT ({key}) DO UPDATE SET amount=EXCLUDED.amount";
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    PreviewAsync(provider, cs, schema, runtime, token, sql));
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

    private async Task WithTableAsync(
        string definition,
        Func<PostgresProvider, string, string, TypedDmlRuntime, CancellationToken, Task> action)
    {
        var cs = fixture.ConnectionString;
        var token = TestContext.Current.CancellationToken;
        var schema = "m13_fence_" + Guid.NewGuid().ToString("N");
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
