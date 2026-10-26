using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreProceduralBodyAstTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType provider = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, provider, SqlCatalogState.KnownEmpty());

    [Fact]
    public void PostgresSqlProcedureExposesOrderedTypedBodyStatementsWithoutExecutingThem()
    {
        var sql = "CREATE PROCEDURE app.clean_orders() LANGUAGE SQL AS $body$" +
            "SELECT id FROM app.orders; DELETE FROM app.orders WHERE id = 3;" +
            "$body$;";
        var report = Analyze(sql);
        var statement = Assert.Single(report.Statements);
        var definition = Assert.Single(report.ProceduralDefinitions);
        Assert.Same(definition, Assert.Single(statement.ProceduralDefinitions));
        Assert.Empty(report.ProceduralIntents);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Equal(SqlEffectCoverage.Partial, statement.EffectCoverage);
        Assert.Equal(SqlProceduralDefinitionKind.Procedure, definition.Kind);
        Assert.Equal("app.clean_orders", definition.Routine.DisplayName);
        Assert.True(definition.BodyAnalyzed);
        Assert.False(definition.IsBodyExecutionProven);
        Assert.Equal(SqlCatalogKnowledge.Unknown, definition.RuntimeCertainty);
        Assert.Equal(2, definition.BodyStatements.Count);
        Assert.Equal(SqlScriptStatementKind.Query, definition.BodyStatements[0].Kind);
        Assert.Equal(SqlScriptStatementKind.Mutation, definition.BodyStatements[1].Kind);
        Assert.Equal(SqlStatementEffectOperation.ReadTable, definition.BodyStatements[0].Operation);
        Assert.Equal(SqlStatementEffectOperation.Delete, definition.BodyStatements[1].Operation);
        Assert.True(definition.PotentialBodyEffects.HasFlag(SqlEffectKind.ReadData));
        Assert.True(definition.PotentialBodyEffects.HasFlag(SqlEffectKind.WriteData));
        Assert.True(definition.PotentialBodyEffects.HasFlag(SqlEffectKind.UnknownEffects));
        Assert.All(definition.BodyStatements, item =>
        {
            Assert.Equal(SqlCatalogKnowledge.Unknown, item.RuntimeCertainty);
            Assert.True(item.PotentialEffects.HasFlag(SqlEffectKind.UnknownEffects));
            Assert.True(item.Span.Start >= statement.Syntax.Span.Start);
            Assert.True(item.Span.Start + item.Span.Length <= statement.Syntax.Span.Start + statement.Syntax.Span.Length);
            Assert.Contains("app.orders", item.ReferencedTables);
        });
        Assert.Equal(SqlStatementEffectOperation.DefineProcedure,
            Assert.Single(statement.DetailedEffects).Operation);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.ChangeSchema));
        Assert.False(statement.Effects.HasFlag(SqlEffectKind.WriteData)); // Definition != invocation
        Assert.Null(statement.DetailedEffects[0].ObjectIdentity);
        Assert.Empty(statement.Changes);
        Assert.False(report.FinalCatalog.IsClosedWorld);
        Assert.Empty(report.Diagnostics);
    }

    [Fact]
    public void CteAndTwoMutationsRemainDistinctClosedAstStatements()
    {
        var sql = "CREATE OR REPLACE PROCEDURE app.apply_batch() LANGUAGE SQL AS $$" +
            "WITH source_rows AS (SELECT 1 AS id) SELECT id FROM source_rows;" +
            "UPDATE app.orders SET id = 2 WHERE id = 1;" +
            "INSERT INTO app.audit(id) VALUES (3);" +
            "$$;";
        var result = Analyze(sql);
        var def = Assert.Single(result.ProceduralDefinitions);
        Assert.Equal(3, def.BodyStatements.Count);
        Assert.Equal(SqlStatementEffectOperation.ReadTable, def.BodyStatements[0].Operation);
        Assert.Equal(SqlStatementEffectOperation.Update, def.BodyStatements[1].Operation);
        Assert.Equal(SqlStatementEffectOperation.Insert, def.BodyStatements[2].Operation);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void QuotedBodySemicolonsAreNotNaivelySplitAndFollowingStatementIsIndependent()
    {
        var sql = "CREATE PROCEDURE app.one() LANGUAGE SQL AS $$" +
            "SELECT ';' AS marker; SELECT 2;" +
            "$$; SELECT 9;";
        var result = Analyze(sql);
        Assert.Equal(2, result.Statements.Count);
        var def = Assert.Single(result.ProceduralDefinitions);
        Assert.Equal(2, def.BodyStatements.Count);
        Assert.Equal(SqlScriptStatementKind.Query, result.Statements[1].Kind);
        Assert.Empty(result.ProceduralIntents);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ADefinitionCannotMakeLaterInvocationRuntimeEffectsTrusted()
    {
        var result = Analyze(
            "CREATE PROCEDURE app.clean() LANGUAGE SQL AS $$SELECT 1;$$;" +
            "CALL app.clean();");
        Assert.Single(result.ProceduralDefinitions);
        var call = Assert.Single(result.ProceduralIntents);
        Assert.False(call.BodyAnalyzed);
        Assert.Equal(SqlCatalogKnowledge.Unknown, call.RuntimeCertainty);
        Assert.True(result.Statements[1].Effects.HasFlag(SqlEffectKind.WriteData));
        Assert.Equal(SqlTransactionRollbackScope.NotApplicable,
            result.Statements[1].TransactionProvenance.RollbackScope);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("CREATE PROCEDURE app.one() LANGUAGE plpgsql AS $$BEGIN SELECT 1; END$$;")]
    [InlineData("CREATE FUNCTION app.one() RETURNS integer LANGUAGE SQL AS $$SELECT 1$$;")]
    [InlineData("CREATE PROCEDURE app.one(i int) LANGUAGE SQL AS $$SELECT 1$$;")]
    [InlineData("CREATE PROCEDURE app.one() LANGUAGE SQL AS 'SELECT 1';")]
    [InlineData("CREATE PROCEDURE one() LANGUAGE SQL AS $$SELECT 1$$;")]
    [InlineData("CREATE PROCEDURE app.one() LANGUAGE SQL AS $$CREATE TABLE app.t(id int);$$;")]
    [InlineData("CREATE PROCEDURE app.one() LANGUAGE SQL AS $$CALL app.other();$$;")]
    [InlineData("CREATE PROCEDURE app.one() LANGUAGE SQL AS $$BEGIN; SELECT 1; COMMIT;$$;")]
    [InlineData("CREATE PROCEDURE app.one() LANGUAGE SQL AS $$EXECUTE 'SELECT 1';$$;")]
    [InlineData("CREATE PROCEDURE app.one() LANGUAGE SQL AS $$SELECT FROM;$$;")]
    [InlineData("CREATE PROCEDURE app.one() LANGUAGE SQL AS $$SELECT 1; INSERT INTO t VALUES(:id);$$;")]
    [InlineData("CREATE PROCEDURE app.one() LANGUAGE SQL AS $$SELECT 1$$ SECURITY DEFINER;")]
    public void UnknownProcedureDefinitionsNeverProducePartialBodyAst(string sql)
    {
        var report = Analyze(sql);
        Assert.Empty(report.ProceduralDefinitions);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(report.Statements).Status);
        Assert.Equal(SqlEffectCoverage.Unknown, report.Statements[0].EffectCoverage);
        Assert.Equal(SqlStatementEffectOperation.Unknown, Assert.Single(report.DetailedEffects).Operation);
        Assert.False(report.FinalCatalog.IsClosedWorld);
        Assert.Contains(report.Diagnostics, d =>
            d.Code == "SQL_PROCEDURAL_UNSUPPORTED" || d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED");
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void NoPostgresProcedureGrammarBorrowedByOtherProviders(SqlAgentToolType provider)
    {
        var report = Analyze("CREATE PROCEDURE app.p() LANGUAGE SQL AS $$SELECT 1;$$;", provider);
        Assert.Empty(report.ProceduralDefinitions);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(report.Statements).Status);
    }

    [Fact]
    public void BodyAnalysisHasBoundedStatementBudget()
    {
        var body = string.Concat(Enumerable.Repeat("SELECT 1;", 17));
        var result = Analyze("CREATE PROCEDURE app.many() LANGUAGE SQL AS $$" + body + "$$;");
        Assert.Empty(result.ProceduralDefinitions);
        Assert.Contains(result.Diagnostics, d => d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED");
    }

    [Fact]
    public void ADefinitionInActiveTransactionCannotProveRuntimeRollback()
    {
        var result = Analyze("BEGIN; CREATE PROCEDURE app.p() LANGUAGE SQL AS $$SELECT 1;$$; ROLLBACK;");
        Assert.Single(result.ProceduralDefinitions);
        Assert.Equal(SqlTransactionState.Unknown, result.Statements[1].TransactionAfter);
        Assert.Equal(SqlTransactionRollbackScope.Unproven, result.Statements[1].TransactionProvenance.RollbackScope);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }
}
