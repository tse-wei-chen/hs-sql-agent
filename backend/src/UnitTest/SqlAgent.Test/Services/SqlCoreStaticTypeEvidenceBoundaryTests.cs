using System;
using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Family-only evidence in the six-dialect Query/DML type validator is NOT a
/// proof of a PostgreSQL Function's exact primitive result type.
/// </summary>
public sealed class SqlCoreStaticTypeEvidenceBoundaryTests
{
    public static IEnumerable<object[]> Dialects() =>
        Enum.GetValues<SqlAgentToolType>().Select(x => new object[] { x });

    private static SqlPlanValidationContext Typed(SqlAgentToolType provider)
    {
        bool upper = provider is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
        string Name(string value) => upper ? value.ToUpperInvariant() : value;
        var columns = new[]
        {
            new SqlSchemaColumn(Name("id"), SqlSchemaValueKind.Numeric),
            new SqlSchemaColumn(Name("description"), SqlSchemaValueKind.Text),
            new SqlSchemaColumn(Name("active"), SqlSchemaValueKind.Boolean)
        };
        return new SqlPlanValidationContext("static-family-v1")
            .WithSchemaSnapshot(new SqlSchemaSnapshot(provider, "snapshot-family-v1",
                new[] { SqlSchemaTable.WithTypes(Name("items"), columns) }));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void SixProviderTypedFamilyChecksKeepProvenMismatchesRejected(SqlAgentToolType provider)
    {
        var ctx = Typed(provider);
        foreach (var sql in new[]
        {
            "SELECT id FROM items WHERE id = 'not-numeric'",
            "SELECT description + 1 FROM items",
            "SELECT id FROM items WHERE id = description"
        })
        {
            var ex = Assert.Throws<SqlCompilationException>(() =>
                SqlCoreFacade.CompileQuery(sql, provider, provider, ctx,
                    new SqlExecutionPlanPolicy()));
            Assert.Equal("SQL_SCHEMA_TYPE_MISMATCH", ex.Diagnostic?.Code);
            Assert.Equal(SqlDiagnosticStage.SemanticValidation, ex.Diagnostic?.Stage);
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void SixProviderTypedFamilyAllowsProvenCompatibleInputs(SqlAgentToolType provider)
    {
        foreach (var sql in new[]
        {
            "SELECT id FROM items WHERE id = 7",
            "SELECT id FROM items WHERE description = 'abc'"
        })
        {
            var compiled = SqlCoreFacade.CompileQuery(sql, provider, provider,
                Typed(provider), new SqlExecutionPlanPolicy());
            Assert.NotEmpty(compiled.Sql);
        }
    }

    [Theory]
    [InlineData("integer", "SELECT CAST('7' AS INTEGER);")]
    [InlineData("bigint", "SELECT CAST('7' AS BIGINT);")]
    [InlineData("boolean", "SELECT CAST('true' AS BOOLEAN);")]
    [InlineData("integer", "SELECT CAST(v AS INTEGER);")]
    [InlineData("bigint", "SELECT CAST(v AS BIGINT);")]
    [InlineData("integer", "SELECT v + 1;")]
    public void TextFamilyCannotBecomeAnExactPrimitiveProof(string returnType, string body)
    {
        var report = SqlCoreFacade.AnalyzeScript(
            "CREATE FUNCTION app.unproven(v text) RETURNS " + returnType +
            " LANGUAGE SQL AS $f$ " + body + " $f$;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Empty(report.ProceduralDefinitions);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(report.Statements).Status);
        Assert.Contains(report.Diagnostics, d =>
            d.Code == "SQL_PROCEDURAL_RETURN_UNPROVEN" ||
            d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED");
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("integer", "SELECT 7;", SqlFunctionReturnEvidence.LiteralMatch)]
    [InlineData("bigint", "SELECT CAST(7 AS BIGINT);", SqlFunctionReturnEvidence.ExplicitCastMatch)]
    [InlineData("boolean", "SELECT TRUE;", SqlFunctionReturnEvidence.LiteralMatch)]
    [InlineData("text", "SELECT 'hello';", SqlFunctionReturnEvidence.LiteralMatch)]
    [InlineData("integer", "SELECT v + 1;", SqlFunctionReturnEvidence.PrimitiveExpressionMatch)]
    public void ExactPrimitiveProofDoesNotConferRuntimeCertainty(
        string returnType, string body, SqlFunctionReturnEvidence evidence)
    {
        var parameter = returnType == "integer" && body.Contains("v +", StringComparison.Ordinal)
            ? "v integer" : "";
        var report = SqlCoreFacade.AnalyzeScript(
            "CREATE FUNCTION app.exact(" + parameter + ") RETURNS " + returnType +
            " LANGUAGE SQL AS $f$ " + body + " $f$;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        var function = Assert.Single(report.ProceduralDefinitions);
        Assert.Equal(evidence, function.FunctionReturn!.Evidence);
        Assert.Equal(SqlCatalogKnowledge.Unknown, function.FunctionReturn.RuntimeCertainty);
        Assert.False(function.FunctionReturn.IsRuntimeReturnProven);
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }
}
