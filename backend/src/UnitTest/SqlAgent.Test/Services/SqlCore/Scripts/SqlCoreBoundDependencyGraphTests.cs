using System;
using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// U2: a static, ordered, *bound* dependency is not a diagnostic name or
/// authorization for physical SQL execution.
/// </summary>
public sealed class SqlCoreBoundDependencyGraphTests
{
    [Fact]
    public void ProvenCatalogThenMutationThenQuery_FormsTypedOrderedEdges()
    {
        const string sql =
            "CREATE TABLE users(id INTEGER);" +
            "UPDATE users SET id = 2 WHERE id = 1;" +
            "SELECT id FROM users";
        var report = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty(),
            TestContext.Current.CancellationToken);

        Assert.Empty(report.Diagnostics);
        Assert.Equal(SqlCatalogBindingStatus.Verified, report.Statements[1].NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, report.Statements[2].NameBinding);

        var edges = report.StaticDependencyEdges;
        Assert.Equal(2, edges.Count);
        Assert.Equal(new[] { (0, 1), (1, 2) },
            edges.Select(e => (e.PredecessorStatementIndex, e.SuccessorStatementIndex)));
        var identity = Assert.Single(report.FinalCatalog.Tables).QualifiedName.Key;
        Assert.All(edges, edge =>
        {
            Assert.Equal(identity, edge.ObjectIdentity.Key);
            Assert.False(edge.IsExecutionProof);
            Assert.Equal(report.Statements[edge.SuccessorStatementIndex].Syntax.Span.Start,
                edge.Span.Start);
        });

        // The original diagnostic-name projection remains intentionally untrusted.
        Assert.All(report.Dependencies,
            item => Assert.Equal(SqlCatalogKnowledge.Unknown, item.Certainty));
        var list = Assert.IsAssignableFrom<IList<SqlScriptStaticDependencyEdge>>(edges);
        Assert.Throws<NotSupportedException>(() => { list[0] = list[0]; });
    }

    [Fact]
    public void IdenticalNamesWithoutCatalogBinding_NeverProduceTypedEdges()
    {
        const string sql = "UPDATE users SET id = 2 WHERE id = 1; SELECT id FROM users";
        var report = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            SqlCatalogState.Unknown(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(report.Dependencies);
        Assert.Empty(report.StaticDependencyEdges);
    }

    [Fact]
    public void UnknownStatementCannotBridgeTwoOtherwiseMatchingCatalogReferences()
    {
        const string sql =
            "CREATE TABLE users(id INTEGER);" +
            "ZORP anything;" +
            "SELECT id FROM users";
        var report = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            SqlCatalogState.KnownEmpty(), TestContext.Current.CancellationToken);

        Assert.Equal(SqlEffectCoverage.Unknown, report.EffectCoverage);
        Assert.Empty(report.StaticDependencyEdges);
        Assert.NotEqual(SqlCatalogBindingStatus.Verified, report.Statements[2].NameBinding);
    }
}
