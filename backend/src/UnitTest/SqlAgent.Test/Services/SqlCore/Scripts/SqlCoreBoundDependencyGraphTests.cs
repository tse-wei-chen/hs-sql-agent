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
    [Fact]
    public void ProvenCteShadowing_DependsOnBoundPhysicalSourceNotSameNamedTable()
    {
        const string sql =
            "CREATE TABLE users(id INTEGER);" +
            "CREATE TABLE other(id INTEGER);" +
            "WITH users AS (SELECT id FROM other) SELECT id FROM users";
        var report = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            SqlCatalogState.KnownEmpty(), TestContext.Current.CancellationToken);

        Assert.Equal(SqlCatalogBindingStatus.Verified, report.Statements[2].NameBinding);
        var edge = Assert.Single(report.StaticDependencyEdges);
        Assert.Equal(1, edge.PredecessorStatementIndex);
        Assert.Equal(2, edge.SuccessorStatementIndex);
        Assert.Equal("other", edge.ObjectIdentity.DisplayName);

        // Identical display strings must not merge one quoted dotted
        // identifier with a two-part qualified physical identity.
        const string dotted =
            "CREATE TABLE \"app.orders\"(id INTEGER);" +
            "CREATE TABLE app.orders(id INTEGER);" +
            "SELECT id FROM \"app.orders\";" +
            "SELECT id FROM app.orders";
        var structured = SqlCoreFacade.AnalyzeScript(dotted, SqlAgentToolType.Postgres,
            SqlCatalogState.KnownEmpty(), TestContext.Current.CancellationToken);
        Assert.All(structured.Statements.Skip(2),
            statement => Assert.Equal(SqlCatalogBindingStatus.Verified, statement.NameBinding));
        Assert.Equal(new[] { (0, 2), (1, 3) },
            structured.StaticDependencyEdges.Select(e =>
                (e.PredecessorStatementIndex, e.SuccessorStatementIndex)));
        Assert.Equal(2, structured.StaticDependencyEdges
            .Select(e => e.ObjectIdentity.Key).Distinct().Count());
    }

    [Fact]
    public void SqlServerGoBoundary_DoesNotMintCrossBatchEdges()
    {
        const string sql = "CREATE TABLE users(id INT)\nGO\nALTER TABLE users ADD nickname VARCHAR(20)";
        var report = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MsSqlServer,
            SqlCatalogState.KnownEmpty(), TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Statements.Count);
        Assert.Equal(new[] { 0, 1 },
            report.Statements.Select(s => s.Syntax.BatchIndex));
        Assert.All(report.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        Assert.Empty(report.StaticDependencyEdges);
    }

}
