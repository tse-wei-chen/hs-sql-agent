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
/// Static INSERT contracts: explicit target arity, each VALUES row,
/// no duplicated target column, and a proven implicit physical column order.
/// Name/shape proofs are not SQL authorization or database execution.
/// </summary>
public sealed class SqlCoreInsertTargetProjectionContractTests
{
    public static IEnumerable<object[]> ModeledNames()
    {
        yield return [SqlAgentToolType.Postgres, "INTEGER", "TEXT"];
        yield return [SqlAgentToolType.Oracle, "NUMBER", "VARCHAR2(20)"];
        yield return [SqlAgentToolType.Firebird, "INTEGER", "VARCHAR(20)"];
        yield return [SqlAgentToolType.Sqlite, "INTEGER", "TEXT"];
    }

    public static IEnumerable<object[]> StrictTypes() =>
        ModeledNames().Where(x => (SqlAgentToolType)x[0] != SqlAgentToolType.Sqlite);

    private static SqlScriptAnalysisResult Analyze(
        SqlAgentToolType dialect, string numberType, string textType, string sql)
    {
        var setup =
            $"CREATE TABLE src (id {numberType}, label {textType});" +
            $"CREATE TABLE dest (id {numberType}, label {textType});";
        return SqlCoreFacade.AnalyzeScript(setup + sql + ";",
            dialect, SqlCatalogState.KnownEmpty());
    }

    [Theory]
    [MemberData(nameof(ModeledNames))]
    public void ExplicitAndImplicitCorrectWidthRetainKnownNameProof(
        SqlAgentToolType dialect, string num, string txt)
    {
        string[] inserts =
        [
            "INSERT INTO dest(id, label) VALUES (1, 'Ada')",
            "INSERT INTO dest(label, id) VALUES ('Ada', 1)",
            "INSERT INTO dest VALUES (1, 'Ada')",
            "INSERT INTO dest(id, label) VALUES (1, 'Ada'), (2, 'Bea')",
            "INSERT INTO dest VALUES (1, 'Ada'), (2, 'Bea')",
            "INSERT INTO dest SELECT id, label FROM src",
            "INSERT INTO dest(id, label) SELECT id, label FROM src"
        ];
        foreach (var insert in inserts)
        {
            var analysis = Analyze(dialect, num, txt, insert);
            Assert.Empty(analysis.Statements[0].Diagnostics);
            Assert.Empty(analysis.Statements[1].Diagnostics);
            var stmt = analysis.Statements[2];
            Assert.Equal(SqlScriptStatementKind.Mutation, stmt.Kind);
            Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.NameBinding);
            Assert.Equal(SqlStatementAnalysisStatus.PartiallyAnalyzed, stmt.Status);
            Assert.Empty(stmt.Diagnostics);
            Assert.Empty(stmt.Changes);
            Assert.Same(stmt.CatalogBefore, stmt.CatalogAfter);
        }
    }

    public static IEnumerable<object[]> MismatchedWidths()
    {
        string[] sql =
        [
            "INSERT INTO dest(id, label) VALUES (1)",
            "INSERT INTO dest(id, label) VALUES (1, 'Ada', 3)",
            "INSERT INTO dest(id, label) VALUES (1, 'Ada'), (2)",
            "INSERT INTO dest(id, label) VALUES (1, 'Ada'), (2, 'Bea', 3)",
            "INSERT INTO dest(id, label) SELECT id FROM src",
            "INSERT INTO dest(id, label) SELECT id, label, 3 FROM src",
            "INSERT INTO dest VALUES (1)",
            "INSERT INTO dest VALUES (1, 'Ada', 3)",
            "INSERT INTO dest SELECT id FROM src",
            "INSERT INTO dest SELECT id, label, 3 FROM src"
        ];
        foreach (var dialect in ModeledNames())
        foreach (var mutation in sql)
            yield return [dialect[0], dialect[1], dialect[2], mutation];
    }

    [Theory]
    [MemberData(nameof(MismatchedWidths))]
    public void WrongInsertWidthCannotBeCertifiedAsValid(
        SqlAgentToolType dialect, string num, string txt, string sql)
    {
        var result = Analyze(dialect, num, txt, sql);
        var stmt = result.Statements[2];
        Assert.Equal(SqlScriptStatementKind.Mutation, stmt.Kind);
        Assert.NotEqual(SqlCatalogBindingStatus.Verified, stmt.CatalogBinding);
        Assert.Contains(stmt.Diagnostics, d => d.Code == "SQL_SCHEMA_SHAPE_UNPROVABLE");
        Assert.Same(stmt.CatalogBefore, stmt.CatalogAfter);
        Assert.Empty(stmt.Changes);
    }

    [Theory]
    [MemberData(nameof(StrictTypes))]
    public void KnownImplicitColumnOrderRejectsProvableAssignmentTypeMismatches(
        SqlAgentToolType dialect, string num, string txt)
    {
        string[] inserts =
        [
            "INSERT INTO dest VALUES ('not-numeric', 1)",
            "INSERT INTO dest VALUES (1, 'Ada'), ('not-numeric', 'Bea')",
            "INSERT INTO dest SELECT label, id FROM src",
            "INSERT INTO dest(id, label) SELECT label, id FROM src",
            "INSERT INTO dest(label, id) VALUES (1, 'not-numeric')"
        ];
        foreach (var insert in inserts)
        {
            var stmt = Analyze(dialect, num, txt, insert).Statements[2];
            Assert.Equal(SqlCatalogBindingStatus.Verified, stmt.NameBinding);
            Assert.Equal(SqlCatalogBindingStatus.Rejected, stmt.TypeBinding);
            Assert.Equal(SqlCatalogBindingStatus.Rejected, stmt.CatalogBinding);
            Assert.Equal(SqlStatementAnalysisStatus.Invalid, stmt.Status);
            Assert.Contains(stmt.Diagnostics, d => d.Code == "SQL_SCHEMA_TYPE_MISMATCH");
        }
    }

    [Theory]
    [MemberData(nameof(ModeledNames))]
    public void DuplicateExplicitTargetIsNotAValidColumnList(
        SqlAgentToolType dialect, string num, string txt)
    {
        var stmt = Analyze(dialect, num, txt,
            "INSERT INTO dest(id, id) VALUES (1, 2)").Statements[2];
        Assert.Equal(SqlCatalogBindingStatus.Rejected, stmt.CatalogBinding);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, stmt.Status);
        Assert.Contains(stmt.Diagnostics, d => d.Code == "SQL_SCHEMA_INSERT_DUPLICATE_TARGET");
    }

    [Fact]
    public void PostgresInsertSelectCanReadProvenViewAndCheckImplicitTargetWidthAndTypes()
    {
        const string setup =
            "CREATE TABLE app.src(id INTEGER, label TEXT);" +
            "CREATE TABLE app.dest(id INTEGER, label TEXT);" +
            "CREATE VIEW app.v(id_alias, label_alias) AS SELECT id, label FROM app.src;";
        string[] valid =
        [
            "INSERT INTO app.dest SELECT id_alias, label_alias FROM app.v",
            "INSERT INTO app.dest SELECT * FROM app.v"
        ];
        foreach (var sql in valid)
        {
            var result = SqlCoreFacade.AnalyzeScript(setup + sql + ";",
                SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
            Assert.Equal(SqlCatalogBindingStatus.Verified, result.Statements[3].CatalogBinding);
            Assert.Empty(result.Statements[3].Diagnostics);
        }

        string[] invalid =
        [
            "INSERT INTO app.dest SELECT id_alias FROM app.v",
            "INSERT INTO app.dest SELECT id_alias, label_alias, 1 FROM app.v"
        ];
        foreach (var sql in invalid)
        {
            var result = SqlCoreFacade.AnalyzeScript(setup + sql + ";",
                SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
            Assert.NotEqual(SqlCatalogBindingStatus.Verified, result.Statements[3].CatalogBinding);
            Assert.Contains(result.Statements[3].Diagnostics,
                d => d.Code == "SQL_SCHEMA_SHAPE_UNPROVABLE");
        }

        var mismatch = SqlCoreFacade.AnalyzeScript(setup +
            "INSERT INTO app.dest SELECT label_alias, id_alias FROM app.v;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlCatalogBindingStatus.Rejected, mismatch.Statements[3].CatalogBinding);
        Assert.Contains(mismatch.Statements[3].Diagnostics,
            d => d.Code == "SQL_SCHEMA_TYPE_MISMATCH");
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnknownWorldCannotInventImplicitInsertTargetProof(SqlAgentToolType dialect)
    {
        var r = SqlCoreFacade.AnalyzeScript(
            "INSERT INTO dest SELECT 1;", dialect, SqlCatalogState.Unknown());
        var stmt = Assert.Single(r.Statements);
        Assert.Equal(SqlCatalogBindingStatus.Unverified, stmt.CatalogBinding);
        Assert.Contains(stmt.Diagnostics, d => d.Code == "SQL_CATALOG_BINDING_INCOMPLETE");
    }
}
