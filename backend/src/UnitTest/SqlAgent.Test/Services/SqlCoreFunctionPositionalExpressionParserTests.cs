using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreFunctionPositionalExpressionParserTests
{
    private static SqlScriptAnalysisResult Analyze(string args, string type, string body) =>
        SqlCoreFacade.AnalyzeScript(
            "CREATE FUNCTION app.calc(" + args + ") RETURNS " + type + " LANGUAGE SQL AS $fn$" + body + "$fn$;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("a integer", "integer", "SELECT ($1);", SqlFunctionReturnEvidence.PositionalParameterMatch)]
    [InlineData("a integer", "integer", "SELECT (($1));", SqlFunctionReturnEvidence.PositionalParameterMatch)]
    [InlineData("a integer, b integer", "integer", "SELECT ($1 + $2) * $1;", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a integer, b bigint", "bigint", "SELECT $1 + ($2 * 3);", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a integer, b integer", "integer", "SELECT ($1 + b) * (a - $2);", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a boolean, b boolean", "boolean", "SELECT NOT ($1 OR b) AND $2;", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a integer, b integer", "integer", "SELECT +$1 - -$2;", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a integer", "integer", "SELECT CAST($1 AS INTEGER);", SqlFunctionReturnEvidence.ExplicitCastMatch)]
    [InlineData("a integer", "bigint", "SELECT CAST($1 AS BIGINT);", SqlFunctionReturnEvidence.ExplicitCastMatch)]
    [InlineData("a bigint", "bigint", "SELECT CAST(($1) AS INT8);", SqlFunctionReturnEvidence.ExplicitCastMatch)]
    [InlineData("a boolean", "boolean", "SELECT CAST($1 AS BOOLEAN);", SqlFunctionReturnEvidence.ExplicitCastMatch)]
    [InlineData("a text", "text", "SELECT CAST($1 AS TEXT);", SqlFunctionReturnEvidence.ExplicitCastMatch)]
    [InlineData("a integer, b integer", "bigint", "SELECT CAST($1 AS BIGINT) + $2;", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a integer, b integer", "integer", "SELECT ($1 + 42);", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a integer, b integer", "bigint", "SELECT ($1 + 2147483648);", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    public void SupportedSyntaxIsOnlyStaticTypeEvidence(string args, string type, string body, SqlFunctionReturnEvidence evidence)
    {
        var result = Analyze(args, type, body);
        var statement = Assert.Single(result.Statements);
        var definition = Assert.Single(result.ProceduralDefinitions);
        var nested = Assert.Single(definition.BodyStatements);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlScriptStatementKind.Query, nested.Kind);
        Assert.Empty(nested.ReferencedTables);
        Assert.Equal(evidence, definition.FunctionReturn!.Evidence);
        Assert.False(definition.FunctionReturn.IsRuntimeReturnProven);
        Assert.Equal(SqlCatalogKnowledge.Unknown, definition.FunctionReturn.RuntimeCertainty);
        Assert.True(nested.PotentialEffects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("a integer", "integer", "SELECT CAST($1 AS BOOLEAN);")]
    [InlineData("a bigint", "integer", "SELECT CAST($1 AS INTEGER);")]
    [InlineData("a text", "integer", "SELECT CAST($1 AS INTEGER);")]
    [InlineData("a integer", "integer", "SELECT CAST($1 + 2 AS INTEGER);")]
    [InlineData("a integer", "integer", "SELECT CAST($1 AS numeric);")]
    [InlineData("a integer", "integer", "SELECT $1 / 2;")]
    [InlineData("a integer", "integer", "SELECT $1 + missing;")]
    [InlineData("a integer", "integer", "SELECT ($1 + 1;")]
    [InlineData("a integer", "integer", "SELECT ($1) SELECT 42;")]
    [InlineData("a integer", "integer", "SELECT $1 AS alias;")]
    [InlineData("a integer", "integer", "SELECT $1 FROM app.table_name;")]
    [InlineData("a integer", "integer", "SELECT $1 WHERE TRUE;")]
    [InlineData("a integer", "integer", "SELECT $1; SELECT $1;")]
    [InlineData("a integer", "integer", "SELECT $1; DELETE FROM app.orders;")]
    [InlineData("a integer", "integer", "SELECT $0;")]
    [InlineData("a integer", "integer", "SELECT $01;")]
    [InlineData("a integer", "integer", "SELECT $2;")]
    [InlineData("a integer", "integer", "SELECT $9999999999999;")]
    [InlineData("a integer", "integer", "SELECT $1 -- comment")]
    [InlineData("a integer", "integer", "SELECT $1 /* comment */;")]
    [InlineData("a integer", "integer", "SELECT '$1';")]
    [InlineData("a integer", "integer", "SELECT $$1;")]
    [InlineData("a integer", "integer", "SELECT $1 + NULL;")]
    [InlineData("a integer", "integer", "SELECT $1 + foo($1);")]
    [InlineData("a integer", "integer", "SELECT $1 + 9223372036854775808;")]
    [InlineData("a integer", "integer", "SELECT$1;")]
    [InlineData("a integer", "integer", "SELECT $1::INTEGER;")]
    [InlineData("a integer", "integer", "SELECT $1 > 0;")]
    public void UnmodeledSyntaxCannotProduceDefinition(string args, string type, string body)
    {
        var result = Analyze(args, type, body);
        Assert.Empty(result.ProceduralDefinitions);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(result.Statements).Status);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED" || d.Code == "SQL_PROCEDURAL_RETURN_UNPROVEN" || d.Code == "SQL_PROCEDURAL_RETURN_TYPE_MISMATCH");
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void DeepAndWideExpressionsRemainFailClosed()
    {
        var deep = "SELECT " + new string('(', 18) + "$1" + new string(')', 18) + ";";
        var wide = "SELECT $1" + string.Concat(Enumerable.Repeat(" + $1", 65)) + ";";
        foreach (var body in new[] { deep, wide })
        {
            var result = Analyze("a integer", "integer", body);
            Assert.Empty(result.ProceduralDefinitions);
            Assert.Contains(result.Diagnostics, d => d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED");
        }
    }

    [Fact]
    public void OtherCallersRetainStrictPositionalLexer()
    {
        Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery("SELECT CAST($1 AS INTEGER)", SqlAgentToolType.Postgres));
        Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.AnalyzeScript(
                "CREATE FUNCTION app.f(a integer) RETURNS integer LANGUAGE SQL AS $q$ SELECT ($1); $q$;",
                SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty()));
    }

    [Fact]
    public void ProcedureDoesNotInheritFunctionOnlyGrammar()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE PROCEDURE app.p() LANGUAGE SQL AS $body$ SELECT ($1); $body$;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Empty(result.ProceduralDefinitions);
        Assert.Contains(result.Diagnostics, d => d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED");
    }
}
