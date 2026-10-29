using System;
using System.Collections.Generic;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>MySQL 8 native DROP FOREIGN KEY: atomic ordered dependency and
/// column effects, only after a provable lowercase case-sensitive identifier
/// contract. No approximation via DROP CONSTRAINT or automatic FK indexes.</summary>
public sealed class SqlCoreMySqlForeignKeyLifecycleTests
{
    private static readonly SqlProviderCapabilityProfile ExactLinux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? state = null, SqlProviderCapabilityProfile? profile = null,
        SqlAgentToolType dialect = SqlAgentToolType.MySQL) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect, state ?? SqlCatalogState.KnownEmpty(),
            profile ?? (dialect == SqlAgentToolType.MySQL ? ExactLinux : null),
            TestContext.Current.CancellationToken);

    private const string Graph =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL UNIQUE);" +
        "CREATE TABLE child_a(pid INT, code INT, note INT, " +
        "CONSTRAINT fk_a FOREIGN KEY(pid) REFERENCES parent(id)," +
        "CONSTRAINT fk_code FOREIGN KEY(code) REFERENCES parent(code));" +
        "CREATE TABLE child_b(pid INT, CONSTRAINT fk_b FOREIGN KEY(pid) REFERENCES parent(id));";

    [Fact]
    public void MySqlLinuxExplicitLowercaseFkIdentifiersHaveExactStructuralProof()
    {
        var r = Analyze(Graph);
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.True(r.FinalCatalog.IsClosedWorld);
        Assert.Equal(3, r.FinalCatalog.Tables.Count);
        Assert.Equal(new[] {"fk_a", "fk_code"},
            r.FinalCatalog.Tables.Single(t => t.Name == "child_a")
                .Constraints.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void DroppingOneFkPreservesOtherChildAndParentConstraintsAndSnapshots()
    {
        var r = Analyze(Graph + "ALTER TABLE child_a DROP FOREIGN KEY fk_a;");
        Assert.Empty(r.Diagnostics);
        var step = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status);
        var change = Assert.Single(step.Changes);
        Assert.Equal(SqlCatalogOperation.DropConstraint, change.Operation);
        Assert.Equal("fk_a", change.ConstraintName);
        Assert.Equal("child_a", change.TableName);
        Assert.Equal("fk_code", Assert.Single(r.FinalCatalog.Tables.Single(t =>
            t.Name == "child_a").Constraints).Name);
        Assert.Equal("fk_a", r.Statements[3].CatalogBefore.Tables.Single(t =>
            t.Name == "child_a").Constraints[0].Name);
        Assert.Equal("fk_b", Assert.Single(r.FinalCatalog.Tables.Single(t =>
            t.Name == "child_b").Constraints).Name);
        Assert.Equal(2, r.FinalCatalog.Tables.Single(t =>
            t.Name == "parent").Constraints.Count);
        Assert.True(r.FinalCatalog.IsClosedWorld);
        Assert.True(step.Effects.HasFlag(SqlEffectKind.ChangeTransaction));
        Assert.Equal(SqlTransactionEffectOrigin.PossibleImplicitCommit, step.TransactionProvenance.Origin);
    }

    [Fact]
    public void TwoForeignKeysAndDependentColumnsDropAsOneImmutableAlter()
    {
        var r = Analyze(Graph +
            "ALTER TABLE child_a DROP FOREIGN KEY fk_a, DROP FOREIGN KEY fk_code, " +
            "DROP COLUMN pid, DROP COLUMN code;");
        Assert.Empty(r.Diagnostics);
        var step = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status);
        Assert.Equal(SqlEffectCoverage.Complete, step.EffectCoverage);
        Assert.Equal(new[] {SqlCatalogOperation.DropConstraint, SqlCatalogOperation.DropConstraint,
            SqlCatalogOperation.DropColumn, SqlCatalogOperation.DropColumn},
            step.Changes.Select(c => c.Operation).ToArray());
        Assert.Equal(new[] {"fk_a", "fk_code"},
            step.Changes.Take(2).Select(c => c.ConstraintName).ToArray());
        var child = r.FinalCatalog.Tables.Single(t => t.Name == "child_a");
        Assert.Empty(child.Constraints);
        Assert.Equal(new[] {"note"}, child.Columns.Select(c => c.Name).ToArray());
        Assert.Equal(3, step.CatalogBefore.Tables.Single(t => t.Name == "child_a").Columns.Count);
        Assert.Equal(2, step.CatalogBefore.Tables.Single(t => t.Name == "child_a").Constraints.Count);
    }

    [Fact]
    public void DroppingFkThenLocalColumnInOneStatementUnlocksParentDrop()
    {
        var r = Analyze(
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child(pid INT, note INT, CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));" +
            "ALTER TABLE child DROP FOREIGN KEY fk_child, DROP COLUMN pid;" +
            "DROP TABLE parent;");
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, s => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        Assert.Empty(r.FinalCatalog.Tables.Single().Constraints);
        Assert.Equal("note", Assert.Single(r.FinalCatalog.Tables.Single().Columns).Name);
    }

    [Fact]
    public void SelfReferencingForeignKeyCanDropThenLocalColumnWithoutTouchingParentKey()
    {
        var r = Analyze(
            "CREATE TABLE tree(id INT NOT NULL PRIMARY KEY, parent_id INT, " +
            "CONSTRAINT fk_tree FOREIGN KEY(parent_id) REFERENCES tree(id));" +
            "ALTER TABLE tree DROP FOREIGN KEY fk_tree, DROP parent_id;");
        Assert.Empty(r.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[1].Status);
        var table = Assert.Single(r.FinalCatalog.Tables);
        Assert.Equal(SqlCatalogConstraintKind.PrimaryKey, Assert.Single(table.Constraints).Kind);
        Assert.Equal(new[] {"id"}, table.Columns.Select(c => c.Name));
    }

    [Fact]
    public void RemovingOnlyOneOfTwoFksDoesNotUnlockReferencedColumn()
    {
        var r = Analyze(Graph +
            "ALTER TABLE child_a DROP FOREIGN KEY fk_a, DROP COLUMN code;");
        var step = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, step.Status);
        Assert.Empty(step.Changes);
        Assert.Same(step.CatalogBefore, step.CatalogAfter);
        Assert.Equal(2, r.FinalCatalog.Tables.Single(t =>
            t.Name == "child_a").Constraints.Count);
    }

    [Theory]
    [InlineData("ALTER TABLE child_a DROP FOREIGN KEY fk_a, DROP FOREIGN KEY unknown;")]
    [InlineData("ALTER TABLE child_a DROP FOREIGN KEY fk_a, DROP FOREIGN KEY fk_a;")]
    [InlineData("ALTER TABLE child_a DROP FOREIGN KEY fk_a, DROP FOREIGN KEY fk_code, DROP COLUMN absent;")]
    [InlineData("ALTER TABLE child_a DROP FOREIGN KEY fk_a, DROP COLUMN note, DROP COLUMN absent;")]
    public void BadLaterActionRejectsWholeAlterWithoutPartialFkRemoval(string ddl)
    {
        var r = Analyze(Graph + ddl);
        var step = r.Statements[3];
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, step.Status);
        Assert.Empty(step.Changes);
        Assert.Same(step.CatalogBefore, step.CatalogAfter);
        Assert.Equal(2, r.FinalCatalog.Tables.Single(t =>
            t.Name == "child_a").Constraints.Count);
    }

    [Theory]
    [InlineData("ALTER TABLE parent DROP FOREIGN KEY id;")]
    [InlineData("ALTER TABLE child_a DROP FOREIGN KEY missing;")]
    [InlineData("ALTER TABLE missing DROP FOREIGN KEY fk_a;")]
    [InlineData("ALTER TABLE child_a DROP FOREIGN KEY fk_code, DROP COLUMN code, DROP COLUMN code;")]
    public void MissingTableOrFkOrDuplicateColumnNeverPublishesKnownDelta(string ddl)
    {
        var r = Analyze(Graph + ddl);
        var step = r.Statements[3];
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, step.Status);
        Assert.DoesNotContain(step.Changes, d => d.Certainty == SqlCatalogKnowledge.Known);
    }

    [Theory]
    [InlineData("ALTER TABLE child_a DROP CONSTRAINT fk_a;")]
    [InlineData("ALTER TABLE child_a DROP FOREIGN KEY IF EXISTS fk_a;")]
    [InlineData("ALTER TABLE child_a DROP FOREIGN KEY fk_a CASCADE;")]
    [InlineData("ALTER TABLE child_a DROP FOREIGN KEY fk_a, DROP INDEX fk_a;")]
    [InlineData("ALTER TABLE child_a DROP FOREIGN KEY fk_a, ADD COLUMN x INT;")]
    [InlineData("ALTER TABLE child_a DROP FOREIGN KEY fk_a, DROP PRIMARY KEY;")]
    [InlineData("ALTER TABLE child_a DROP FOREIGN KEY fk_a ALGORITHM=INPLACE;")]
    public void UnmodeledFormsCannotBePromotedToExactKnownCatalog(string ddl)
    {
        var r = Analyze(Graph + ddl);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[3].Status);
        Assert.DoesNotContain(r.Statements[3].Changes, d =>
            d.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void NoDeclaredLinuxIdentifierModeNeverFabricatesFkProof()
    {
        var r = Analyze(Graph, profile: new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[1].Status);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("CREATE TABLE Parent(id INT NOT NULL PRIMARY KEY);")]
    [InlineData("CREATE TABLE parent(ID INT NOT NULL PRIMARY KEY);")]
    [InlineData("CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, CONSTRAINT PK_PARENT UNIQUE(id));")]
    public void MySqlZeroLowercaseProofRejectsMixedCaseIdentifierDeclarations(string create)
    {
        var r = Analyze(create +
            "CREATE TABLE child(pid INT, CONSTRAINT fk_child FOREIGN KEY(pid) REFERENCES parent(id));");
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[1].Status);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void MySqlDropForeignKeyGrammarDoesNotLeakToOtherDialects(SqlAgentToolType dialect)
    {
        var r = Analyze("ALTER TABLE child DROP FOREIGN KEY fk_child;", dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(r.Statements).Status);
    }

    [Fact]
    public void DuplicateMySqlForeignKeySymbolAcrossChildTablesIsRejected()
    {
        var r = Analyze(
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child_a(pid INT, CONSTRAINT fk_shared FOREIGN KEY(pid) REFERENCES parent(id));" +
            "CREATE TABLE child_b(pid INT, CONSTRAINT fk_shared FOREIGN KEY(pid) REFERENCES parent(id));");
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[0].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, r.Statements[1].Status);
        Assert.Equal(SqlStatementAnalysisStatus.Invalid, r.Statements[2].Status);
        Assert.Empty(r.Statements[2].Changes);
        Assert.Same(r.Statements[2].CatalogBefore, r.Statements[2].CatalogAfter);
        Assert.Equal(2, r.FinalCatalog.Tables.Count);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void UnknownCatalogNeverBecomesExactOnForeignKeyDrop()
    {
        var r = Analyze("ALTER TABLE child DROP FOREIGN KEY fk_child;",
            SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, Assert.Single(r.Statements).Status);
        Assert.False(r.FinalCatalog.IsClosedWorld);
    }
}
