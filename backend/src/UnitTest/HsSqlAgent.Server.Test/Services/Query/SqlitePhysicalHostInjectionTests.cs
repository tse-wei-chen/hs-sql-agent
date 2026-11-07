using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Moq;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// End-to-end Host MCP Query -> F# SqlCore -> first-party SQLite provider.
/// Uses an actual isolated on-disk database; a rejected injection must leave
/// physical table contents and catalog unchanged. No SQL executor is mocked.
/// </summary>
public sealed class SqlitePhysicalHostInjectionTests
{
    private const string InjectionShapedValue = "x'; DROP TABLE main.users; --";

    private static readonly string[] SecondStatements =
    [
        "DELETE FROM main.users WHERE id = 1",
        "UPDATE main.users SET name = 'hijacked' WHERE id = 1",
        "DROP TABLE main.users",
        "CREATE TABLE main.pwned(id INTEGER)",
        "INSERT INTO main.users(id,name) VALUES (99,'pwned')",
        "REPLACE INTO main.users(id,name) VALUES (1,'pwned')",
        "PRAGMA writable_schema = ON",
        "ATTACH DATABASE ':memory:' AS rogue"
    ];

    private static readonly string[] StatementSeparators =
    [
        "; ",
        ";\n",
        "; /* allegedly harmless */ ",
        "; -- hidden second statement\n"
    ];

    [Fact]
    public async Task StackedSqlInjection_NeverMutatesPhysicalSqliteAcross32HostRequests()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            var tool = CreateTool(connectionString);
            var before = await PhysicalSnapshotAsync(connectionString);
            var attempted = 0;

            foreach (var separator in StatementSeparators)
            foreach (var secondStatement in SecondStatements)
            {
                var sql = "SELECT id FROM main.users WHERE id = 1"
                    + separator + secondStatement;
                var response = await tool.ExecuteQuerySql(
                    sql, TestContext.Current.CancellationToken);

                Assert.False(response.Success, sql);
                Assert.NotNull(response.Error);
                Assert.Equal(before, await PhysicalSnapshotAsync(connectionString));
                attempted++;
            }

            Assert.Equal(32, attempted);
        });
    }

    [Fact]
    public async Task HiddenPhysicalSecretTable_CannotEscapeWhitelistViaCteJoinOrUnion()
    {
        var attempts = new[]
        {
            "SELECT secret FROM main.secrets",
            "WITH hidden AS (SELECT secret FROM main.secrets) SELECT secret FROM hidden",
            "SELECT u.id FROM main.users u JOIN main.secrets s ON u.id = s.id",
            "SELECT id FROM main.users UNION ALL SELECT id FROM main.secrets",
            "SELECT id FROM main.users WHERE EXISTS (SELECT 1 FROM main.secrets)"
        };

        await WithDatabaseAsync(async connectionString =>
        {
            var tool = CreateTool(connectionString);
            var before = await PhysicalSnapshotAsync(connectionString);

            foreach (var sql in attempts)
            {
                var response = await tool.ExecuteQuerySql(
                    sql, TestContext.Current.CancellationToken);

                Assert.False(response.Success, sql);
                Assert.NotNull(response.Error);
                Assert.Empty(response.Rows);
                Assert.Equal(before, await PhysicalSnapshotAsync(connectionString));
            }
        });
    }

    [Fact]
    public async Task RegisteredSqliteFunction_CanReadExcludedRealTable_ButMcpQueryRejectsCall()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            const string secretSentinel = "SENTINEL_PRIVATE_VALUE";
            var before = await PhysicalSnapshotAsync(connectionString);

            // Real SQLite function registered by the embedding host, not a mocked
            // executor. It executes another SELECT against the excluded table.
            // Without a native-function proof, the visible Query tables are not
            // enough to enforce the MCP key's whitelist.
            await using var connection = new SqliteConnection(connectionString);
            connection.CreateFunction("leak_secret", () =>
            {
                using var inner = new SqliteConnection(connectionString);
                inner.Open();
                using var cmd = inner.CreateCommand();
                cmd.CommandText = "SELECT secret FROM main.secrets WHERE id = 1";
                return cmd.ExecuteScalar()?.ToString();
            });
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using (var control = connection.CreateCommand())
            {
                control.CommandText = "SELECT leak_secret()";
                Assert.Equal(secretSentinel,
                    (await control.ExecuteScalarAsync(TestContext.Current.CancellationToken))?.ToString());
            }

            var connections = new Mock<IDbConnectionFactory>();
            connections.Setup(x => x.Create(connectionString)).Returns(connection);
            var provider = new Mock<ISqlProvider>();
            provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Sqlite);
            provider.SetupGet(x => x.Connections).Returns(connections.Object);

            var result = await CreateTool(connectionString, provider.Object).ExecuteQuerySql(
                "SELECT \"leak_secret\"() AS leaked FROM main.users WHERE id = 1",
                TestContext.Current.CancellationToken);

            Assert.False(result.Success, "Opaque SQLite functions must not bypass the table whitelist.");
            Assert.Empty(result.Rows);
            Assert.DoesNotContain(secretSentinel, result.ToString());
            Assert.Equal(before, await PhysicalSnapshotAsync(connectionString));
        });
    }

    [Fact]
    public async Task InjectionShapedString_IsBoundAsOneLiteralAndRemainsQueryable()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            const string query =
                "SELECT id FROM main.users WHERE name = 'x''; DROP TABLE main.users; --'";
            var provider = new SqliteProvider();
            var compiled = new TypedQueryRuntime().Compile(
                provider, query, SqlAgentToolType.Sqlite,
                ReadPolicy(), AllowedTables());

            Assert.DoesNotContain("DROP TABLE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(compiled.Parameters,
                parameter => Equals(parameter.Value, InjectionShapedValue));

            var before = await PhysicalSnapshotAsync(connectionString);
            var response = await CreateTool(connectionString).ExecuteQuerySql(
                query, TestContext.Current.CancellationToken);

            Assert.True(response.Success, response.Error?.Message);
            Assert.Equal(1, response.RowCount);
            var row = Assert.Single(response.Rows);
            Assert.Equal(2, Convert.ToInt32(row["id"]));
            Assert.Equal(before, await PhysicalSnapshotAsync(connectionString));
        });
    }

    [Fact]
    public async Task LegitimateOrAndCteQueries_AreNotBlacklistedAsInjections()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            var tool = CreateTool(connectionString);
            var before = await PhysicalSnapshotAsync(connectionString);
            var queries = new[]
            {
                "SELECT id FROM main.users WHERE id = 1 OR id = 2",
                "WITH visible AS (SELECT id FROM main.users) SELECT id FROM visible"
            };

            foreach (var query in queries)
            {
                var result = await tool.ExecuteQuerySql(
                    query, TestContext.Current.CancellationToken);
                Assert.True(result.Success, result.Error?.Message);
                Assert.Equal(2, result.RowCount);
                Assert.Equal(before, await PhysicalSnapshotAsync(connectionString));
            }
        });
    }

    private static readonly IReadOnlySet<string> UserTable =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main.users" };

    private static IReadOnlySet<string> AllowedTables() => UserTable;

    private static SecurityPolicyModel ReadPolicy() => new()
    {
        QueryMaxRows = 25,
        QueryTimeoutSeconds = 10
    };

    private static SqlAgentTool CreateTool(string connectionString, ISqlProvider? overrideProvider = null)
    {
        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.SqlProvider] = "Sqlite";
        context.Items[McpContextItemKeys.SqlConnectionString] = connectionString;
        context.Items[McpContextItemKeys.AllowedTools] = "execute_query_sql";
        context.Items[McpContextItemKeys.TableWhitelist] = "main.users";
        context.Items[McpContextItemKeys.AccessKeyId] = 7;
        context.Items[McpContextItemKeys.DbManagementId] = 11;
        context.Items[McpContextItemKeys.DatabaseName] = "main";

        var http = new Mock<IHttpContextAccessor>();
        http.SetupGet(x => x.HttpContext).Returns(context);
        var providers = new Mock<ISqlProviderFactory>(MockBehavior.Strict);
        providers.Setup(x => x.GetProvider(SqlAgentToolType.Sqlite))
            .Returns(overrideProvider ?? new SqliteProvider());
        var audit = new Mock<IAuditService>();
        audit.Setup(x => x.WriteEventAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AuditEventContext>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(ReadPolicy());
        var limiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        limiter.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());

        return new SqlAgentTool(http.Object, providers.Object, audit.Object,
            Mock.Of<IDbSemanticService>(), state.Object, limiter.Object,
            typedQueryRuntime: new TypedQueryRuntime());
    }

    private static async Task WithDatabaseAsync(Func<string, Task> scenario)
    {
        var file = Path.Combine(Path.GetTempPath(),
            $"hsa-host-injection-{Guid.NewGuid():N}.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Pooling = false
        }.ToString();
        try
        {
            await ExecuteSetupAsync(connectionString);
            await scenario(connectionString);
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    private static async Task ExecuteSetupAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE main.users (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO main.users (id,name)
                VALUES (1,'Alice'), (2,'x''; DROP TABLE main.users; --');
            CREATE TABLE main.secrets (id INTEGER PRIMARY KEY, secret TEXT NOT NULL);
            INSERT INTO main.secrets (id,secret) VALUES (1,'SENTINEL_PRIVATE_VALUE');
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> PhysicalSnapshotAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT group_concat(id || ':' || name, '|')
                 FROM (SELECT id, name FROM main.users ORDER BY id))
              || '|' || (SELECT count(*) FROM main.secrets)
              || '|' || (SELECT count(*) FROM main.sqlite_schema
                         WHERE type = 'table' AND name = 'pwned')
            """;
        return (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))
            ?.ToString() ?? throw new InvalidOperationException("SQLite evidence is absent.");
    }
}
