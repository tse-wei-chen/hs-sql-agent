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
/// An SQL qualifier is a unique relation identity, not "the one table
/// containing this column". Multiple candidates must fail closed even
/// when only one candidate happens to declare the referenced column.
/// </summary>
public sealed class SqlCoreMutationQualifierProvenanceTests
{
    private const string CatalogScript =
        "CREATE TABLE app.dest (id INTEGER, own_only INTEGER, label TEXT);" +
        "CREATE TABLE app.src (id INTEGER, source_only INTEGER);" +
        "CREATE TABLE audit.src (id INTEGER, audit_only INTEGER);";

    private static SqlCatalogState Catalog()
    {
        var setup = SqlCoreFacade.AnalyzeScript(
            CatalogScript, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Empty(setup.Diagnostics);
        Assert.Equal(3, setup.FinalCatalog.Tables.Count);
        return setup.FinalCatalog;
    }

    public static IEnumerable<object[]> ConflictingMutationQualifiers()
    {
        string[] statements =
        [
            "UPDATE app.dest AS src SET label = 'changed' FROM app.src WHERE src.own_only = 1",
            "DELETE FROM app.dest AS src USING app.src WHERE src.own_only = 1",
            "UPDATE app.dest AS d SET label = 'changed' FROM app.src, audit.src WHERE src.source_only = 1",
            "DELETE FROM app.dest AS d USING app.src, audit.src WHERE src.audit_only = 1",
            "UPDATE app.dest AS src SET label = 'changed' FROM app.src WHERE src.source_only = 1",
            "DELETE FROM app.dest AS src USING app.src WHERE src.source_only = 1"
        ];
        foreach (var sql in statements) yield return [sql];
    }

    [Theory]
    [MemberData(nameof(ConflictingMutationQualifiers))]
    public void ConflictingSourceQualifierCannotAcquireNameProofFromUniqueColumn(string sql)
    {
        var catalog = Catalog();
        var result = SqlCoreFacade.AnalyzeScript(
            sql + ";", SqlAgentToolType.Postgres, catalog);
        var statement = Assert.Single(result.Statements);

        Assert.Equal(SqlScriptStatementKind.Mutation, statement.Kind);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, statement.CatalogBinding);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, statement.NameBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, statement.Status);
        Assert.Contains(statement.Diagnostics, d =>
            d.Code == "SQL_SCHEMA_QUALIFIER_UNPROVABLE");
        Assert.Empty(statement.Changes);
        Assert.Same(catalog, statement.CatalogBefore);
        Assert.Same(catalog, statement.CatalogAfter);
        Assert.Same(catalog, result.FinalCatalog);
    }

    [Theory]
    [InlineData("UPDATE app.dest AS d SET label = 'changed' FROM app.src, audit.src WHERE app.src.source_only = 1 AND audit.src.audit_only = 1 AND d.own_only = 1")]
    [InlineData("DELETE FROM app.dest AS d USING app.src, audit.src WHERE app.src.source_only = 1 AND audit.src.audit_only = 1 AND d.own_only = 1")]
    [InlineData("UPDATE app.dest AS d SET label = 'changed' FROM app.src AS s WHERE s.source_only = 1 AND d.own_only = 1")]
    [InlineData("DELETE FROM app.dest AS d USING app.src AS s WHERE s.source_only = 1 AND d.own_only = 1")]
    public void UniqueExplicitAliasOrFullyQualifiedSourceStillProvesMutation(string sql)
    {
        var catalog = Catalog();
        var statement = Assert.Single(SqlCoreFacade.AnalyzeScript(
            sql + ";", SqlAgentToolType.Postgres, catalog).Statements);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.NameBinding);
        Assert.Equal(SqlCatalogBindingStatus.Verified, statement.CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, statement.Status);
        Assert.Empty(statement.Diagnostics);
        Assert.Same(catalog, statement.CatalogAfter);
    }

    [Theory]
    [InlineData("UPDATE app.dest AS d SET label = 'changed' FROM app.src AS s WHERE id = 1")]
    [InlineData("DELETE FROM app.dest AS d USING app.src AS s WHERE id = 1")]
    public void ExistingUnqualifiedAmbiguityFenceStillRejects(string sql)
    {
        var statement = Assert.Single(SqlCoreFacade.AnalyzeScript(
            sql + ";", SqlAgentToolType.Postgres, Catalog()).Statements);
        Assert.Equal(SqlCatalogBindingStatus.Rejected, statement.CatalogBinding);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_SCHEMA_COLUMN_AMBIGUOUS");
    }

    public static IEnumerable<object[]> SixProviderQueries()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
            yield return [provider];
    }

    private static SqlPlanValidationContext Snapshot(SqlAgentToolType dialect)
    {
        // This is an explicit provider-local Snapshot; unlike Virtual Catalog
        // identifier rules, its schema claims are supplied by the host.
        var upper = dialect is SqlAgentToolType.Oracle or SqlAgentToolType.Firebird;
        string Name(string value) => upper ? value.ToUpperInvariant() : value;
        return new SqlPlanValidationContext("qualifier-proof-v1")
            .WithSchemaSnapshot(new SqlSchemaSnapshot(
                dialect, "qualifier-proof-v1",
                [
                    new SqlSchemaTable(Name("app.src"), [Name("id"), Name("source_only")]),
                    new SqlSchemaTable(Name("audit.src"), [Name("id"), Name("audit_only")])
                ]));
    }

    [Theory]
    [MemberData(nameof(SixProviderQueries))]
    public void SixDialectsRejectSharedUnaliasedTailQualifierEvenWhenOnlyOneHasColumn(
        SqlAgentToolType dialect)
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT src.source_only FROM app.src JOIN audit.src ON 1 = 1",
                dialect, dialect, Snapshot(dialect), new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_SCHEMA_QUALIFIER_UNPROVABLE", ex.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Binding, ex.Diagnostic?.Stage);
    }

    [Theory]
    [MemberData(nameof(SixProviderQueries))]
    public void DistinctAliasesStillPermitSixProviderQueryScope(SqlAgentToolType dialect)
    {
        var command = SqlCoreFacade.CompileQuery(
            "SELECT a.source_only FROM app.src a JOIN audit.src b ON a.id = b.id",
            dialect, dialect, Snapshot(dialect), new SqlExecutionPlanPolicy());
        Assert.NotEmpty(command.Sql);
    }

    [Fact]
    public void UnknownWorldNeverPromotesQualifierEvidence()
    {
        var statement = Assert.Single(SqlCoreFacade.AnalyzeScript(
            "UPDATE app.dest AS src SET label = 'changed' FROM app.src WHERE src.own_only = 1",
            SqlAgentToolType.Postgres, SqlCatalogState.Unknown()).Statements);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, statement.CatalogBinding);
        Assert.Contains(statement.Diagnostics, d => d.Code == "SQL_CATALOG_BINDING_INCOMPLETE");
    }
}
