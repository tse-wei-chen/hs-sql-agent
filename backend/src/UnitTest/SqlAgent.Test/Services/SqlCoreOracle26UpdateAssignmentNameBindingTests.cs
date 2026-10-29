using System;
using System.Collections.Generic;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real Oracle 26ai returned ORA-00918 for an unqualified SET column
/// present in both target and FROM. Static target-only lookup cannot
/// silently certify a SQL statement that Oracle rejects as ambiguous.
/// </summary>
public sealed class SqlCoreOracle26UpdateAssignmentNameBindingTests
{
    private const string Ddl =
        "CREATE TABLE dest(id NUMBER NOT NULL PRIMARY KEY, payload NUMBER, bucket NUMBER);" +
        "CREATE TABLE lookup(id NUMBER NOT NULL PRIMARY KEY, payload NUMBER);" +
        "CREATE TABLE extra(id NUMBER NOT NULL PRIMARY KEY);";

    private static SqlCatalogState Catalog()
    {
        var ddl = SqlCoreFacade.AnalyzeScript(
            Ddl, SqlAgentToolType.Oracle, SqlCatalogState.KnownEmpty());
        Assert.Empty(ddl.Diagnostics);
        return ddl.FinalCatalog;
    }

    private static SqlStatementAnalysis One(string sql)
    {
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Oracle, ServerVersion: new Version(26, 0));
        return Assert.Single(SqlCoreFacade.AnalyzeScript(
            sql + ";", SqlAgentToolType.Oracle, Catalog(), profile).Statements);
    }

    [Theory]
    [InlineData("UPDATE dest SET payload = l.payload FROM lookup l WHERE dest.id = l.id")]
    [InlineData("UPDATE dest SET payload = 1 FROM lookup l WHERE dest.id = l.id")]
    [InlineData("UPDATE dest SET payload = l.id FROM lookup l WHERE dest.id = l.id")]
    [InlineData("UPDATE dest SET payload = 5 FROM extra x, lookup l WHERE dest.id = x.id AND x.id = l.id")]
    public void AmbiguousOracle26TargetAssignmentNeverProducesVerifiedBinding(string sql)
    {
        var item = One(sql);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, item.CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, item.Status);
        Assert.Contains(item.Diagnostics,
            d => d.Code == "SQL_SCHEMA_UPDATE_ASSIGNMENT_AMBIGUOUS");
        Assert.NotEqual(SqlMutationSourceMatchCardinality.ProvenAtMostOne,
            item.SourceMatchCardinality);
        Assert.Empty(item.Changes);
    }

    [Theory]
    [InlineData("UPDATE dest SET dest.payload = l.payload FROM lookup l WHERE dest.id = l.id")]
    [InlineData("UPDATE dest SET dest.payload = 1 FROM lookup l WHERE dest.id = l.id")]
    [InlineData("UPDATE dest SET bucket = l.id FROM lookup l WHERE dest.id = l.id")]
    [InlineData("UPDATE dest SET payload = 1 FROM extra x WHERE dest.id = x.id")]
    [InlineData("UPDATE dest SET payload = 1 WHERE id = 1")]
    public void ExplicitlyQualifiedOrUncontestedAssignmentRemainsVerified(string sql)
    {
        var item = One(sql);
        Assert.Equal(SqlCatalogBindingStatus.Verified, item.CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, item.Status);
        Assert.Empty(item.Diagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void SixProviderOrdinarySelectDoesNotUseAssignmentAmbiguityRule(SqlAgentToolType dialect)
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "SELECT 1;", dialect, SqlCatalogState.KnownEmpty());
        var statement = Assert.Single(result.Statements);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.CatalogBinding);
        Assert.Empty(statement.Diagnostics);
    }
}
