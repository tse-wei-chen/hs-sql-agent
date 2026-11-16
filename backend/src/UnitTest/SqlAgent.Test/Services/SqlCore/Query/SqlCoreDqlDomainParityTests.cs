using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HsSqlAgent.SqlCore.Models;
using Microsoft.Data.Sqlite;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// U8/Q01-Q07: the Query domain must retain physical read-set, family,
/// source-capability and function policy fences across all six dialects.
/// Script analysis is evidence only, not a shortcut to a Query command.
/// </summary>
public sealed class SqlCoreDqlDomainParityTests
{
    private static readonly IReadOnlySet<string> OnlyOrders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    private static SqlPlanValidationContext AllowOrders() =>
        new("u8-dql-readset-v1", OnlyOrders);

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void QueryDomain_SingleCompileAndScriptKeepReadOnlyFamily(SqlAgentToolType provider)
    {
        const string sql = "SELECT id FROM orders WHERE id = 1";
        var command = SqlCoreFacade.CompileQueryWithFacts(
            sql, provider, provider, AllowOrders(), new SqlExecutionPlanPolicy(10));
        Assert.Equal(SqlStatementKind.Query, command.Command.Kind);
        Assert.Equal(new[] { "orders" }, command.Facts.ReferencedTables.OrderBy(x => x));

        var script = SqlCoreFacade.ParseScript(sql + ";", provider);
        var statement = Assert.Single(script.Statements);
        Assert.Equal(SqlScriptStatementKind.Query, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void QueryDomain_CteReadSetDoesNotGrantAliasPhysicalTableAuthority(SqlAgentToolType provider)
    {
        const string sql = "WITH scoped AS (SELECT id FROM orders) SELECT id FROM scoped";
        var command = SqlCoreFacade.CompileQueryWithFacts(
            sql, provider, provider, AllowOrders(), new SqlExecutionPlanPolicy(10));
        Assert.Equal(SqlStatementKind.Query, command.Command.Kind);
        Assert.Contains("orders", command.Facts.ReferencedTables);
        Assert.DoesNotContain("scoped", command.Facts.ReferencedTables);
        Assert.True(command.Facts.ContainsCte);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void QueryDomain_CannotAcquireMutationAuthority(SqlAgentToolType provider)
    {
        Assert.ThrowsAny<Exception>(() => SqlCoreFacade.CompileQuery(
            "UPDATE orders SET id = 3 WHERE id = 1",
            provider, provider, AllowOrders(), new SqlExecutionPlanPolicy(10)));
    }

    [Theory]
    [InlineData("ROW_NUMBER() OVER (ORDER BY id)")]
    [InlineData("RANK() OVER (ORDER BY id)")]
    [InlineData("DENSE_RANK() OVER (ORDER BY id)")]
    public void PostgresWhitelist_AllowsProvenPgCatalogWindowFunctions(string window)
    {
        var query = $"SELECT {window} AS rn FROM orders";
        var result = SqlCoreFacade.CompileQueryWithFacts(
            query, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            AllowOrders(), new SqlExecutionPlanPolicy(10));
        Assert.Equal(SqlStatementKind.Query, result.Command.Kind);
        Assert.Contains("pg_catalog.", result.Command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orders", result.Facts.ReferencedTables);
    }

    [Theory]
    [InlineData("SELECT pg_temp.dangerous_effect() AS v FROM orders")]
    [InlineData("WITH scoped AS (SELECT pg_temp.dangerous_effect() AS v FROM orders) SELECT v FROM scoped")]
    public void PostgresWhitelist_StillRejectsUnprovenNativeFunctions(string sql)
    {
        var failure = Assert.Throws<UnauthorizedAccessException>(() =>
            SqlCoreFacade.CompileQuery(sql,
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                AllowOrders(), new SqlExecutionPlanPolicy(10)));
        var diagnostic = Assert.IsType<SqlDiagnostic>(
            failure.Data["HsSqlAgent.SqlCore.Diagnostic"]);
        Assert.Equal("SQL_POLICY_POSTGRES_QUERY_FUNCTION_NOT_PROVEN", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Policy, diagnostic.Stage);
    }

    [Fact]
    public void NestedReadSet_DisallowsUndeclaredPhysicalRelation()
    {
        const string sql = "WITH scoped AS (SELECT id FROM secret_data) SELECT id FROM scoped";
        Assert.Throws<UnauthorizedAccessException>(() =>
            SqlCoreFacade.CompileQuery(sql,
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                AllowOrders(), new SqlExecutionPlanPolicy(10)));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void QueryDomain_DerivedAndWindowReadSetIncludesOnlyPhysicalSource(SqlAgentToolType provider)
    {
        const string sql = "SELECT d.id, ROW_NUMBER() OVER (ORDER BY d.id) AS rn FROM (SELECT id FROM orders) d";
        var query = SqlCoreFacade.CompileQueryWithFacts(
            sql, provider, provider, AllowOrders(), new SqlExecutionPlanPolicy(10));
        Assert.Equal(SqlStatementKind.Query, query.Command.Kind);
        Assert.True(query.Facts.ContainsSubquery);
        Assert.Equal(new[] { "orders" }, query.Facts.ReferencedTables.OrderBy(x => x));
    }

    [Fact]
    public void MySqlOrdinaryCte_RequiresProvenSourceAndTargetVersions()
    {
        const string sql = "WITH scoped AS (SELECT id FROM orders) SELECT id FROM scoped";
        var legacy = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(5, 7, 0));
        var modern = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(8, 4, 0));

        var sourceError = Assert.ThrowsAny<Exception>(() => SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            AllowOrders(), new SqlExecutionPlanPolicy(10), legacy, modern));
        Assert.Contains("select.cte", sourceError.Message, StringComparison.OrdinalIgnoreCase);

        var targetError = Assert.ThrowsAny<Exception>(() => SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            AllowOrders(), new SqlExecutionPlanPolicy(10), modern, legacy));
        Assert.Contains("select.cte", targetError.Message, StringComparison.OrdinalIgnoreCase);

        var supported = SqlCoreFacade.CompileQuery(
            sql, SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            AllowOrders(), new SqlExecutionPlanPolicy(10), modern, modern);
        Assert.Equal(SqlStatementKind.Query, supported.Kind);
    }

    [Fact]
    public async Task SqlitePhysicalDifferential_CteWindowAndRowCapMatchNativeQuery()
    {
        const string sql =
            "WITH scoped AS (SELECT id FROM orders) " +
            "SELECT id, ROW_NUMBER() OVER (ORDER BY id) AS rn FROM scoped ORDER BY id";
        await using var db = new SqliteConnection("Data Source=:memory:");
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using (var setup = db.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE orders(id INTEGER PRIMARY KEY);" +
                "INSERT INTO orders(id) VALUES (3),(1),(2);";
            await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        async Task<List<(long Id, long Rank)>> Execute(string nativeSql,
            IEnumerable<SqlParameterValue>? parameters = null)
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandText = nativeSql;
            if (parameters is not null)
            {
                foreach (var bound in parameters)
                {
                    var parameter = cmd.CreateParameter();
                    parameter.ParameterName = bound.Name;
                    parameter.Value = bound.Value ?? DBNull.Value;
                    cmd.Parameters.Add(parameter);
                }
            }
            var results = new List<(long, long)>();
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                results.Add((reader.GetInt64(0), reader.GetInt64(1)));
            return results;
        }

        var native = await Execute(sql);
        var compiled = SqlCoreFacade.CompileQueryWithFacts(
            sql, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            AllowOrders(), new SqlExecutionPlanPolicy(10));
        var actual = await Execute(compiled.Command.Sql, compiled.Command.Parameters);
        Assert.Equal(native, actual);
        Assert.Equal(new[] { (1L, 1L), (2L, 2L), (3L, 3L) }, actual);
        Assert.Equal(new[] { "orders" }, compiled.Facts.ReferencedTables.OrderBy(x => x));
    }

}
