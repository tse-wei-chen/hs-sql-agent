using System.Data.Common;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using FirebirdSql.Data.FirebirdClient;
using HsSqlAgent.Provider.Firebird;
using HsSqlAgent.Provider.Oracle;
using HsSqlAgent.Provider.SqlServer;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Moq;
using Oracle.ManagedDataAccess.Client;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// #575: live, independently measured hidden-table reads through native UDFs.
/// Each provider's direct control reads the excluded relation from a real DB;
/// the published MCP Query tool must then fail closed for the same function
/// when only the visible users table is granted. No mocked SQL executor.
/// </summary>
public sealed class QueryNativeFunctionLiveTests
{
    private const string Secret = "SENTINEL_PRIVATE_VALUE";

    [Fact]
    public async Task SqlServer_QualifiedUdfReadsExcludedPhysicalTableButMcpRejects()
    {
        var connectionString = RequireConnection("TEST_SQLSERVER_CONNECTION", "SQL Server 2022");
        var ct = TestContext.Current.CancellationToken;
        var database = "hsqlq_" + Guid.NewGuid().ToString("N")[..15];
        await using var admin = new SqlConnection(connectionString);
        for (var attempt = 0; ; attempt++)
        {
            try { await admin.OpenAsync(ct); break; }
            catch (SqlException) when (attempt < 24 && !ct.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        }
        await ExecuteAsync(admin, "CREATE DATABASE [" + database + "]", ct);
        try
        {
            var target = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = database,
                Pooling = false
            }.ConnectionString;
            await using var setup = new SqlConnection(target);
            await setup.OpenAsync(ct);
            await ExecuteAsync(setup, "CREATE TABLE dbo.users (id INT PRIMARY KEY)", ct);
            await ExecuteAsync(setup, "CREATE TABLE dbo.secrets (id INT PRIMARY KEY, secret NVARCHAR(64))", ct);
            await ExecuteAsync(setup, "INSERT INTO dbo.users VALUES (1)", ct);
            await ExecuteAsync(setup, "INSERT INTO dbo.secrets VALUES (1, N'" + Secret + "')", ct);
            await ExecuteAsync(setup, """
                CREATE FUNCTION dbo.leak_secret() RETURNS NVARCHAR(64)
                AS
                BEGIN
                    DECLARE @value NVARCHAR(64);
                    SELECT @value = secret FROM dbo.secrets WHERE id = 1;
                    RETURN @value;
                END
                """, ct);

            var sql = "SELECT dbo.leak_secret() AS leaked FROM dbo.users";
            Assert.Equal(Secret, (await ScalarAsync(setup, sql, ct))?.ToString());
            var before = await PhysicalCountAsync(setup, "dbo.users", "dbo.secrets", ct);
            await AssertMcpDeniesAsync(
                new MsSqlServerProvider(), SqlAgentToolType.MsSqlServer,
                target, "dbo.users", sql, "SELECT id FROM dbo.users", ct);
            Assert.Equal(before, await PhysicalCountAsync(setup, "dbo.users", "dbo.secrets", ct));
        }
        finally
        {
            await ExecuteAsync(admin,
                "ALTER DATABASE [" + database + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
                CancellationToken.None);
            await ExecuteAsync(admin, "DROP DATABASE [" + database + "]", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Oracle_QualifiedUdfReadsExcludedPhysicalTableButMcpRejects()
    {
        var connectionString = RequireConnection("TEST_ORACLE_CONNECTION", "Oracle 26ai");
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var users = "QUSR_" + suffix;
        var secrets = "QSEC_" + suffix;
        var function = "QFN_" + suffix;
        await using var setup = new OracleConnection(connectionString);
        await setup.OpenAsync(ct);
        var created = 0;
        try
        {
            await ExecuteAsync(setup, "CREATE TABLE " + users + " (ID NUMBER(10) PRIMARY KEY)", ct);
            created = 1;
            await ExecuteAsync(setup, "CREATE TABLE " + secrets
                + " (ID NUMBER(10) PRIMARY KEY, SECRET VARCHAR2(64))", ct);
            created = 2;
            await ExecuteAsync(setup, "INSERT INTO " + users + " VALUES (1)", ct);
            await ExecuteAsync(setup, "INSERT INTO " + secrets + " VALUES (1, '" + Secret + "')", ct);
            await ExecuteAsync(setup, "CREATE FUNCTION " + function + """
                 RETURN VARCHAR2 IS
                     secret_value VARCHAR2(64);
                 BEGIN
                     SELECT SECRET INTO secret_value FROM 
                """ + secrets + """
                 WHERE ID = 1;
                     RETURN secret_value;
                 END;
                """, ct);
            created = 3;

            var table = "SQLCORE." + users;
            var sql = "SELECT SQLCORE." + function + "() AS LEAKED FROM " + table;
            Assert.Equal(Secret, (await ScalarAsync(setup, sql, ct))?.ToString());
            var before = await PhysicalCountAsync(setup, users, secrets, ct);
            await AssertMcpDeniesAsync(
                new OracleProvider(), SqlAgentToolType.Oracle,
                connectionString, table, sql, "SELECT ID FROM " + table, ct);
            Assert.Equal(before, await PhysicalCountAsync(setup, users, secrets, ct));
        }
        finally
        {
            if (created >= 3)
                await ExecuteAsync(setup, "DROP FUNCTION " + function, CancellationToken.None);
            if (created >= 2)
                await ExecuteAsync(setup, "DROP TABLE " + secrets + " PURGE", CancellationToken.None);
            if (created >= 1)
                await ExecuteAsync(setup, "DROP TABLE " + users + " PURGE", CancellationToken.None);
        }
    }

    // #575: An unqualified modeled SQL built-in is only secure if the physical
    // Oracle resolver cannot redirect it to an application-owned function.
    [Fact]
    public async Task Oracle_UnqualifiedAbsBuiltinShadow_MustNotLeakExcludedRelation()
    {
        var connectionString = RequireConnection("TEST_ORACLE_CONNECTION", "Oracle 26ai");
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var users = "QUSR_" + suffix;
        var secrets = "QSEC_" + suffix;
        await using var setup = new OracleConnection(connectionString);
        await setup.OpenAsync(ct);
        var stage = 0;
        try
        {
            await ExecuteAsync(setup, "CREATE TABLE " + users + " (ID NUMBER(10) PRIMARY KEY)", ct);
            stage = 1;
            await ExecuteAsync(setup, "CREATE TABLE " + secrets
                + " (ID NUMBER(10) PRIMARY KEY, SECRET VARCHAR2(64))", ct);
            stage = 2;
            await ExecuteAsync(setup, "INSERT INTO " + users + " VALUES (1)", ct);
            await ExecuteAsync(setup, "INSERT INTO " + secrets + " VALUES (1, '" + Secret + "')", ct);

            // The shadow function has precisely the same name/signature as
            // the ordinary modeled ABS(NUMBER) built-in and internally reads
            // the excluded table, without any forbidden relation in Query SQL.
            await ExecuteAsync(setup, "CREATE FUNCTION ABS(P_ID NUMBER) RETURN VARCHAR2 IS "
                + "V_SECRET VARCHAR2(64); BEGIN SELECT SECRET INTO V_SECRET FROM "
                + secrets + " WHERE ID=P_ID; RETURN V_SECRET; END;", ct);
            stage = 3;

            var table = "SQLCORE." + users;
            var sql = "SELECT ABS(ID) AS LEAKED FROM " + table;
            // Independent positive control proves whether Oracle resolves
            // this particular name to a user-defined function, rather than
            // trusting a statement-shape-only theoretical counterexample.
            Assert.Equal(Secret, (await ScalarAsync(setup, sql, ct))?.ToString());

            var provider = new OracleProvider();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { table };
            var compiled = new TypedQueryRuntime().Compile(
                provider, sql, SqlAgentToolType.Oracle, Policy(), allowed);
            Assert.Contains("ABS(", compiled.Sql, StringComparison.OrdinalIgnoreCase);

            var before = await PhysicalCountAsync(setup, users, secrets, ct);
            var result = await Tool(provider, SqlAgentToolType.Oracle,
                    connectionString, table).ExecuteQuerySql(sql, ct);
            Assert.False(result.Success, "Canonical ABS cannot be trusted when Oracle resolves a schema UDF.");
            Assert.Empty(result.Rows);
            Assert.DoesNotContain(Secret, result.ToString());
            Assert.Equal(before, await PhysicalCountAsync(setup, users, secrets, ct));
        }
        finally
        {
            if (stage >= 3)
                await ExecuteAsync(setup, "DROP FUNCTION ABS", CancellationToken.None);
            if (stage >= 2)
                await ExecuteAsync(setup, "DROP TABLE " + secrets + " PURGE", CancellationToken.None);
            if (stage >= 1)
                await ExecuteAsync(setup, "DROP TABLE " + users + " PURGE", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Firebird_QuotedUdfReadsExcludedPhysicalTableButMcpRejects()
    {
        var connectionString = RequireConnection("TEST_FIREBIRD_CONNECTION", "Firebird 5");
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var users = "QUSR_" + suffix;
        var secrets = "QSEC_" + suffix;
        var function = "QFN_" + suffix;
        await using var setup = new FbConnection(connectionString);
        await setup.OpenAsync(ct);
        var created = 0;
        try
        {
            await ExecuteAsync(setup, "CREATE TABLE " + users + " (ID INTEGER PRIMARY KEY)", ct);
            created = 1;
            await ExecuteAsync(setup, "CREATE TABLE " + secrets
                + " (ID INTEGER PRIMARY KEY, SECRET VARCHAR(64))", ct);
            created = 2;
            await ExecuteAsync(setup, "INSERT INTO " + users + " VALUES (1)", ct);
            await ExecuteAsync(setup, "INSERT INTO " + secrets + " VALUES (1, '" + Secret + "')", ct);
            await ExecuteAsync(setup, "CREATE FUNCTION " + function + """
                 RETURNS VARCHAR(64) AS
                 DECLARE VARIABLE secret_value VARCHAR(64);
                 BEGIN
                   SELECT SECRET FROM 
                """ + secrets + """
                 WHERE ID = 1 INTO :secret_value;
                   RETURN secret_value;
                 END
                """, ct);
            created = 3;

            var sql = "SELECT \"" + function + "\"() AS LEAKED FROM " + users;
            Assert.Equal(Secret, (await ScalarAsync(setup, sql, ct))?.ToString());
            var before = await PhysicalCountAsync(setup, users, secrets, ct);
            await AssertMcpDeniesAsync(
                new FirebirdProvider(), SqlAgentToolType.Firebird,
                connectionString, users, sql, "SELECT ID FROM " + users, ct);
            Assert.Equal(before, await PhysicalCountAsync(setup, users, secrets, ct));
        }
        finally
        {
            if (created >= 3)
                await ExecuteAsync(setup, "DROP FUNCTION " + function, CancellationToken.None);
            if (created >= 2)
                await ExecuteAsync(setup, "DROP TABLE " + secrets, CancellationToken.None);
            if (created >= 1)
                await ExecuteAsync(setup, "DROP TABLE " + users, CancellationToken.None);
        }
    }

    private static async Task AssertMcpDeniesAsync(
        ISqlProvider provider, SqlAgentToolType dialect, string connectionString,
        string allowedTable, string unsafeSql, string safeSql, CancellationToken ct)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { allowedTable };
        var rejection = Assert.Throws<UnauthorizedAccessException>(() =>
            new TypedQueryRuntime().Compile(provider, unsafeSql, dialect, Policy(), tables));
        var diagnostic = Assert.IsType<SqlDiagnostic>(
            rejection.Data["HsSqlAgent.SqlCore.Diagnostic"]);
        Assert.Equal("SQL_POLICY_NATIVE_QUERY_FUNCTION_NOT_PROVEN", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Policy, diagnostic.Stage);

        var tool = Tool(provider, dialect, connectionString, allowedTable);
        var denied = await tool.ExecuteQuerySql(unsafeSql, ct);
        Assert.False(denied.Success, "Opaque database UDF escaped the Query table whitelist.");
        Assert.Empty(denied.Rows);
        Assert.DoesNotContain(Secret, denied.ToString());

        var allowed = await tool.ExecuteQuerySql(safeSql, ct);
        Assert.True(allowed.Success, allowed.Error?.Message);
        Assert.Single(allowed.Rows);
    }

    private static SecurityPolicyModel Policy() => new()
    {
        QueryMaxRows = 20,
        QueryTimeoutSeconds = 10
    };

    private static SqlAgentTool Tool(
        ISqlProvider provider, SqlAgentToolType dialect,
        string connectionString, string allowedTable)
    {
        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.SqlProvider] = dialect.ToString();
        context.Items[McpContextItemKeys.SqlConnectionString] = connectionString;
        context.Items[McpContextItemKeys.AllowedTools] = "execute_query_sql";
        context.Items[McpContextItemKeys.TableWhitelist] = allowedTable;
        context.Items[McpContextItemKeys.AccessKeyId] = 7;
        context.Items[McpContextItemKeys.DbManagementId] = 11;
        context.Items[McpContextItemKeys.DatabaseName] = "native-function-live-test";

        var http = new Mock<IHttpContextAccessor>();
        http.SetupGet(x => x.HttpContext).Returns(context);
        var providers = new Mock<ISqlProviderFactory>(MockBehavior.Strict);
        providers.Setup(x => x.GetProvider(dialect)).Returns(provider);
        var audit = new Mock<IAuditService>();
        audit.Setup(x => x.WriteEventAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<AuditEventContext>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var security = new Mock<ISecurityPolicyRuntimeState>();
        security.Setup(x => x.GetCurrent()).Returns(Policy());
        var limiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        limiter.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());

        return new SqlAgentTool(http.Object, providers.Object, audit.Object,
            Mock.Of<IDbSemanticService>(), security.Object, limiter.Object,
            typedQueryRuntime: new TypedQueryRuntime());
    }

    private static string RequireConnection(string variable, string provider)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(provider
                + " native-function live proof requires " + variable + ".");
        Assert.Skip("Requires real " + provider + ".");
        throw new InvalidOperationException("Unreachable");
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct);
    }

    private static async Task ExecuteAsync(
        DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(int Users, int Secrets)> PhysicalCountAsync(
        DbConnection connection, string users, string secrets, CancellationToken ct)
    {
        var userRows = Convert.ToInt32(await ScalarAsync(
            connection, "SELECT COUNT(*) FROM " + users, ct));
        var secretRows = Convert.ToInt32(await ScalarAsync(
            connection, "SELECT COUNT(*) FROM " + secrets, ct));
        return (userRows, secretRows);
    }
}
