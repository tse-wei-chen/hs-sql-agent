using System;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL SQL-language Function positional argument analysis is an
/// isolated, bounded, non-executable syntax-only grammar, not an extension
/// of the generic query lexer or a trusted routine invocation.
/// </summary>
public sealed class SqlCoreFunctionPositionalParameterTests
{
    private static SqlScriptAnalysisResult Analyze(string arguments, string returnType, string body) =>
        SqlCoreFacade.AnalyzeScript(
            "CREATE FUNCTION app.positional(" + arguments + ") RETURNS " + returnType +
            " LANGUAGE SQL AS $body$" + body + "$body$;",
            SqlAgentToolType.Postgres,
            SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("amount integer", "integer", "SELECT $1;", SqlFunctionReturnEvidence.PositionalParameterMatch)]
    [InlineData("amount integer", "integer", "select $1", SqlFunctionReturnEvidence.PositionalParameterMatch)]
    [InlineData("n bigint", "bigint", "SELECT $1;", SqlFunctionReturnEvidence.PositionalParameterMatch)]
    [InlineData("label text", "text", "SELECT $1;", SqlFunctionReturnEvidence.PositionalParameterMatch)]
    [InlineData("ok boolean", "boolean", "SELECT $1;", SqlFunctionReturnEvidence.PositionalParameterMatch)]
    [InlineData("a integer, b bigint", "bigint", "SELECT $2;", SqlFunctionReturnEvidence.PositionalParameterMatch)]
    [InlineData("a boolean, b boolean", "boolean", "SELECT NOT $2;", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a integer, b integer", "integer", "SELECT $1 + $2;", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a integer, b bigint", "bigint", "SELECT $1 + $2;", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a bigint, b integer", "bigint", "SELECT $1 - $2;", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a integer, b integer", "integer", "SELECT $1 * $2;", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a boolean, b boolean", "boolean", "SELECT $1 AND $2;", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    [InlineData("a boolean, b boolean", "boolean", "SELECT $1 OR $2;", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    public void PositionalExpressionBindsToDeclaredOrdinalWithoutSqlReplacement(
        string args, string declared, string body, SqlFunctionReturnEvidence evidence)
    {
        var sql = "CREATE FUNCTION app.positional(" + args + ") RETURNS " + declared +
            " LANGUAGE SQL AS $body$" + body + "$body$;";
        var result = Analyze(args, declared, body);
        var statement = Assert.Single(result.Statements);
        var definition = Assert.Single(result.ProceduralDefinitions);
        var parsed = Assert.Single(definition.BodyStatements);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlScriptStatementKind.Query, parsed.Kind);
        Assert.Equal(SqlStatementEffectOperation.ReadTable, parsed.Operation);
        Assert.Empty(parsed.ReferencedTables);
        Assert.True(parsed.Span.Start >= sql.IndexOf("$body$", StringComparison.Ordinal) + 6);
        Assert.True(parsed.Span.End <= sql.LastIndexOf("$body$", StringComparison.Ordinal));
        Assert.True(parsed.PotentialEffects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.True(definition.PotentialBodyEffects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Equal(evidence, definition.FunctionReturn!.Evidence);
        Assert.False(definition.FunctionReturn.IsRuntimeReturnProven);
        Assert.Equal(SqlCatalogKnowledge.Unknown, definition.FunctionReturn.RuntimeCertainty);
        Assert.Equal(definition.Parameters.Count, args.Split(',').Length);
        Assert.Empty(result.Diagnostics);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("n integer", "integer", "SELECT $0;")]
    [InlineData("n integer", "integer", "SELECT $2;")]
    [InlineData("n integer", "integer", "SELECT $01;")]
    [InlineData("n integer", "integer", "SELECT $99999999999999999;")]
    [InlineData("n integer", "text", "SELECT $1;")]
    [InlineData("n integer", "bigint", "SELECT $1;")]
    [InlineData("a bigint, b integer", "integer", "SELECT $1 + $2;")]
    [InlineData("a integer, b bigint", "integer", "SELECT $1 + $2;")]
    [InlineData("a text, b text", "text", "SELECT $1 + $2;")]
    [InlineData("a boolean, b boolean", "integer", "SELECT $1 AND $2;")]
    [InlineData("a integer, b integer", "boolean", "SELECT $1 AND $2;")]
    [InlineData("a integer, b integer", "integer", "SELECT NOT $1;")]
    [InlineData("n integer", "integer", "SELECT $1 / $1;")]
    [InlineData("n integer", "integer", "SELECT $1 || $1;")]
    [InlineData("n integer", "integer", "SELECT $1 AS value;")]
    [InlineData("n integer", "integer", "SELECT $1 FROM app.users;")]
    [InlineData("n integer", "integer", "SELECT $1 WHERE true;")]
    [InlineData("n integer", "integer", "SELECT $1; SELECT 1;")]
    [InlineData("n integer", "integer", "SELECT $1; UPDATE app.orders SET id = 2;")]
    [InlineData("n integer", "integer", "SELECT '$1';")]
    [InlineData("n integer", "integer", "SELECT $$1;")]
    [InlineData("n integer", "integer", "SELECT $1 -- comment")]
    [InlineData("n integer", "integer", "SELECT $1 /* comment */;")]
    [InlineData("n integer", "integer", "SELECT $1 UNION SELECT 1;")]
    public void UnmodeledAndAmbiguousFormsFailClosedWithoutTrustedDefinition(
        string arguments, string type, string body)
    {
        var result = Analyze(arguments, type, body);
        Assert.Empty(result.ProceduralDefinitions);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(result.Statements).Status);
        Assert.True(Assert.Single(result.Statements).Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED" || d.Code == "SQL_PROCEDURAL_RETURN_UNPROVEN" || d.Code == "SQL_PROCEDURAL_RETURN_TYPE_MISMATCH");
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void OrdinaryQueryParserStillRejectsPostgreSqlPositionalSyntax()
    {
        Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery("SELECT $1", SqlAgentToolType.Postgres));
    }

    [Fact]
    public void ProcedureCannotInheritFunctionOnlyPositionalGrammar()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE PROCEDURE app.p() LANGUAGE SQL AS $body$ SELECT $1; $body$;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Empty(result.ProceduralDefinitions);
        Assert.Contains(result.Diagnostics, d => d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED");
    }

    [Fact]
    public void OtherProvidersNeverAcquireFunctionPositionalGrammar()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.AnalyzeScript(
                "CREATE FUNCTION app.positional(n integer) RETURNS integer LANGUAGE SQL AS $b$ SELECT $1; $b$;",
                SqlAgentToolType.MySQL, SqlCatalogState.KnownEmpty()));
    }

    [Fact]
    public void PositionalSymbolsAreIsolatedBetweenNeighboringFunctions()
    {
        var sql =
            "CREATE FUNCTION app.a(a integer) RETURNS integer LANGUAGE SQL AS $b$ SELECT $1; $b$;" +
            "CREATE FUNCTION app.b(a boolean, b boolean) RETURNS boolean LANGUAGE SQL AS $b$ SELECT $2; $b$;";
        var result = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Equal(2, result.ProceduralDefinitions.Count);
        Assert.All(result.ProceduralDefinitions, definition =>
            Assert.Equal(SqlFunctionReturnEvidence.PositionalParameterMatch, definition.FunctionReturn!.Evidence));
        Assert.Equal(1, result.ProceduralDefinitions[0].Parameters.Count);
        Assert.Equal(2, result.ProceduralDefinitions[1].Parameters.Count);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }
}
