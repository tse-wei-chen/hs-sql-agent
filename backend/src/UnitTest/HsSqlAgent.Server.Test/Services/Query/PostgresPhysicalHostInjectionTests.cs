using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Provider.PostgreSql;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Moq;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

// Physical PostgreSQL 16 Query Host safety evidence; the SQL executor and database are real.
// Each scenario owns a throwaway schema; setup, snapshots, and teardown run outside MCP.
public sealed class PostgresPhysicalQueryFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("host_injection")
        .WithUsername("host_test")
        .WithPassword("HostTestPass123!")
        .Build();

    public string ConnectionString => _container.GetConnectionString();
    public ValueTask InitializeAsync() =>
        new(_container.StartAsync(TestContext.Current.CancellationToken));
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

public sealed class PostgresPhysicalHostInjectionTests(PostgresPhysicalQueryFixture fixture)
    : IClassFixture<PostgresPhysicalQueryFixture>
{
    private static readonly string[] Separators =
        ["; ", ";\n", "; /* covered by query policy */ ", "; -- second statement\n"];

    [Fact]
    public async Task StackedQueryToMutation_NoDatabaseChangesAcross32Requests()
    {
        await WithSchemaAsync(async schema =>
        {
            var next = new[]
            {
                "DELETE FROM " + schema + ".users WHERE id=1",
                "UPDATE " + schema + ".users SET name='hijacked' WHERE id=1",
                "DROP TABLE " + schema + ".users",
                "CREATE TABLE " + schema + ".pwned (id INTEGER)",
                "INSERT INTO " + schema + ".users(id,name) VALUES (99,'pwned')",
                "TRUNCATE TABLE " + schema + ".users",
                "ALTER TABLE " + schema + ".users ADD COLUMN injected INTEGER",
                "SELECT setval('" + schema + ".counter', 100, true)"
            };
            var before = await SnapshotAsync(schema);
            var tool = Tool(schema);
            var attempted = 0;
            foreach (var separator in Separators)
            foreach (var second in next)
            {
                var sql = "SELECT id FROM " + schema + ".users WHERE id=1" + separator + second;
                var result = await tool.ExecuteQuerySql(sql, TestContext.Current.CancellationToken);
                Assert.False(result.Success, sql);
                Assert.Empty(result.Rows);
                Assert.Equal(before, await SnapshotAsync(schema));
                attempted++;
            }
            Assert.Equal(32, attempted);
        });
    }

    [Fact]
    public async Task UnauthorizedTables_CannotEscapePhysicalWhitelistViaCteJoinUnionOrExists()
    {
        await WithSchemaAsync(async schema =>
        {
            var attempts = new[]
            {
                "SELECT secret FROM " + schema + ".secrets",
                "WITH hidden AS (SELECT secret FROM " + schema + ".secrets) SELECT secret FROM hidden",
                "SELECT u.id FROM " + schema + ".users u JOIN " + schema + ".secrets s ON u.id=s.id",
                "SELECT id FROM " + schema + ".users UNION ALL SELECT id FROM " + schema + ".secrets",
                "SELECT id FROM " + schema + ".users WHERE EXISTS (SELECT 1 FROM " + schema + ".secrets)"
            };
            var before = await SnapshotAsync(schema);
            var tool = Tool(schema);
            foreach (var sql in attempts)
            {
                var result = await tool.ExecuteQuerySql(sql, TestContext.Current.CancellationToken);
                Assert.False(result.Success, sql);
                Assert.Empty(result.Rows);
                Assert.Equal(before, await SnapshotAsync(schema));
            }
        });
    }

    // PostgreSQL functions can change persisted sequence state even in a SELECT.
    // This test is intentionally RED against a Host that only checks statement kind.
    [Theory]
    [InlineData("nextval")]
    [InlineData("setval")]
    public async Task QuerySideEffectFunctions_MustNotChangeRealSequenceState(string function)
    {
        await WithSchemaAsync(async schema =>
        {
            var sql = function == "nextval"
                ? "SELECT nextval('" + schema + ".counter') AS value"
                : "SELECT setval('" + schema + ".counter', 99, true) AS value";
            var before = await SnapshotAsync(schema);
            var response = await Tool(schema).ExecuteQuerySql(
                sql, TestContext.Current.CancellationToken);
            Assert.False(response.Success, sql);
            Assert.Empty(response.Rows);
            Assert.Equal(before, await SnapshotAsync(schema));
        });
    }

    [Fact]
    public async Task VolatileUserDefinedFunction_CannotWriteThroughAllowedSelect()
    {
        await WithSchemaAsync(async schema =>
        {
            // Actual database function, deliberately volatile and permitted to UPDATE
            // the whitelisted relation when run outside a read-only transaction.
            await ExecuteAsync("CREATE FUNCTION " + schema +
                ".mutate_user() RETURNS integer LANGUAGE plpgsql VOLATILE AS $$ " +
                "BEGIN UPDATE " + schema + ".users SET name='hijacked' WHERE id=1; " +
                "RETURN 1; END; $$");
            var sql = "SELECT " + schema + ".mutate_user() AS value FROM " +
                schema + ".users WHERE id=1";
            var before = await SnapshotAsync(schema);
            var response = await Tool(schema).ExecuteQuerySql(
                sql, TestContext.Current.CancellationToken);
            Assert.False(response.Success, sql);
            Assert.Empty(response.Rows);
            Assert.Equal(before, await SnapshotAsync(schema));
        });
    }

    // A READ ONLY transaction prohibits mutation but cannot enforce the MCP table whitelist:
    // PostgreSQL's native query_to_xml executes its argument as a separate SQL statement.
    [Fact]
    public async Task PgCatalogQueryToXml_CannotReadOutsideTableWhitelist()
    {
        await WithSchemaAsync(async schema =>
        {
            var hiddenSql = "SELECT secret FROM " + schema + ".secrets";
            var directSql = "SELECT pg_catalog.query_to_xml('" + hiddenSql +
                "', true, false, '')::text";
            await using (var direct = new NpgsqlConnection(fixture.ConnectionString))
            {
                await direct.OpenAsync(TestContext.Current.CancellationToken);
                await using var cmd = new NpgsqlCommand(directSql, direct);
                var xml = (await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))?.ToString();
                Assert.Contains("SENTINEL_PRIVATE_VALUE", xml);
            }

            var before = await SnapshotAsync(schema);
            var result = await Tool(schema).ExecuteQuerySql(
                "SELECT pg_catalog.query_to_xml('" + hiddenSql +
                "', true, false, '') AS leaked FROM " + schema + ".users WHERE id=1",
                TestContext.Current.CancellationToken);
            Assert.False(result.Success, "An opaque read-only SQL function bypassed the table whitelist.");
            Assert.DoesNotContain("SENTINEL_PRIVATE_VALUE", result.ToString());
            Assert.Equal(before, await SnapshotAsync(schema));
        });
    }

    // STABLE read-only user functions also have privileged visibility to tables
    // outside the SQL compiler's visible relation graph.
    [Fact]
    public async Task QualifiedSqlFunction_CannotReadOutsideTableWhitelist()
    {
        await WithSchemaAsync(async schema =>
        {
            await ExecuteAsync("CREATE FUNCTION " + schema +
                ".leak_secret() RETURNS text LANGUAGE sql STABLE AS 'SELECT secret FROM " +
                schema + ".secrets WHERE id=1'");
            var before = await SnapshotAsync(schema);
            var result = await Tool(schema).ExecuteQuerySql(
                "SELECT " + schema + ".leak_secret() AS leaked FROM " +
                schema + ".users WHERE id=1",
                TestContext.Current.CancellationToken);
            Assert.False(result.Success, "A qualified read-only UDF bypassed the table whitelist.");
            Assert.DoesNotContain("SENTINEL_PRIVATE_VALUE", result.ToString());
            Assert.Equal(before, await SnapshotAsync(schema));
        });
    }

    [Fact]
    public async Task WhitelistedBuiltin_ResolvesToPgCatalogEvenWhenSearchPathShadowsLower()
    {
        await WithSchemaAsync(async schema =>
        {
            // If this unqualified name resolved through the caller's search_path,
            // the same SELECT would disclose the excluded table.
            await ExecuteAsync("CREATE FUNCTION " + schema +
                ".lower(text) RETURNS text LANGUAGE sql STABLE AS 'SELECT secret FROM " +
                schema + ".secrets WHERE id=1'");
            var shadowedConnection = fixture.ConnectionString +
                ";Search Path=" + schema + ",pg_catalog,public";
            await using (var direct = new NpgsqlConnection(shadowedConnection))
            {
                await direct.OpenAsync(TestContext.Current.CancellationToken);
                await using var cmd = new NpgsqlCommand(
                    "SELECT lower(name) FROM " + schema + ".users WHERE id=1", direct);
                Assert.Equal("SENTINEL_PRIVATE_VALUE",
                    await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }

            var before = await SnapshotAsync(schema);
            var result = await Tool(schema, shadowedConnection).ExecuteQuerySql(
                "SELECT lower(name) AS normalized FROM " + schema + ".users WHERE id=1",
                TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Error?.Message);
            Assert.Equal("alice", Assert.Single(result.Rows)["normalized"]?.ToString());
            Assert.Equal(before, await SnapshotAsync(schema));
        });
    }

    [Fact]
    public async Task BoundInjectionShapedLiteral_IsQueryableWithoutExecutingPayload()
    {
        await WithSchemaAsync(async schema =>
        {
            var sql = "SELECT id FROM " + schema +
                ".users WHERE name='x''; DROP TABLE public.users; --'";
            var provider = new PostgresProvider();
            var compiled = new TypedQueryRuntime().Compile(
                provider, sql, SqlAgentToolType.Postgres, Policy(), Allowed(schema));
            Assert.DoesNotContain("DROP TABLE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(compiled.Parameters, p =>
                Equals(p.Value, "x'; DROP TABLE public.users; --"));
            var before = await SnapshotAsync(schema);
            var result = await Tool(schema).ExecuteQuerySql(
                sql, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Error?.Message);
            Assert.Equal(2, Convert.ToInt32(Assert.Single(result.Rows)["id"]));
            Assert.Equal(before, await SnapshotAsync(schema));
        });
    }

    [Fact]
    public async Task OrdinaryOrAndCte_RemainUsableWithoutModifyingDatabase()
    {
        await WithSchemaAsync(async schema =>
        {
            var tool = Tool(schema);
            var before = await SnapshotAsync(schema);
            foreach (var sql in new[]
            {
                "SELECT id FROM " + schema + ".users WHERE id=1 OR id=2",
                "WITH visible AS (SELECT id FROM " + schema + ".users) SELECT id FROM visible"
            })
            {
                var result = await tool.ExecuteQuerySql(sql, TestContext.Current.CancellationToken);
                Assert.True(result.Success, result.Error?.Message);
                Assert.Equal(2, result.RowCount);
                Assert.Equal(before, await SnapshotAsync(schema));
            }
        });
    }

    private static IReadOnlySet<string> Allowed(string schema) =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { schema + ".users" };

    private static SecurityPolicyModel Policy() => new()
    {
        QueryMaxRows = 25,
        QueryTimeoutSeconds = 10
    };

    private SqlAgentTool Tool(string schema, string? overrideConnectionString = null)
    {
        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.SqlProvider] = "Postgres";
        context.Items[McpContextItemKeys.SqlConnectionString] = overrideConnectionString ?? fixture.ConnectionString;
        context.Items[McpContextItemKeys.AllowedTools] = "execute_query_sql";
        context.Items[McpContextItemKeys.TableWhitelist] = schema + ".users";
        context.Items[McpContextItemKeys.AccessKeyId] = 7;
        context.Items[McpContextItemKeys.DbManagementId] = 11;
        context.Items[McpContextItemKeys.DatabaseName] = "host_injection";
        var http = new Mock<IHttpContextAccessor>();
        http.SetupGet(x => x.HttpContext).Returns(context);
        var providers = new Mock<ISqlProviderFactory>(MockBehavior.Strict);
        providers.Setup(x => x.GetProvider(SqlAgentToolType.Postgres))
            .Returns(new PostgresProvider());
        var audit = new Mock<IAuditService>();
        audit.Setup(x => x.WriteEventAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AuditEventContext>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var security = new Mock<ISecurityPolicyRuntimeState>();
        security.Setup(x => x.GetCurrent()).Returns(Policy());
        var limiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        limiter.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        return new SqlAgentTool(http.Object, providers.Object, audit.Object,
            Mock.Of<IDbSemanticService>(), security.Object, limiter.Object,
            typedQueryRuntime: new TypedQueryRuntime());
    }

    private async Task WithSchemaAsync(Func<string, Task> test)
    {
        var schema = "inj_" + Guid.NewGuid().ToString("N");
        try
        {
            await ExecuteAsync("CREATE SCHEMA " + schema);
            await ExecuteAsync("CREATE TABLE " + schema +
                ".users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
            await ExecuteAsync("INSERT INTO " + schema +
                ".users (id,name) VALUES (1,'Alice'), (2,'x''; DROP TABLE public.users; --')");
            await ExecuteAsync("CREATE TABLE " + schema +
                ".secrets (id INTEGER PRIMARY KEY, secret TEXT NOT NULL)");
            await ExecuteAsync("INSERT INTO " + schema +
                ".secrets (id,secret) VALUES (1,'SENTINEL_PRIVATE_VALUE')");
            await ExecuteAsync("CREATE SEQUENCE " + schema + ".counter START WITH 10");
            await test(schema);
        }
        finally
        {
            await ExecuteAsync("DROP SCHEMA IF EXISTS " + schema + " CASCADE");
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> SnapshotAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT " +
            "(SELECT string_agg(id::text || ':' || name, '|' ORDER BY id) FROM " + schema + ".users) || '|' || " +
            "(SELECT count(*)::text FROM " + schema + ".secrets) || '|' || " +
            "(SELECT count(*)::text FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n " +
            "ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname='pwned') || '|' || " +
            "(SELECT count(*)::text FROM information_schema.columns " +
            "WHERE table_schema=@schema AND table_name='users' AND column_name='injected') || '|' || " +
            "(SELECT last_value::text || ':' || is_called::text FROM " + schema + ".counter)",
            connection);
        cmd.Parameters.AddWithValue("schema", schema);
        return (await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))?.ToString()
            ?? throw new InvalidOperationException("Physical PostgreSQL snapshot was absent.");
    }
}
