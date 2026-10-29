using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// A rename is one typed, immutable constraint-identity transition, not an
/// ALTER COLUMN string rewrite. PostgreSQL and Oracle have different
/// physical constraint/index name scopes and must not be conflated.
/// </summary>
public sealed class SqlCoreRenameConstraintTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres,
        SqlCatalogState? state = null) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, state ?? SqlCatalogState.KnownEmpty(),
            null, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER", "app.")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER", "")]
    public void PrimaryKeyRenamePreservesStructureAndHistoricalSnapshot(
        SqlAgentToolType dialect, string number, string schema)
    {
        var result = Analyze(
            $"CREATE TABLE {schema}parent(id {number} NOT NULL, " +
            "CONSTRAINT pk_parent PRIMARY KEY(id));" +
            $"CREATE TABLE {schema}child(pid {number}, CONSTRAINT fk_child " +
            $"FOREIGN KEY(pid) REFERENCES {schema}parent(id));" +
            $"ALTER TABLE {schema}parent RENAME CONSTRAINT pk_parent TO pk_new;",
            dialect);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Equal("RenameConstraint", Assert.Single(result.Statements[2].Changes).Operation.ToString());
        var prior = result.Statements[2].CatalogBefore;
        Assert.Equal("pk_parent", prior.Tables.Single(t => t.Name == schema + "parent")
            .Constraints.Single().Name);
        var parent = result.FinalCatalog.Tables.Single(t => t.Name == schema + "parent");
        Assert.Equal("pk_new", Assert.Single(parent.Constraints).Name);
        Assert.Equal(SqlCatalogConstraintKind.PrimaryKey, parent.Constraints.Single().Kind);
        Assert.Equal(new[] { dialect == SqlAgentToolType.Oracle ? "ID" : "id" },
            parent.Constraints.Single().Columns.ToArray());
        var child = result.FinalCatalog.Tables.Single(t => t.Name == schema + "child");
        Assert.Equal(dialect == SqlAgentToolType.Oracle ? "FK_CHILD" : "fk_child",
            Assert.Single(child.Constraints).Name);
        Assert.Equal(parent.QualifiedName!.Key,
            child.Constraints.Single().ReferencedIdentity!.Key);
        Assert.Equal(SqlCatalogKnowledge.Known, result.Statements[2].Certainty);
        Assert.True(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER", "app.")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER", "")]
    public void RenameForeignKeyThenDropRenamedConstraintFreesParent(
        SqlAgentToolType dialect, string number, string schema)
    {
        var result = Analyze(
            $"CREATE TABLE {schema}parent(id {number} NOT NULL, " +
            "CONSTRAINT pk_parent PRIMARY KEY(id));" +
            $"CREATE TABLE {schema}child(pid {number}, CONSTRAINT fk_child " +
            $"FOREIGN KEY(pid) REFERENCES {schema}parent(id));" +
            $"ALTER TABLE {schema}child RENAME CONSTRAINT fk_child TO fk_parent_new;" +
            $"ALTER TABLE {schema}child DROP CONSTRAINT fk_parent_new;" +
            $"DROP TABLE {schema}parent;", dialect);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Single(result.Statements[2].Changes);
        Assert.Empty(result.FinalCatalog.Tables.Where(t => t.Name == schema + "parent"));
        Assert.Empty(Assert.Single(result.FinalCatalog.Tables).Constraints);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER", "app.")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER", "")]
    public void RepeatedUniqueRenameAndExactDrop(
        SqlAgentToolType dialect, string number, string schema)
    {
        var result = Analyze(
            $"CREATE TABLE {schema}t(id {number}, code {number}, " +
            "CONSTRAINT uq_old UNIQUE(code));" +
            $"ALTER TABLE {schema}t RENAME CONSTRAINT uq_old TO uq_mid;" +
            $"ALTER TABLE {schema}t RENAME CONSTRAINT uq_mid TO uq_final;" +
            $"ALTER TABLE {schema}t DROP CONSTRAINT uq_final;", dialect);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Empty(Assert.Single(result.FinalCatalog.Tables).Constraints);
        Assert.Equal(dialect == SqlAgentToolType.Oracle ? "UQ_MID" : "uq_mid",
            Assert.Single(result.Statements[1].CatalogAfter.Tables.Single().Constraints).Name);
        Assert.Equal(dialect == SqlAgentToolType.Oracle ? "UQ_OLD" : "uq_old",
            Assert.Single(result.Statements[1].CatalogBefore.Tables.Single().Constraints).Name);
    }

    [Fact]
    public void PostgresFkNamesAreTableScopedAndDoNotChangeOtherTables()
    {
        var result = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE app.a(pid INTEGER, CONSTRAINT fk_same FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "CREATE TABLE app.b(pid INTEGER, CONSTRAINT fk_same FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "ALTER TABLE app.a RENAME CONSTRAINT fk_same TO fk_renamed;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal("fk_renamed",
            Assert.Single(result.FinalCatalog.Tables.Single(t => t.Name == "app.a").Constraints).Name);
        Assert.Equal("fk_same",
            Assert.Single(result.FinalCatalog.Tables.Single(t => t.Name == "app.b").Constraints).Name);
    }

    [Fact]
    public void OracleConstraintNameIsSchemaGlobalNotTableLocal()
    {
        var result = Analyze(
            "CREATE TABLE first(id NUMBER NOT NULL, CONSTRAINT pk_one PRIMARY KEY(id));" +
            "CREATE TABLE second(id NUMBER NOT NULL, CONSTRAINT pk_two PRIMARY KEY(id));" +
            "ALTER TABLE first RENAME CONSTRAINT pk_one TO pk_two;",
            SqlAgentToolType.Oracle);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[2].Status);
        Assert.DoesNotContain(result.Statements[2].Changes,
            x => x.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void PostgresForeignKeyCanRenameToNameInAnotherTable()
    {
        var result = Analyze(
            "CREATE TABLE app.parent(id INTEGER NOT NULL, CONSTRAINT pk_parent PRIMARY KEY(id));" +
            "CREATE TABLE app.a(pid INTEGER, CONSTRAINT fk_same FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "CREATE TABLE app.b(pid INTEGER, CONSTRAINT fk_other FOREIGN KEY(pid) REFERENCES app.parent(id));" +
            "ALTER TABLE app.a RENAME CONSTRAINT fk_same TO fk_other;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal("fk_other",
            Assert.Single(result.FinalCatalog.Tables.Single(t => t.Name == "app.a").Constraints).Name);
    }

    [Theory]
    [InlineData("ALTER TABLE app.t RENAME CONSTRAINT absent TO next_name;")]
    [InlineData("ALTER TABLE app.t RENAME CONSTRAINT pk_id TO uq_code;")]
    [InlineData("ALTER TABLE app.t RENAME CONSTRAINT pk_id TO pk_id;")]
    public void InvalidRenameCannotPublishPartialKnownChange(string alter)
    {
        var result = Analyze(
            "CREATE TABLE app.t(id INTEGER NOT NULL, code INTEGER, " +
            "CONSTRAINT pk_id PRIMARY KEY(id), CONSTRAINT uq_code UNIQUE(code));" + alter);
        var step = result.Statements[1];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, step.Status);
        Assert.Empty(step.Changes);
        Assert.Same(step.CatalogBefore, step.CatalogAfter);
    }

    [Fact]
    public void RenamedPrimaryKeyRejectsKnownStandaloneIndexNamespaceCollision()
    {
        var result = Analyze(
            "CREATE TABLE app.t(id INTEGER NOT NULL, CONSTRAINT pk_id PRIMARY KEY(id));" +
            "CREATE TABLE app.unrelated(code INTEGER);" +
            "CREATE INDEX index_collision ON app.unrelated(code);" +
            "ALTER TABLE app.t RENAME CONSTRAINT pk_id TO index_collision;");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[3].Status);
        Assert.DoesNotContain(result.Statements[3].Changes,
            c => c.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void UnknownCatalogCannotProveConstraintRename()
    {
        var result = Analyze("ALTER TABLE app.t RENAME CONSTRAINT pk_id TO renamed;",
            state: SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void OtherDialectsDoNotInheritRenameConstraint(SqlAgentToolType dialect)
    {
        var result = Analyze("ALTER TABLE t RENAME CONSTRAINT pk_old TO pk_new;", dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(result.Statements).Status);
    }

    [Theory]
    [InlineData("ALTER TABLE app.t RENAME CONSTRAINT pk_id TO next, DROP COLUMN code;")]
    [InlineData("ALTER TABLE app.t RENAME CONSTRAINT pk_id TO next CASCADE;")]
    [InlineData("ALTER TABLE app.t RENAME CONSTRAINT IF EXISTS pk_id TO next;")]
    public void CompoundAndUnsupportedModifiersFailClosed(string alter)
    {
        var result = Analyze(
            "CREATE TABLE app.t(id INTEGER NOT NULL, code INTEGER, CONSTRAINT pk_id PRIMARY KEY(id));" +
            alter);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.DoesNotContain(result.Statements[1].Changes, c =>
            c.Certainty == SqlCatalogKnowledge.Known);
    }
}
