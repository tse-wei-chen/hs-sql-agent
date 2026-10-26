using System;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Narrow PostgreSQL SQL-function AST return-shape evidence.
/// An inferred primitive expression type is not execution, a server binding,
/// a successful conversion, or a permission proof.
/// </summary>
public sealed class SqlCoreFunctionPrimitiveExpressionTests
{
    private static SqlScriptAnalysisResult Analyze(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("n integer", "integer", "n + 1")]
    [InlineData("n integer", "integer", "n * 2 - 3")]
    [InlineData("n integer", "integer", "-n + 1")]
    [InlineData("n bigint", "bigint", "n + 2")]
    [InlineData("n bigint", "bigint", "n - 2147483648")]
    [InlineData("n integer", "bigint", "n + 2147483648")]
    [InlineData("value integer", "integer", "1 + 2")]
    [InlineData("enabled boolean", "boolean", "NOT enabled")]
    [InlineData("enabled boolean", "boolean", "enabled AND TRUE")]
    [InlineData("enabled boolean", "boolean", "NOT enabled OR FALSE")]
    public void BoundedPrimitiveExpressionsCarryStaticTypeEvidence(
        string parameters, string resultType, string expression)
    {
        var sql = $"CREATE FUNCTION app.computed({parameters}) RETURNS {resultType} " +
            $"LANGUAGE SQL AS $fn$ SELECT {expression}; $fn$;";
        var result = Analyze(sql);
        var statement = Assert.Single(result.Statements);
        var definition = Assert.Single(result.ProceduralDefinitions);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlFunctionReturnEvidence.PrimitiveExpressionMatch, definition.FunctionReturn!.Evidence);
        Assert.False(definition.FunctionReturn.IsRuntimeReturnProven);
        Assert.Equal(SqlCatalogKnowledge.Unknown, definition.FunctionReturn.RuntimeCertainty);
        Assert.True(definition.PotentialBodyEffects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("n integer", "integer", "CAST(n AS INTEGER)")]
    [InlineData("n integer", "bigint", "CAST(n AS BIGINT)")]
    [InlineData("n bigint", "bigint", "CAST(n AS BIGINT)")]
    [InlineData("enabled boolean", "boolean", "CAST(enabled AS BOOLEAN)")]
    [InlineData("message text", "text", "CAST(message AS TEXT)")]
    public void ExplicitIdentityOrIntegerWideningCastOfBoundParameterCarriesEvidence(
        string parameters, string resultType, string expression)
    {
        var result = Analyze($"CREATE FUNCTION app.cast_param({parameters}) RETURNS {resultType} " +
            $"LANGUAGE SQL AS $fn$ SELECT {expression}; $fn$;");
        var definition = Assert.Single(result.ProceduralDefinitions);
        Assert.Equal(SqlFunctionReturnEvidence.ExplicitCastMatch, definition.FunctionReturn!.Evidence);
        Assert.False(definition.FunctionReturn.IsRuntimeReturnProven);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("n bigint", "integer", "n + 1")]
    [InlineData("n integer", "integer", "n + 2147483648")]
    [InlineData("n integer", "boolean", "n + 1")]
    [InlineData("n integer", "integer", "n / 2")]
    [InlineData("n integer", "integer", "n = 1")]
    [InlineData("enabled boolean", "boolean", "enabled AND 1")]
    [InlineData("enabled boolean", "boolean", "enabled OR unknown_name")]
    [InlineData("n integer", "integer", "n + 1 FROM app.orders")]
    [InlineData("n integer", "integer", "n + 1 WHERE TRUE")]
    [InlineData("n integer", "integer", "ABS(n)")]
    [InlineData("n integer", "integer", "CAST(n AS BOOLEAN)")]
    [InlineData("n bigint", "integer", "CAST(n AS INTEGER)")]
    [InlineData("message text", "integer", "CAST(message AS INTEGER)")]
    public void UnprovenExpressionsNeverProduceFunctionDefinitions(
        string parameters, string resultType, string expression)
    {
        var result = Analyze($"CREATE FUNCTION app.unproven({parameters}) RETURNS {resultType} " +
            $"LANGUAGE SQL AS $fn$ SELECT {expression}; $fn$;");
        Assert.Empty(result.ProceduralDefinitions);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(result.Statements).Status);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "SQL_PROCEDURAL_RETURN_UNPROVEN" || d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED");
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void SourceDialectCannotBorrowPostgreSqlFunctionExpressions()
    {
        Assert.Throws<HsSqlAgent.SqlCore.SqlParsing.SqlParseException>(() =>
            SqlCoreFacade.AnalyzeScript(
                "CREATE FUNCTION app.f(n integer) RETURNS integer LANGUAGE SQL AS $b$ SELECT n + 1; $b$;",
                SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty()));
    }
}
