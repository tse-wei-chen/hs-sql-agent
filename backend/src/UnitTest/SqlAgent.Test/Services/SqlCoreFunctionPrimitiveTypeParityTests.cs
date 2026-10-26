using System;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Named-parameter Query AST and Function-only positional AST must agree on
/// primitive type rules. These are syntactic observations, never execution proof.
/// </summary>
public sealed class SqlCoreFunctionPrimitiveTypeParityTests
{
    private static SqlScriptAnalysisResult Analyze(string declaration, string result, string expression) =>
        SqlCoreFacade.AnalyzeScript(
            $"CREATE FUNCTION app.parity({declaration}) RETURNS {result} LANGUAGE SQL AS $fn$ SELECT {expression}; $fn$;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("n integer", "integer", "n + 2", "$1 + 2", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("n integer", "integer", "n * 2 - 3", "$1 * 2 - 3", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("n bigint", "bigint", "n + 2", "$1 + 2", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("n integer", "bigint", "n + 2147483648", "$1 + 2147483648", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("n integer, m bigint", "bigint", "n + m", "$1 + $2", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("n integer, m bigint", "bigint", "m - n", "$2 - $1", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("n integer, m integer", "integer", "n * m", "$1 * $2", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("active boolean", "boolean", "NOT active", "NOT $1", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a boolean, b boolean", "boolean", "a AND b", "$1 AND $2", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a boolean, b boolean", "boolean", "a OR b", "$1 OR $2", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("active boolean", "boolean", "NOT active OR FALSE", "NOT $1 OR FALSE", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("n integer", "integer", "CAST(n AS INTEGER)", "CAST($1 AS INTEGER)", SqlFunctionReturnEvidence.ExplicitCastMatch)]
    [InlineData("n integer", "bigint", "CAST(n AS BIGINT)", "CAST($1 AS BIGINT)", SqlFunctionReturnEvidence.ExplicitCastMatch)]
    [InlineData("n bigint", "bigint", "CAST(n AS BIGINT)", "CAST($1 AS BIGINT)", SqlFunctionReturnEvidence.ExplicitCastMatch)]
    [InlineData("ok boolean", "boolean", "CAST(ok AS BOOLEAN)", "CAST($1 AS BOOLEAN)", SqlFunctionReturnEvidence.ExplicitCastMatch)]
    [InlineData("message text", "text", "CAST(message AS TEXT)", "CAST($1 AS TEXT)", SqlFunctionReturnEvidence.ExplicitCastMatch)]
    public void CompatiblePrimitiveTypeEvidenceAgreesAcrossBothAstPaths(
        string declaration, string result, string named, string positional,
        SqlFunctionReturnEvidence evidence)
    {
        foreach (var expression in new[] { named, positional })
        {
            var report = Analyze(declaration, result, expression);
            var statement = Assert.Single(report.Statements);
            var function = Assert.Single(report.ProceduralDefinitions);
            Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
            Assert.NotEqual(SqlFunctionReturnType.Void, function.FunctionReturn!.DeclaredType);
            Assert.Equal(evidence, function.FunctionReturn.Evidence);
            Assert.False(function.FunctionReturn.IsRuntimeReturnProven);
            Assert.Equal(SqlCatalogKnowledge.Unknown, function.FunctionReturn.RuntimeCertainty);
            Assert.True(function.PotentialBodyEffects.HasFlag(SqlEffectKind.UnknownEffects));
            Assert.False(report.FinalCatalog.IsClosedWorld);
            Assert.Empty(report.Diagnostics);
        }
    }

    [Theory]
    [InlineData("n bigint", "integer", "n + 1", "$1 + 1")]
    [InlineData("n integer", "integer", "n + 2147483648", "$1 + 2147483648")]
    [InlineData("n integer", "boolean", "n + 1", "$1 + 1")]
    [InlineData("n boolean", "boolean", "n AND 1", "$1 AND 1")]
    [InlineData("n integer", "boolean", "NOT n", "NOT $1")]
    [InlineData("n bigint", "integer", "CAST(n AS INTEGER)", "CAST($1 AS INTEGER)")]
    [InlineData("n integer", "boolean", "CAST(n AS BOOLEAN)", "CAST($1 AS BOOLEAN)")]
    [InlineData("n text", "integer", "CAST(n AS INTEGER)", "CAST($1 AS INTEGER)")]
    [InlineData("n integer", "integer", "n / 2", "$1 / 2")]
    [InlineData("n integer", "integer", "n + unknown", "$1 + unknown")]
    public void InvalidTypeEvidenceCannotProducePartialFunctionDefinition(
        string declaration, string result, string named, string positional)
    {
        foreach (var expression in new[] { named, positional })
        {
            var report = Analyze(declaration, result, expression);
            Assert.Empty(report.ProceduralDefinitions);
            Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(report.Statements).Status);
            Assert.Contains(report.Diagnostics, d =>
                d.Code == "SQL_PROCEDURAL_RETURN_UNPROVEN" || d.Code == "SQL_PROCEDURAL_RETURN_TYPE_MISMATCH" || d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED");
            Assert.False(report.FinalCatalog.IsClosedWorld);
        }
    }

    [Fact]
    public void NamedAndOrdinalDirectReferencesKeepDistinctEvidenceTags()
    {
        Assert.Equal(SqlFunctionReturnEvidence.ParameterSymbolMatch,
            Assert.Single(Analyze("n integer", "integer", "n").ProceduralDefinitions).FunctionReturn!.Evidence);
        Assert.Equal(SqlFunctionReturnEvidence.PositionalParameterMatch,
            Assert.Single(Analyze("n integer", "integer", "$1").ProceduralDefinitions).FunctionReturn!.Evidence);
    }
}
