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

/// <summary>
/// MySQL native DROP PRIMARY KEY is a typed static Catalog operation, not
/// a generic DROP CONSTRAINT or authorization of SQL execution.
/// </summary>
public sealed class SqlCoreMySqlDropPrimaryKeyTests
{
    private static readonly SqlProviderCapabilityProfile Linux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });

    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlCatalogState? catalog = null, SqlProviderCapabilityProfile? profile = null,
        SqlAgentToolType dialect = SqlAgentToolType.MySQL) =>
        SqlCoreFacade.AnalyzeScript(sql, dialect,
            catalog ?? SqlCatalogState.KnownEmpty(),
            profile ?? (dialect == SqlAgentToolType.MySQL ? Linux : null),
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, note INT);", 1)]
    [InlineData("CREATE TABLE parent(a INT NOT NULL, b INT NOT NULL, note INT, CONSTRAINT pk_parent PRIMARY KEY(a,b));", 2)]
    public void DropPrimaryKeyRemovesExactlyOneKeyAndPreservesHistoricalCatalog(string create, int arity)
    {
        var result = Analyze(create + "ALTER TABLE parent DROP PRIMARY KEY;");
        Assert.Empty(result.Diagnostics);
        Assert.Equal(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        var delta = Assert.Single(result.Statements[1].Changes);
        Assert.Equal(SqlCatalogOperation.DropConstraint, delta.Operation);
        Assert.Equal(SqlCatalogKnowledge.Known, delta.Certainty);
        Assert.Equal("PRIMARY", delta.ConstraintName);
        Assert.Equal("parent", delta.TableIdentity!.DisplayName);
        Assert.Equal("parent", delta.TableName);
        Assert.Equal(arity, Assert.Single(result.Statements[1].CatalogBefore.Tables)
            .Constraints.Single(c => c.Kind == SqlCatalogConstraintKind.PrimaryKey).Columns.Count);
        Assert.DoesNotContain(Assert.Single(result.FinalCatalog.Tables).Constraints,
            c => c.Kind == SqlCatalogConstraintKind.PrimaryKey);
        Assert.True(result.FinalCatalog.IsClosedWorld);
        Assert.True(result.Statements[1].Effects.HasFlag(SqlEffectKind.ChangeSchema));
        Assert.True(result.Statements[1].Effects.HasFlag(SqlEffectKind.ChangeTransaction));
        Assert.Equal(SqlTransactionEffectOrigin.PossibleImplicitCommit,
            result.Statements[1].TransactionProvenance.Origin);
    }

    [Fact]
    public void DropPrimaryKeyPreservesOtherKeysAndUnrelatedTables()
    {
        const string sql =
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL UNIQUE, note INT);" +
            "CREATE TABLE other(id INT NOT NULL PRIMARY KEY);" +
            "ALTER TABLE parent DROP PRIMARY KEY;" +
            "ALTER TABLE parent DROP COLUMN id;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, statement =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, statement.Status));
        var parent = result.FinalCatalog.Tables.Single(t => t.Name == "parent");
        Assert.Single(parent.Constraints);
        Assert.Equal(SqlCatalogConstraintKind.Unique, parent.Constraints[0].Kind);
        Assert.Empty(parent.Indexes);
        Assert.Equal(new[] { "code", "note" }, parent.Columns.Select(c => c.Name));
        Assert.Equal(SqlCatalogConstraintKind.PrimaryKey,
            Assert.Single(result.FinalCatalog.Tables.Single(t => t.Name == "other").Constraints).Kind);
        Assert.Equal(SqlCatalogOperation.DropConstraint,
            Assert.Single(result.Statements[2].Changes).Operation);
        Assert.Equal(SqlCatalogOperation.DropColumn,
            Assert.Single(result.Statements[3].Changes).Operation);
    }

    [Fact]
    public void DropOwnPrimaryKeyPreservesUnrelatedOutgoingForeignKey()
    {
        const string sql =
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
            "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, pid INT, " +
            "CONSTRAINT fk_parent FOREIGN KEY(pid) REFERENCES parent(id));" +
            "ALTER TABLE child DROP PRIMARY KEY;";
        var result = Analyze(sql);
        Assert.Empty(result.Diagnostics);
        Assert.All(result.Statements, s =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, s.Status));
        var child = result.FinalCatalog.Tables.Single(t => t.Name == "child");
        Assert.Equal("fk_parent", Assert.Single(child.Constraints).Name);
        Assert.Equal(SqlCatalogConstraintKind.ForeignKey, child.Constraints[0].Kind);
        Assert.Equal(SqlCatalogConstraintKind.PrimaryKey,
            Assert.Single(result.FinalCatalog.Tables.Single(t => t.Name == "parent").Constraints).Kind);
    }

    [Theory]
    [InlineData("CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
        "CREATE TABLE child(pid INT, CONSTRAINT fk FOREIGN KEY(pid) REFERENCES parent(id));" +
        "ALTER TABLE parent DROP PRIMARY KEY;")]
    [InlineData("CREATE TABLE tree(id INT NOT NULL PRIMARY KEY, pid INT, " +
        "CONSTRAINT fk_self FOREIGN KEY(pid) REFERENCES tree(id));" +
        "ALTER TABLE tree DROP PRIMARY KEY;")]
    public void IncomingAndSelfForeignKeysBlockPrimaryKeyRemoval(string sql)
    {
        var result = Analyze(sql);
        var drop = result.Statements[^1];
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, drop.Status);
        Assert.DoesNotContain(drop.Changes, d => d.Certainty == SqlCatalogKnowledge.Known);
        Assert.Contains(drop.CatalogBefore.Tables.SelectMany(t => t.Constraints),
            c => c.Kind == SqlCatalogConstraintKind.PrimaryKey);
    }

    [Theory]
    [InlineData("ALTER TABLE missing DROP PRIMARY KEY;")]
    [InlineData("CREATE TABLE parent(id INT); ALTER TABLE parent DROP PRIMARY KEY;")]
    [InlineData("CREATE TABLE parent(id INT NOT NULL PRIMARY KEY); ALTER TABLE parent DROP PRIMARY KEY, DROP COLUMN id;")]
    [InlineData("CREATE TABLE parent(id INT NOT NULL PRIMARY KEY); ALTER TABLE parent DROP PRIMARY KEY, ADD PRIMARY KEY(id);")]
    [InlineData("CREATE TABLE parent(id INT NOT NULL PRIMARY KEY); ALTER TABLE parent DROP PRIMARY KEY IF EXISTS;")]
    [InlineData("CREATE TABLE parent(id INT NOT NULL PRIMARY KEY); ALTER TABLE parent DROP PRIMARY KEY CASCADE;")]
    public void UnsupportedOrInvalidDropMustNotPublishKnownDelta(string sql)
    {
        var result = Analyze(sql);
        var step = result.Statements[^1];
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, step.Status);
        Assert.DoesNotContain(step.Changes, d => d.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void UnknownCatalogCannotProveKeyAbsenceOrExistingDependencies()
    {
        var result = Analyze("ALTER TABLE parent DROP PRIMARY KEY;", SqlCatalogState.Unknown());
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.DoesNotContain(result.Statements[0].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }

    [Fact]
    public void UndeclaredLinuxModeCannotMintKnownPrimaryKeyDrop()
    {
        var script =
            "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY);" +
            "ALTER TABLE parent DROP PRIMARY KEY;";
        var result = Analyze(script,
            profile: new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL));
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[1].Status);
        Assert.DoesNotContain(result.Statements[1].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherProvidersDoNotInheritMySqlDropPrimaryKey(SqlAgentToolType dialect)
    {
        var result = Analyze("ALTER TABLE parent DROP PRIMARY KEY;",
            dialect: dialect);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, result.Statements[0].Status);
        Assert.DoesNotContain(result.Statements[0].Changes,
            change => change.Certainty == SqlCatalogKnowledge.Known);
    }
}
