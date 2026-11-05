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
/// A single MySQL ALTER must prove the whole FK DROP + named secondary
/// index ADD mutation atomically, not emit an intermediate exposed Catalog.
/// </summary>
public sealed class SqlCoreMySqlAtomicDropFkAddIndexTests
{
    private const string Shared =
        "CREATE TABLE parent(id INT NOT NULL PRIMARY KEY, code INT NOT NULL, " +
        "CONSTRAINT uq_pair UNIQUE(id,code));" +
        "CREATE TABLE child(id INT NOT NULL PRIMARY KEY, p INT, q INT, note INT, " +
        "CONSTRAINT fk_short FOREIGN KEY(p) REFERENCES parent(id)," +
        "CONSTRAINT fk_long FOREIGN KEY(p,q) REFERENCES parent(id,code));";
    private static readonly SqlProviderCapabilityProfile Linux = new(
        SqlAgentToolType.MySQL, null, default, null,
        new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOWER_CASE_TABLE_NAMES"] = "0"
        });

    private static SqlScriptAnalysisResult Analyze(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.MySQL,
            SqlCatalogState.KnownEmpty(), Linux,
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_short, ADD INDEX ix_after(note);", "fk_long", "ix_after")]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_long, ADD KEY ix_after(p,q);", "fk_short", "ix_after")]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_short, DROP FOREIGN KEY fk_long, ADD INDEX ix_after(p);", "", "ix_after")]
    public void FullAtomicAlterProvesOneFkGraphMutationAndOnePhysicalIndex(
        string sql, string remaining, string index)
    {
        var r = Analyze(Shared + sql);
        Assert.Empty(r.Diagnostics);
        Assert.All(r.Statements, step =>
            Assert.Equal(SqlStatementAnalysisStatus.Analyzed, step.Status));
        var step = r.Statements[2];
        Assert.Equal(new[] { SqlCatalogOperation.DropConstraint,
                SqlCatalogOperation.CreateIndex },
            remaining.Length == 0
                ? new[] { step.Changes[0].Operation, step.Changes[^1].Operation }
                : step.Changes.Select(x => x.Operation).ToArray());
        Assert.Equal(remaining.Length == 0 ? 3 : 2, step.Changes.Count);
        Assert.All(step.Changes, delta => Assert.Equal(SqlCatalogKnowledge.Known, delta.Certainty));
        var old = step.CatalogBefore.Tables.Single(t => t.Name == "child");
        var current = step.CatalogAfter.Tables.Single(t => t.Name == "child");
        Assert.Equal(2, old.Constraints.Count(c => c.Kind == SqlCatalogConstraintKind.ForeignKey));
        Assert.Empty(old.Indexes);
        var names = current.Constraints
            .Where(c => c.Kind == SqlCatalogConstraintKind.ForeignKey)
            .Select(c => c.Name).ToArray();
        Assert.Equal(string.IsNullOrWhiteSpace(remaining) ? [] : [remaining], names);
        Assert.Equal(index, Assert.Single(current.Indexes).Name);
        Assert.True(r.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_short, ADD INDEX fk_short(note);")]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_long, ADD KEY FK_LONG(note);")]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY absent, ADD INDEX ix_after(note);")]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_short, ADD INDEX PRIMARY(note);")]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_short, ADD INDEX ix_after(absent);")]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_short, DROP FOREIGN KEY fk_short, ADD INDEX ix_after(note);")]
    public void InvalidLaterIndexOrFkActionNeverPartiallyDropsTheConstraint(string sql)
    {
        var r = Analyze(Shared + sql);
        var step = r.Statements[2];
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, step.Status);
        Assert.DoesNotContain(step.Changes, delta => delta.Certainty == SqlCatalogKnowledge.Known);
        Assert.Equal(2, step.CatalogBefore.Tables.Single(t => t.Name == "child")
            .Constraints.Count(x => x.Kind == SqlCatalogConstraintKind.ForeignKey));
    }

    [Theory]
    [InlineData("ALTER TABLE child ADD INDEX ix_after(note), DROP FOREIGN KEY fk_short;")]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_short, ADD INDEX ix_a(note), ADD INDEX ix_b(p);")]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_short, ADD UNIQUE INDEX ix_a(note);")]
    [InlineData("ALTER TABLE child DROP FOREIGN KEY fk_short, ADD INDEX ix_after(note), DROP COLUMN q;")]
    public void UnmodeledPhysicalAlterOrderOrKeyTypeRemainsFailClosed(string sql)
    {
        var r = Analyze(Shared + sql);
        Assert.NotEqual(SqlStatementAnalysisStatus.Analyzed, r.Statements[2].Status);
        Assert.DoesNotContain(r.Statements[2].Changes,
            delta => delta.Certainty == SqlCatalogKnowledge.Known);
    }
}
