using System;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// A known incompatible static return shape is distinct from unproven
/// semantics. Neither is a claim about PostgreSQL runtime assignment casts.
/// </summary>
public sealed class SqlCoreFunctionReturnDiagnosticProvenanceTests
{
    private static (string Sql, SqlScriptAnalysisResult Report) Analyze(
        string parameters, string declared, string body)
    {
        var sql = "CREATE FUNCTION app.provenance(" + parameters + ") RETURNS " +
            declared + " LANGUAGE SQL AS $fn$" + body + "$fn$;";
        return (sql, SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty()));
    }

    [Theory]
    [InlineData("", "integer", "SELECT TRUE;")]
    [InlineData("", "boolean", "SELECT 23;")]
    [InlineData("", "text", "SELECT FALSE;")]
    [InlineData("arg bigint", "integer", "SELECT arg;")]
    [InlineData("arg boolean", "integer", "SELECT arg;")]
    [InlineData("arg integer", "boolean", "SELECT arg + 2;")]
    [InlineData("arg integer", "boolean", "SELECT arg + 1;")]
    [InlineData("arg boolean", "integer", "SELECT $1;")]
    [InlineData("arg bigint", "integer", "SELECT $1;")]
    [InlineData("arg integer, other bigint", "integer", "SELECT $1 + $2;")]
    [InlineData("arg boolean", "integer", "SELECT NOT $1;")]
    [InlineData("arg boolean", "integer", "SELECT $1 AND TRUE;")]
    public void ProvenPrimitiveMismatchHasPreciseBodyDiagnostic(
        string parameters, string declared, string body)
    {
        var (sql, report) = Analyze(parameters, declared, body);
        Assert.Empty(report.ProceduralDefinitions);
        var statement = Assert.Single(report.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal("SQL_PROCEDURAL_RETURN_TYPE_MISMATCH", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.SemanticValidation, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Semantic, diagnostic.Category);
        var start = sql.IndexOf("$fn$", StringComparison.Ordinal) + 4;
        var end = sql.LastIndexOf("$fn$", StringComparison.Ordinal);
        Assert.InRange(diagnostic.Span.Start, start, end);
        Assert.InRange(diagnostic.Span.End, diagnostic.Span.Start + 1, end);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("", "integer", "SELECT unknown;")]
    [InlineData("", "integer", "SELECT NULL;")]
    [InlineData("arg integer", "integer", "SELECT mystery(arg);")]
    [InlineData("arg integer", "integer", "SELECT arg / 2;")]
    [InlineData("arg integer", "integer", "SELECT CAST(arg AS NUMERIC);")]
    [InlineData("arg integer", "integer", "SELECT $1 / 2;")]
    [InlineData("arg integer", "integer", "SELECT CAST($1 AS NUMERIC);")]
    public void UnprovenReturnDoesNotPretendToBeTypeMismatch(
        string parameters, string declared, string body)
    {
        var (sql, report) = Analyze(parameters, declared, body);
        Assert.Empty(report.ProceduralDefinitions);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(report.Statements).Status);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.NotEqual("SQL_PROCEDURAL_RETURN_TYPE_MISMATCH", diagnostic.Code);
        Assert.Contains(diagnostic.Code, new[]
        {
            "SQL_PROCEDURAL_RETURN_UNPROVEN", "SQL_PROCEDURAL_BODY_UNSUPPORTED"
        });
        if (diagnostic.Code == "SQL_PROCEDURAL_RETURN_UNPROVEN")
        {
            var start = sql.IndexOf("$fn$", StringComparison.Ordinal) + 4;
            var end = sql.LastIndexOf("$fn$", StringComparison.Ordinal);
            Assert.InRange(diagnostic.Span.Start, start, end);
            Assert.True(diagnostic.Span.End <= end);
        }
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void MultiStatementBatchRetainsAbsoluteReturnDiagnosticOffsets()
    {
        const string first = "CREATE FUNCTION app.one() RETURNS integer LANGUAGE SQL AS $fn$SELECT 1;$fn$;";
        const string second = "CREATE FUNCTION app.two() RETURNS integer LANGUAGE SQL AS $fn$SELECT FALSE;$fn$;";
        var report = SqlCoreFacade.AnalyzeScript(first + second, SqlAgentToolType.Postgres,
            SqlCatalogState.KnownEmpty());
        Assert.Equal(2, report.Statements.Count);
        Assert.Single(report.ProceduralDefinitions);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal("SQL_PROCEDURAL_RETURN_TYPE_MISMATCH", diagnostic.Code);
        var body = (first + second).LastIndexOf("SELECT FALSE;", StringComparison.Ordinal);
        Assert.InRange(diagnostic.Span.Start, body, body + "SELECT FALSE;".Length);
    }

    [Fact]
    public void ValidFunctionStillCarriesStaticOnlyEvidenceAndOriginalBodySpan()
    {
        var (sql, report) = Analyze("arg integer", "integer", " SELECT $1 + 1; ");
        var proof = Assert.Single(report.ProceduralDefinitions).FunctionReturn!;
        Assert.Equal(SqlFunctionReturnEvidence.PrimitiveExpressionMatch, proof.Evidence);
        Assert.Equal(SqlCatalogKnowledge.Unknown, proof.RuntimeCertainty);
        Assert.False(proof.IsRuntimeReturnProven);
        var bodyStart = sql.IndexOf("$fn$", StringComparison.Ordinal) + 4;
        Assert.Equal(bodyStart, proof.ResultSpan.Start);
        Assert.Equal(" SELECT $1 + 1; ".Length, proof.ResultSpan.Length);
        Assert.Empty(report.Diagnostics);
    }
}
