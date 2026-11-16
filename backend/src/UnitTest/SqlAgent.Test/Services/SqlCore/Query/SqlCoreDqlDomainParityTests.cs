using System;
using System.Collections.Generic;
using System.Linq;
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
}
