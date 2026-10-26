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
/// Dependency references are potential names from already-checked syntax,
/// NEVER certified physical Catalog identities or execution authorization.
/// </summary>
public sealed class SqlCoreScriptObjectDependencyTests
{
    private static SqlScriptAnalysisResult Analyze(string sql,
        SqlAgentToolType dialect = SqlAgentToolType.Postgres) =>
        SqlCoreFacade.AnalyzeScript(sql, new SqlScriptAnalysisOptions(dialect));

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void QueryReportsAConservativeTableDependencyAcrossAllSources(SqlAgentToolType dialect)
    {
        var report = Analyze("SELECT id FROM users", dialect);
        var read = Assert.Single(report.Dependencies);
        Assert.Equal(SqlScriptDependencyKind.ReadTable, read.Kind);
        Assert.Equal("users", read.ObjectName, ignoreCase: true);
        Assert.Equal(0, read.StatementIndex);
        Assert.Equal(SqlCatalogKnowledge.Unknown, read.Certainty);
        Assert.Equal(report.Statements[0].Syntax.Span.Start, read.Span.Start);
    }

    [Fact]
    public void InsertSelectReportsWriteTargetAndReadSourceWithoutDoubleCounting()
    {
        var result = Analyze("INSERT INTO destination(id) SELECT id FROM source;");
        Assert.Equal(2, result.Dependencies.Count);

        var writer = Assert.Single(result.Dependencies.Where(x =>
            x.Kind == SqlScriptDependencyKind.MutationTarget));
        Assert.Equal("destination", writer.ObjectName);
        Assert.Equal(0, writer.StatementIndex);

        var reader = Assert.Single(result.Dependencies.Where(x =>
            x.Kind == SqlScriptDependencyKind.ReadTable));
        Assert.Equal("source", reader.ObjectName);
        Assert.Equal(0, reader.StatementIndex);
        Assert.All(result.Dependencies,
            dependency => Assert.Equal(SqlCatalogKnowledge.Unknown, dependency.Certainty));
    }

    [Fact]
    public void PermissionTargetsAreSyntacticReferencesNotAclProof()
    {
        var result = Analyze("GRANT SELECT ON users TO reader;");
        var permission = Assert.Single(result.Dependencies);
        Assert.Equal(SqlScriptDependencyKind.PermissionTarget, permission.Kind);
        Assert.Equal("users", permission.ObjectName);
        Assert.Equal(SqlCatalogKnowledge.Unknown, permission.Certainty);
    }

    [Fact]
    public void SchemaDeclarationsDoNotMasqueradeAsDependencyReferences()
    {
        var result = Analyze("CREATE TABLE users (id INTEGER);");
        Assert.Empty(result.Dependencies);
        Assert.NotEmpty(result.CatalogChanges);
    }

    [Fact]
    public void OrderingPreservesTheOriginatingStatementAndSourceSpan()
    {
        const string sql = "SELECT id FROM users; UPDATE orders SET id = 2 WHERE id = 1; SELECT 1;";
        var result = Analyze(sql);
        Assert.Equal(2, result.Dependencies.Count);
        Assert.Equal(new[] { 0, 1 },
            result.Dependencies.Select(x => x.StatementIndex));
        Assert.Equal(new[] {
                SqlScriptDependencyKind.ReadTable,
                SqlScriptDependencyKind.MutationTarget
            },
            result.Dependencies.Select(x => x.Kind));
        foreach (var dependency in result.Dependencies)
        {
            var span = result.Statements[dependency.StatementIndex].Syntax.Span;
            Assert.InRange(dependency.Span.Start, span.Start, span.Start + span.Length);
        }
    }

    [Fact]
    public void InvalidOrUnsupportedStatementsNeverGenerateInventedDependencies()
    {
        var invalid = Analyze("SELECT FROM users;");
        Assert.Equal(SqlStatementAnalysisStatus.Invalid,
            Assert.Single(invalid.Statements).Status);
        Assert.Empty(invalid.Dependencies);

        var unsupported = Analyze("SOME UNKNOWN STATEMENT; SELECT id FROM valid_table;");
        Assert.Equal(SqlEffectCoverage.Unknown, unsupported.EffectCoverage);
        var reference = Assert.Single(unsupported.Dependencies);
        Assert.Equal(1, reference.StatementIndex);
        Assert.Equal("valid_table", reference.ObjectName);
    }

    [Fact]
    public void EmptyAndTableFreeScriptsProduceNoObjectReferences()
    {
        Assert.Empty(Analyze("  -- nothing\n").Dependencies);
        Assert.Empty(Analyze("SELECT 1").Dependencies);
    }

    [Fact]
    public void DependencyListIsAnImmutableClrSnapshot()
    {
        var result = Analyze("SELECT id FROM users");
        var values = Assert.IsAssignableFrom<IList<SqlScriptDependencyReference>>(result.Dependencies);
        Assert.Single(values);
        Assert.Throws<NotSupportedException>(() => values[0] = values[0]);

        var type = typeof(SqlScriptDependencyReference);
        Assert.True(type.IsSealed);
        Assert.All(type.GetProperties(), property => {
            Assert.Null(property.SetMethod);
            Assert.DoesNotContain("Microsoft.FSharp",
                property.PropertyType.FullName ?? "", StringComparison.Ordinal);
        });
    }
}
