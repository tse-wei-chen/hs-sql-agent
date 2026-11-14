using System;
using System.Collections.Generic;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Oracle evaluates unparenthesized compound SELECTs left-to-right; Core's
/// canonical set grammar gives INTERSECT precedence. Admit only equivalent
/// left prefixes or explicitly grouped right subqueries, and render grouping
/// on both Oracle and PostgreSQL targets without SQL-string rewriting.
/// </summary>
public sealed class SqlCoreOracleSetGroupingTests
{
    private static CompiledSqlCommand Compile(string sql, SqlAgentToolType target) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Oracle,
                new SqlProviderCapabilityProfile(SqlAgentToolType.Oracle, new Version(21, 0))),
            target,
            new SqlPlanValidationContext("oracle-set-grouping-v1"),
            new SqlExecutionPlanPolicy());

    [Theory]
    [InlineData("SELECT 1 INTERSECT SELECT 2 MINUS SELECT 3")]
    [InlineData("SELECT 1 INTERSECT SELECT 2 UNION SELECT 3")]
    [InlineData("SELECT 1 INTERSECT SELECT 2 EXCEPT SELECT 3")]
    [InlineData("SELECT 1 INTERSECT SELECT 2 MINUS SELECT 3 UNION SELECT 4")]
    [InlineData("SELECT 1 INTERSECT SELECT 2 INTERSECT SELECT 3 MINUS SELECT 4")]
    [InlineData("SELECT 1 INTERSECT SELECT 2 UNION ALL SELECT 3")]
    [InlineData("SELECT 1 INTERSECT SELECT 2 MINUS SELECT 3 ORDER BY 1")]
    [InlineData("SELECT id FROM a INTERSECT SELECT id FROM b MINUS SELECT id FROM c")]
    [InlineData("WITH q AS (SELECT id FROM a) SELECT id FROM q INTERSECT SELECT id FROM b MINUS SELECT id FROM c")]
    public void LeftToRightOraclePrefixAgreesWithPortableCanonicalPrecedence(string sql)
    {
        var oracle = Compile(sql, SqlAgentToolType.Oracle);
        var postgres = Compile(sql, SqlAgentToolType.Postgres);
        Assert.Contains(" INTERSECT ", oracle.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" INTERSECT ", postgres.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" MINUS ", postgres.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT 1 MINUS (SELECT 2 INTERSECT SELECT 3)", " MINUS (", " EXCEPT (")]
    [InlineData("SELECT 1 UNION (SELECT 2 INTERSECT SELECT 3)", " UNION (", " UNION (")]
    [InlineData("SELECT 1 INTERSECT (SELECT 2 MINUS SELECT 3)", " INTERSECT (", " INTERSECT (")]
    [InlineData("SELECT 1 MINUS (SELECT 2 UNION SELECT 3)", " MINUS (", " EXCEPT (")]
    [InlineData("SELECT 1 INTERSECT SELECT 2 MINUS (SELECT 3 INTERSECT SELECT 4)", " MINUS (", " EXCEPT (")]
    [InlineData("SELECT id FROM a MINUS (SELECT id FROM b INTERSECT SELECT id FROM c)", " MINUS (", " EXCEPT (")]
    public void ExplicitGroupedSetRhsStaysGroupedOnOracleAndPostgres(
        string sql, string oracleFragment, string postgresFragment)
    {
        var oracle = Compile(sql, SqlAgentToolType.Oracle);
        var postgres = Compile(sql, SqlAgentToolType.Postgres);
        Assert.Contains(oracleFragment, oracle.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(postgresFragment, postgres.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" MINUS ", postgres.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT 1 MINUS SELECT 2 INTERSECT SELECT 3")]
    [InlineData("SELECT 1 UNION SELECT 2 INTERSECT SELECT 3")]
    [InlineData("SELECT 1 EXCEPT SELECT 2 INTERSECT SELECT 3")]
    [InlineData("SELECT 1 MINUS SELECT 2 INTERSECT SELECT 3 MINUS SELECT 4")]
    [InlineData("SELECT 1 MINUS (SELECT 2) INTERSECT SELECT 3")]
    [InlineData("SELECT 1 MINUS (SELECT 2 INTERSECT SELECT 3) INTERSECT SELECT 4")]
    [InlineData("SELECT 1 UNION ALL SELECT 2 INTERSECT SELECT 3")]
    [InlineData("SELECT 1 MINUS ALL SELECT 2")]
    [InlineData("SELECT 1 MINUS DISTINCT SELECT 2")]
    [InlineData("SELECT 1 INTERSECT ALL SELECT 2 MINUS SELECT 3")]
    public void UngroupedMixedPrecedenceAndUnprovenAllRemainFailClosed(string sql)
    {
        Assert.ThrowsAny<Exception>(() => Compile(sql, SqlAgentToolType.Oracle));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void NonOracleSourceMinusStillRejected(SqlAgentToolType provider)
    {
        Assert.ThrowsAny<Exception>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT 1 INTERSECT SELECT 2 MINUS SELECT 3", provider));
    }

    [Fact]
    public void GroupedSetExpressionsNeverEraseOracleAllRejection()
    {
        Assert.ThrowsAny<Exception>(() =>
            Compile("SELECT 1 MINUS (SELECT 2 MINUS ALL SELECT 3)",
                SqlAgentToolType.Oracle));
    }

    [Fact]
    public void ParenthesisGroupingIsNotAStringRewrite()
    {
        // A quoted identifier matching the operator spelling stays an
        // identifier and does not trigger contextual set parsing.
        var result = Compile(
            "SELECT id AS \"MINUS\" FROM orders MINUS (SELECT id FROM refunds)",
            SqlAgentToolType.Oracle);
        Assert.Contains(" MINUS ", result.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
