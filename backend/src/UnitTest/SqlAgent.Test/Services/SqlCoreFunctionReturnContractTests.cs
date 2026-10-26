using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Return contracts are syntactic AST evidence, not executed results,
/// server type lookup, function installation or authority to invoke it.
/// </summary>
public sealed class SqlCoreFunctionReturnContractTests
{
    private static SqlScriptAnalysisResult Analyze(string sql, SqlAgentToolType provider = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, provider, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("integer", "SELECT 42;", SqlFunctionReturnType.Integer, SqlFunctionReturnEvidence.LiteralMatch)]
    [InlineData("int4", "SELECT 7;", SqlFunctionReturnType.Integer, SqlFunctionReturnEvidence.LiteralMatch)]
    [InlineData("boolean", "SELECT TRUE;", SqlFunctionReturnType.Boolean, SqlFunctionReturnEvidence.LiteralMatch)]
    [InlineData("bool", "SELECT FALSE;", SqlFunctionReturnType.Boolean, SqlFunctionReturnEvidence.LiteralMatch)]
    [InlineData("text", "SELECT 'ok';", SqlFunctionReturnType.Text, SqlFunctionReturnEvidence.LiteralMatch)]
    [InlineData("bigint", "SELECT CAST(42 AS BIGINT);", SqlFunctionReturnType.BigInteger, SqlFunctionReturnEvidence.ExplicitCastMatch)]
    [InlineData("integer", "SELECT CAST(42 AS INTEGER);", SqlFunctionReturnType.Integer, SqlFunctionReturnEvidence.ExplicitCastMatch)]
    public void ScalarReturnRequiresLiteralOrExplicitCompatibleCast(
        string returnType, string body, SqlFunctionReturnType expected, SqlFunctionReturnEvidence evidence)
    {
        var sql = $"CREATE FUNCTION app.answer() RETURNS {returnType} LANGUAGE SQL AS $fn$${body}$fn$;";
        var analysis = Analyze(sql);
        var statement = Assert.Single(analysis.Statements);
        var definition = Assert.Single(analysis.ProceduralDefinitions);
        Assert.Same(definition, Assert.Single(statement.ProceduralDefinitions));
        Assert.Equal(SqlProceduralDefinitionKind.Function, definition.Kind);
        Assert.Equal("app.answer", definition.Routine.DisplayName);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
        Assert.Equal(SqlStatementEffectOperation.DefineFunction, Assert.Single(statement.DetailedEffects).Operation);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.ChangeSchema));
        Assert.False(statement.Effects.HasFlag(SqlEffectKind.WriteData));
        Assert.Equal(expected, definition.FunctionReturn!.DeclaredType);
        Assert.Equal(evidence, definition.FunctionReturn.Evidence);
        Assert.False(definition.FunctionReturn.IsRuntimeReturnProven);
        Assert.Equal(SqlCatalogKnowledge.Unknown, definition.FunctionReturn.RuntimeCertainty);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Single(definition.BodyStatements);
        Assert.True(definition.FunctionReturn.ResultSpan.Start >= statement.Syntax.Span.Start);
        Assert.True(definition.FunctionReturn.ResultSpan.End <= statement.Syntax.Span.End);
        Assert.Empty(analysis.ProceduralIntents);
        Assert.Empty(analysis.Diagnostics);
        Assert.False(analysis.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void AlternatePostgresqlAsBodyLanguageOrderAndNestedStatementsAreModeled()
    {
        var report = Analyze(
            "CREATE OR REPLACE FUNCTION app.flag() RETURNS boolean AS $q$" +
            "UPDATE app.jobs SET status = 1 WHERE id = 1;" +
            "SELECT TRUE;" +
            "$q$ LANGUAGE SQL;");
        var definition = Assert.Single(report.ProceduralDefinitions);
        Assert.Equal(SqlFunctionReturnType.Boolean, definition.FunctionReturn!.DeclaredType);
        Assert.Equal(SqlFunctionReturnEvidence.LiteralMatch, definition.FunctionReturn.Evidence);
        Assert.Equal(2, definition.BodyStatements.Count);
        Assert.Equal(SqlStatementEffectOperation.Update, definition.BodyStatements[0].Operation);
        Assert.Equal(SqlStatementEffectOperation.ReadTable, definition.BodyStatements[1].Operation);
        Assert.True(definition.PotentialBodyEffects.HasFlag(SqlEffectKind.WriteData));
        Assert.False(Assert.Single(report.Statements).Effects.HasFlag(SqlEffectKind.WriteData));
        Assert.Empty(report.Diagnostics);
    }

    [Theory]
    [InlineData("DELETE FROM app.jobs WHERE id = 1;")]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT 1; UPDATE app.jobs SET id = 2;")]
    public void VoidReturnDoesNotRequireAResultProducingFinalStatement(string body)
    {
        var report = Analyze("CREATE FUNCTION app.cleanup() RETURNS void LANGUAGE SQL AS $body$" +
            body + "$body$;");
        var definition = Assert.Single(report.ProceduralDefinitions);
        Assert.Equal(SqlProceduralDefinitionKind.Function, definition.Kind);
        Assert.Equal(SqlFunctionReturnType.Void, definition.FunctionReturn!.DeclaredType);
        Assert.Equal(SqlFunctionReturnEvidence.VoidNoValueRequired, definition.FunctionReturn.Evidence);
        Assert.False(definition.FunctionReturn.IsRuntimeReturnProven);
        Assert.Equal(SqlStatementEffectOperation.DefineFunction, Assert.Single(report.DetailedEffects).Operation);
        Assert.Empty(report.Diagnostics);
    }

    [Theory]
    [InlineData("integer", "SELECT 'hello';")]
    [InlineData("boolean", "SELECT 1;")]
    [InlineData("text", "SELECT TRUE;")]
    [InlineData("integer", "SELECT 2147483648;")]
    [InlineData("bigint", "SELECT 1;")]
    [InlineData("integer", "SELECT 1 + 2;")]
    [InlineData("integer", "SELECT id FROM app.orders;")]
    [InlineData("integer", "SELECT 1, 2;")]
    [InlineData("integer", "SELECT 1 UNION SELECT 2;")]
    [InlineData("integer", "SELECT 1 WHERE FALSE;")]
    [InlineData("integer", "SELECT 1 LIMIT 1;")]
    [InlineData("integer", "UPDATE app.orders SET id = 2;")]
    [InlineData("integer", "SELECT CAST('not-number' AS INTEGER);")]
    [InlineData("text", "SELECT CAST(7 AS TEXT);")]
    public void UnprovenReturnTypeOrShapeFailsClosedWithoutDefinition(
        string returnType, string body)
    {
        var report = Analyze($"CREATE FUNCTION app.bad() RETURNS {returnType} LANGUAGE SQL AS $fn$" +
            body + "$fn$;");
        Assert.Empty(report.ProceduralDefinitions);
        var statement = Assert.Single(report.Statements);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, statement.Status);
        Assert.Equal(SqlEffectCoverage.Unknown, statement.EffectCoverage);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_PROCEDURAL_RETURN_UNPROVEN");
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("CREATE FUNCTION app.f(a integer) RETURNS integer LANGUAGE SQL AS $b$SELECT 1;$b$;")]
    [InlineData("CREATE FUNCTION app.f() RETURNS SETOF integer LANGUAGE SQL AS $b$SELECT 1;$b$;")]
    [InlineData("CREATE FUNCTION app.f() RETURNS TABLE(value integer) LANGUAGE SQL AS $b$SELECT 1;$b$;")]
    [InlineData("CREATE FUNCTION app.f() RETURNS numeric LANGUAGE SQL AS $b$SELECT 1;$b$;")]
    [InlineData("CREATE FUNCTION app.f() RETURNS integer LANGUAGE plpgsql AS $b$SELECT 1;$b$;")]
    [InlineData("CREATE FUNCTION app.f() RETURNS integer LANGUAGE SQL AS $b$SELECT 1;$b$ SECURITY DEFINER;")]
    [InlineData("CREATE FUNCTION app.f() RETURNS integer LANGUAGE SQL AS $b$BEGIN ATOMIC SELECT 1; END;$b$;")]
    [InlineData("CREATE FUNCTION app.f() RETURNS integer LANGUAGE SQL AS 'SELECT 1;';")]
    [InlineData("CREATE FUNCTION app.f() RETURNS integer LANGUAGE SQL AS $b$SELECT 1; COMMIT;$b$;")]
    [InlineData("CREATE FUNCTION app.f() RETURNS integer LANGUAGE SQL AS $b$SELECT FROM;$b$;")]
    public void UnsupportedFunctionGrammarCannotLeakPartialBodyOrReturnProof(string sql)
    {
        var report = Analyze(sql);
        Assert.Empty(report.ProceduralDefinitions);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(report.Statements).Status);
        Assert.True(report.FinalCatalog.IsClosedWorld == false);
        Assert.Contains(report.Diagnostics, d =>
            d.Code == "SQL_PROCEDURAL_UNSUPPORTED" || d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED");
    }

    [Fact]
    public void ProcedureRemainsDistinctFromFunctionReturnContractsAndLaterCall()
    {
        var report = Analyze(
            "CREATE PROCEDURE app.work() LANGUAGE SQL AS $b$ SELECT 1; $b$;" +
            "CREATE FUNCTION app.answer() RETURNS integer LANGUAGE SQL AS $b$ SELECT 42; $b$;" +
            "CALL app.work();");
        Assert.Equal(3, report.Statements.Count);
        Assert.Equal(2, report.ProceduralDefinitions.Count);
        Assert.Equal(SqlProceduralDefinitionKind.Procedure, report.ProceduralDefinitions[0].Kind);
        Assert.Null(report.ProceduralDefinitions[0].FunctionReturn);
        Assert.Equal(SqlProceduralDefinitionKind.Function, report.ProceduralDefinitions[1].Kind);
        Assert.NotNull(report.ProceduralDefinitions[1].FunctionReturn);
        Assert.Equal(SqlStatementEffectOperation.DefineProcedure, report.Statements[0].DetailedEffects[0].Operation);
        Assert.Equal(SqlStatementEffectOperation.DefineFunction, report.Statements[1].DetailedEffects[0].Operation);
        Assert.False(Assert.Single(report.ProceduralIntents).BodyAnalyzed);
        Assert.Equal(SqlCatalogKnowledge.Unknown, report.FinalCatalog.Knowledge);
    }

    [Fact]
    public void UnsupportedRuntimeEffectsNeverTurnIntoVerifiedFunctionExecution()
    {
        var report = Analyze(
            "BEGIN; CREATE FUNCTION app.answer() RETURNS integer LANGUAGE SQL AS $b$SELECT 42;$b$;" +
            "ROLLBACK;");
        var function = Assert.Single(report.ProceduralDefinitions);
        Assert.False(function.FunctionReturn!.IsRuntimeReturnProven);
        Assert.Equal(SqlTransactionState.Unknown, report.FinalTransaction);
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }
}
