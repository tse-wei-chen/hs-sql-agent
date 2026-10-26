using System;
using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Oracle legacy MINUS means duplicate-eliminating EXCEPT. The lexer must
/// not reserve the word for other providers, and source precedence must
/// never silently change when combined with INTERSECT.
/// </summary>
public sealed class SqlCoreOracleMinusSetGrammarTests
{
    private static CompiledSqlCommand Compile(string sql,
        SqlAgentToolType source, SqlAgentToolType target) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, source), target,
            new SqlPlanValidationContext("oracle-minus-capability-v1"),
            new SqlExecutionPlanPolicy());

    [Theory]
    [InlineData("SELECT id FROM alpha MINUS SELECT id FROM beta")]
    [InlineData("SELECT id FROM alpha minus SELECT id FROM beta")]
    [InlineData("SELECT 1 MINUS SELECT 2")]
    [InlineData("SELECT 1 MINUS SELECT 2 MINUS SELECT 3")]
    [InlineData("SELECT 1 UNION SELECT 2 MINUS SELECT 3")]
    [InlineData("SELECT 1 MINUS SELECT 2 UNION SELECT 3")]
    [InlineData("SELECT 1 MINUS SELECT 2 ORDER BY 1")]
    [InlineData("SELECT 1 MINUS SELECT 2 FETCH FIRST 5 ROWS ONLY")]
    [InlineData("WITH q AS (SELECT id FROM alpha) SELECT id FROM q MINUS SELECT id FROM beta")]
    [InlineData("SELECT id FROM alpha a MINUS SELECT id FROM beta b")]
    [InlineData("SELECT id FROM (SELECT id FROM alpha MINUS SELECT id FROM beta) d")]
    [InlineData("SELECT id FROM alpha MINUS SELECT id FROM beta WHERE id > 10")]
    public void OracleMinusIsClosedSetOperationAndRendersNativeMinus(string sql)
    {
        var command = Compile(sql, SqlAgentToolType.Oracle, SqlAgentToolType.Oracle);
        Assert.Contains(" MINUS ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" EXCEPT ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT 1 MINUS SELECT 2")]
    [InlineData("SELECT id FROM alpha MINUS SELECT id FROM beta")]
    [InlineData("SELECT id FROM alpha MINUS SELECT id FROM beta ORDER BY 1")]
    [InlineData("SELECT 1 UNION SELECT 2 MINUS SELECT 3")]
    public void OracleMinusLowersToPortableExceptForPostgres(string sql)
    {
        var command = Compile(sql, SqlAgentToolType.Oracle, SqlAgentToolType.Postgres);
        Assert.Contains(" EXCEPT ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" MINUS ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT 1 EXCEPT SELECT 2")]
    [InlineData("SELECT id FROM alpha EXCEPT SELECT id FROM beta")]
    public void ExistingPostgresExceptRendersUsingNativeOracleMinus(string sql)
    {
        var command = Compile(sql, SqlAgentToolType.Postgres, SqlAgentToolType.Oracle);
        Assert.Contains(" MINUS ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" EXCEPT ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT 1 MINUS ALL SELECT 2")]
    [InlineData("SELECT 1 MINUS DISTINCT SELECT 2")]
    [InlineData("SELECT 1 MINUS")]
    [InlineData("SELECT 1 MINUS SELECT")]
    [InlineData("SELECT 1 MINUS SELECT 2 INTERSECT SELECT 3")]
    [InlineData("SELECT 1 INTERSECT SELECT 2 MINUS SELECT 3")]
    [InlineData("SELECT 1 UNION SELECT 2 INTERSECT SELECT 3")]
    [InlineData("SELECT 1 MINUS SELECT 2 INTERSECT ALL SELECT 3")]
    public void UnsupportedOrPrecedenceAmbiguousOracleSetFormsFailClosed(string sql)
    {
        Assert.ThrowsAny<Exception>(() =>
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Oracle));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void NonOracleSourcesNeverGainMinusOperator(SqlAgentToolType provider)
    {
        Assert.ThrowsAny<Exception>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT 1 MINUS SELECT 2", provider));
    }

    [Fact]
    public void QuotedMinusMustNotBecomeSetOperator()
    {
        Assert.ThrowsAny<Exception>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT 1 \"MINUS\" SELECT 2", SqlAgentToolType.Oracle));
    }

    [Fact]
    public void OracleAliasNamedMinusIsStillAllowedWhenExplicitlyQuoted()
    {
        var command = Compile(
            "SELECT id AS \"MINUS\" FROM alpha",
            SqlAgentToolType.Oracle, SqlAgentToolType.Oracle);
        Assert.DoesNotContain(" MINUS SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectsDoNotLoseExistingUnquotedMinusColumnNames(SqlAgentToolType provider)
    {
        var compiled = Compile("SELECT minus FROM alpha",
            provider, provider);
        Assert.NotEmpty(compiled.Sql);
    }
}
