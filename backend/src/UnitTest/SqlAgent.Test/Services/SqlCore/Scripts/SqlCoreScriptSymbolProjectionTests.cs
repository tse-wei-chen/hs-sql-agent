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
/// Catalog symbols are only the final, proven virtual schema declarations.
/// They are not lexical SQL aliases, scope binding proofs or live DB objects.
/// </summary>
public sealed class SqlCoreScriptSymbolProjectionTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "INTEGER", "TEXT")]
    [InlineData(SqlAgentToolType.MySQL, "INT", "VARCHAR(64)")]
    [InlineData(SqlAgentToolType.Sqlite, "INTEGER", "TEXT")]
    [InlineData(SqlAgentToolType.MsSqlServer, "INT", "NVARCHAR(64)")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER", "VARCHAR2(64)")]
    [InlineData(SqlAgentToolType.Firebird, "INTEGER", "VARCHAR(64)")]
    public void SixSourceDialects_ProjectOnlyProvenFinalVirtualCatalogSymbols(
        SqlAgentToolType provider, string idType, string textType)
    {
        var script = $"CREATE TABLE customers(id {idType}, label {textType});";
        var result = SqlCoreFacade.AnalyzeScript(script,
            provider, SqlCatalogState.KnownEmpty());

        Assert.Equal(SqlStatementAnalysisStatus.Analyzed,
            Assert.Single(result.Statements).Status);
        Assert.True(result.HasCompleteSymbolCatalog);
        Assert.Equal(3, result.Symbols.Count);

        var table = Assert.Single(result.FinalCatalog.Tables);
        var tableSymbol = Assert.Single(result.Symbols.Where(
            x => x.Kind == SqlScriptSymbolKind.Table));
        Assert.Equal(table.Name, tableSymbol.Name);
        Assert.Equal(table.QualifiedName.Key, tableSymbol.TableIdentity.Key);
        Assert.Equal(SqlCatalogKnowledge.Known, tableSymbol.Certainty);
        Assert.Null(tableSymbol.TypeName);
        Assert.False(tableSymbol.Nullable.HasValue);

        var columns = result.Symbols.Where(x => x.Kind == SqlScriptSymbolKind.Column).ToArray();
        Assert.Equal(table.Columns.Select(x => x.Name), columns.Select(x => x.Name));
        Assert.Equal(table.Columns.Select(x => x.TypeName), columns.Select(x => x.TypeName));
        Assert.Equal(table.Columns.Select(x => x.Nullable), columns.Select(x => x.Nullable.Value));
        Assert.All(columns, column =>
        {
            Assert.Equal(table.QualifiedName.Key, column.TableIdentity.Key);
            Assert.Equal(SqlCatalogKnowledge.Known, column.Certainty);
        });
    }

    [Fact]
    public void FinalSymbolsObserveAlterAndDropRatherThanHistoricalChanges()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE orders(id INTEGER); ALTER TABLE orders ADD COLUMN total INTEGER;" +
            "CREATE TABLE scratch(id INTEGER); DROP TABLE scratch;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.Equal(4, result.Statements.Count);
        Assert.True(result.HasCompleteSymbolCatalog);
        Assert.Equal(4, result.CatalogChanges.Count);
        Assert.Equal(new[] { SqlScriptSymbolKind.Table,
            SqlScriptSymbolKind.Column, SqlScriptSymbolKind.Column },
            result.Symbols.Select(x => x.Kind));
        Assert.DoesNotContain(result.Symbols, x => x.Name.Contains("scratch", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "id", "total" },
            result.Symbols.Where(x => x.Kind == SqlScriptSymbolKind.Column).Select(x => x.Name));
    }

    [Fact]
    public void TransactionRollbackRemovesTransientSymbols()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "BEGIN; CREATE TABLE payments(id INTEGER); ROLLBACK;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.True(result.HasCompleteSymbolCatalog);
        Assert.Empty(result.Symbols);
        Assert.Empty(result.FinalCatalog.Tables);
    }

    [Fact]
    public void UnsupportedStatementTaintsCatalogAndCannotMintKnownSymbols()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE orders(id INTEGER); UNRECOGNIZED WORDS;",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.False(result.HasCompleteSymbolCatalog);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Empty(result.Symbols);
        Assert.Equal(SqlEffectCoverage.Unknown, result.EffectCoverage);
    }

    [Fact]
    public void OpenWorldUnknownCatalogDoesNotClaimCompleteSymbolsOrAbsence()
    {
        var unknown = SqlCoreFacade.AnalyzeScript("SELECT 1",
            SqlAgentToolType.Postgres);
        Assert.False(unknown.HasCompleteSymbolCatalog);
        Assert.Empty(unknown.Symbols);

        var known = SqlCoreFacade.AnalyzeScript("SELECT 1",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.True(known.HasCompleteSymbolCatalog);
        Assert.Empty(known.Symbols);
    }

    [Fact]
    public void QuotedDottedIdentifierNeverCollapsesIntoQualifiedPath()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE \"app.orders\"(id INTEGER);" +
            "CREATE TABLE app.orders(id INTEGER, note TEXT);",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        Assert.True(result.HasCompleteSymbolCatalog);
        var tables = result.Symbols.Where(x => x.Kind == SqlScriptSymbolKind.Table).ToArray();
        Assert.Equal(2, tables.Length);
        Assert.Equal(tables[0].Name, tables[1].Name);
        Assert.NotEqual(tables[0].TableIdentity.Key, tables[1].TableIdentity.Key);
        Assert.Equal(new[] { 1, 2 }, tables.Select(x => x.TableIdentity.Parts.Count).Order());
        Assert.Equal(5, result.Symbols.Count);
    }

    [Fact]
    public void SymbolSnapshotIsImmutableAndContainsClrOnlySurface()
    {
        var result = SqlCoreFacade.AnalyzeScript("CREATE TABLE a(id INTEGER)",
            SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());
        var list = Assert.IsAssignableFrom<IList<SqlScriptSymbol>>(result.Symbols);
        Assert.Equal(2, list.Count);
        Assert.Throws<NotSupportedException>(() => list[0] = list[0]);
        Assert.True(typeof(SqlScriptSymbol).IsSealed);
        foreach (var prop in typeof(SqlScriptSymbol).GetProperties())
        {
            Assert.Null(prop.SetMethod);
            Assert.DoesNotContain("Microsoft.FSharp", prop.PropertyType.FullName ?? "",
                StringComparison.Ordinal);
        }
    }
}
