using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Static Session premises cannot survive opaque statements that might
/// alter session state. Static Catalog knowledge is never runtime attestation.
/// </summary>
public sealed class SqlCoreOpaqueSessionProofInvalidationTests
{
    private static SqlScriptAnalysisResult Pg(string script) =>
        SqlCoreFacade.AnalyzeScript(script, SqlAgentToolType.Postgres,
            SqlCatalogState.KnownEmpty());

    [Fact]
    public void UnknownCommandMustNotRetainPreviouslyProvenSearchPath()
    {
        var result = Pg("CREATE TABLE app.users(id INTEGER);" +
            "SET search_path TO app; DISCARD ALL; SELECT id FROM users;");

        var opaque = result.Statements[2];
        Assert.Equal(SqlScriptStatementKind.Unknown, opaque.Kind);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, opaque.Status);
        Assert.Equal("app", opaque.SessionBefore.SearchPath);
        Assert.True(opaque.SessionBefore.IsSearchPathProven);
        Assert.False(opaque.SessionAfter.IsSearchPathProven);
        Assert.Null(opaque.SessionAfter.SearchPath);
        Assert.False(result.FinalSession.IsSearchPathProven);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.NotEqual(SqlCatalogBindingStatus.Verified,
            result.Statements[3].CatalogBinding);
    }

    [Theory]
    [InlineData("GRANT ALL ON app.users TO reader;", SqlScriptStatementKind.Security)]
    [InlineData("CALL app.reset(1);", SqlScriptStatementKind.Procedural)]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = sales;", SqlScriptStatementKind.Schema)]
    public void UnsupportedOpaqueFamiliesDiscardStaleSessionPremises(
        string statement, SqlScriptStatementKind family)
    {
        var result = Pg("SET search_path TO app; " + statement);
        var opaque = result.Statements[1];

        Assert.Equal(family, opaque.Kind);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, opaque.Status);
        Assert.True(opaque.SessionBefore.IsSearchPathProven);
        Assert.False(opaque.SessionAfter.IsSearchPathProven);
        Assert.False(result.FinalSession.IsSearchPathProven);
    }

    [Fact]
    public void RecognizedProcedureCallCanMutateSessionDespitePartialSyntaxAnalysis()
    {
        var result = Pg("SET search_path TO app; CALL app.reset();");
        var call = result.Statements[1];

        Assert.Equal(SqlScriptStatementKind.Procedural, call.Kind);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, call.Status);
        Assert.Single(call.ProceduralIntents);
        Assert.True(call.Effects.HasFlag(SqlEffectKind.ChangeSession));
        Assert.False(call.SessionAfter.IsSearchPathProven);
        Assert.Null(result.FinalSession.SearchPath);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ExplainAnalyzeDoesNotPreservePriorSearchPathWhenItsQueryMayExecute()
    {
        var result = Pg("SET search_path TO app; EXPLAIN ANALYZE SELECT 1;");
        var utility = result.Statements[1];

        Assert.Equal(SqlScriptStatementKind.Utility, utility.Kind);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, utility.Status);
        Assert.True(Assert.Single(utility.UtilityIntents).MayExecuteStatement);
        Assert.True(utility.SessionBefore.IsSearchPathProven);
        Assert.False(utility.SessionAfter.IsSearchPathProven);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void PlanningOnlyExplainKeepsPreviouslyProvenSessionScope()
    {
        var result = Pg("SET search_path TO app; EXPLAIN SELECT 1;");
        var utility = result.Statements[1];

        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, utility.Status);
        Assert.False(Assert.Single(utility.UtilityIntents).MayExecuteStatement);
        Assert.Equal("app", utility.SessionAfter.SearchPath);
        Assert.True(result.FinalSession.IsSearchPathProven);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void ExplicitSupportedSessionTransitionKeepsNewScope()
    {
        var result = Pg("SET search_path TO app; SET search_path TO sales;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal("app", result.Statements[1].SessionBefore.SearchPath);
        Assert.Equal("sales", result.FinalSession.SearchPath);
        Assert.True(result.FinalSession.IsSearchPathProven);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void InvalidQuerySyntaxDoesNotInventAnExecutedSessionChange()
    {
        var result = Pg("SET search_path TO app; SELECT FROM users;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, result.Statements[1].Status);
        Assert.Equal("app", result.Statements[1].SessionAfter.SearchPath);
        Assert.True(result.FinalSession.IsSearchPathProven);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnclassifiedSqlCannotKeepSqlServerNoCountProof()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "SET NOCOUNT ON; DISCARD ALL;",
            SqlAgentToolType.MsSqlServer, SqlCatalogState.KnownEmpty());

        Assert.Equal(true, result.Statements[0].SessionAfter.NoCount);
        Assert.Equal(SqlScriptStatementKind.Unknown, result.Statements[1].Kind);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, result.Statements[1].Status);
        Assert.Null(result.Statements[1].SessionAfter.NoCount);
        Assert.Null(result.FinalSession.NoCount);
    }
}
