using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// MySQL's native CTE INSERT grammar places WITH after INSERT INTO; it is
/// not PostgreSQL's top-level WITH INSERT. A proven MySQL 8.0.1+ profile is
/// required when callers explicitly declare a server version.
/// </summary>
public sealed class SqlCoreMySqlInsertCteProfileTests
{
    private const string InsertCte =
        "INSERT INTO users(id) WITH source_rows AS (SELECT 42 AS id) SELECT id FROM source_rows";

    [Theory]
    [InlineData("INSERT INTO users(id) WITH source_rows AS (SELECT 42 AS id) SELECT id FROM source_rows")]
    [InlineData("INSERT INTO users(id) WITH seed AS (SELECT 42 AS id), source_rows(id) AS (SELECT id FROM seed) SELECT id FROM source_rows")]
    public void MySqlEightParsesNativeInsertCteAsMutation(string sql)
    {
        var script = SqlCoreFacade.ParseScript(sql + "; SELECT 1",
            new SqlScriptParseOptions(SqlAgentToolType.MySQL, MySql8(),
                TestContext.Current.CancellationToken));
        Assert.Empty(script.Diagnostics);
        Assert.Equal(2, script.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Mutation, script.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[0].Status);
        Assert.Equal(SqlScriptStatementKind.Query, script.Statements[1].Kind);

        var analysis = SqlCoreFacade.AnalyzeScript(sql,
            new SqlScriptAnalysisOptions(SqlAgentToolType.MySQL,
                SqlCatalogState.Unknown(), MySql8(),
                TestContext.Current.CancellationToken));
        var statement = Assert.Single(analysis.Statements);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.WriteData));
    }

    [Fact]
    public void MySqlEightCompilesNativeCteAfterInsertAndParameterizesSource()
    {
        var profile = MySql8();
        var compiled = SqlCoreFacade.CompileDml(InsertCte,
            SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("mysql-insert-with-cte",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }),
            new DmlCompilationPolicy(), profile, profile,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));

        Assert.Equal(SqlStatementKind.Insert, compiled.Kind);
        Assert.StartsWith("INSERT INTO", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(compiled.Sql.IndexOf("INSERT INTO", StringComparison.OrdinalIgnoreCase)
            < compiled.Sql.IndexOf("WITH ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, 42));
        Assert.DoesNotContain("SELECT 42", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InsertCteDoesNotBypassPhysicalSourceReadAllowlist()
    {
        const string sql = "INSERT INTO users(id) WITH source_rows AS " +
                           "(SELECT id FROM secret_source) SELECT id FROM source_rows";
        var denied = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("mysql-insert-cte-read-denied",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        Assert.False(denied.Success);
        Assert.NotEmpty(denied.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH source_rows AS (SELECT 42 AS id) SELECT id FROM source_rows")]
    [InlineData("INSERT INTO users(id) WITH source_rows AS (SELECT 42 AS id) SELECT id FROM source_rows")]
    public void MySql57SourceRejectsOrdinaryCteInBothDqlAndDml(string sql)
    {
        var old = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(5, 7));
        var parsed = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MySQL, old,
                TestContext.Current.CancellationToken));
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(parsed.Statements).Status);
        Assert.NotEmpty(parsed.Diagnostics);
    }

    [Fact]
    public void MySql57TargetCannotCompileCteInsertFromValidMySqlEightSource()
    {
        var old = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(5, 7));
        Assert.Throws<SqlCompilationException>(() => SqlCoreFacade.CompileDml(
            InsertCte, SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("mysql-insert-cte-legacy-target",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }),
            new DmlCompilationPolicy(), MySql8(), old,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty)));
    }

    [Theory]
    [InlineData("WITH source_rows AS (SELECT 42 AS id) INSERT INTO users(id) SELECT id FROM source_rows")]
    [InlineData("INSERT INTO users(id) WITH source_rows AS (DELETE FROM users) SELECT id FROM source_rows")]
    public void MySqlRejectsIncorrectOrUnmodeledCteInsertGrammar(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MySQL, MySql8(),
                TestContext.Current.CancellationToken));
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(parsed.Statements).Status);
    }

    private static SqlProviderCapabilityProfile MySql8() =>
        new(SqlAgentToolType.MySQL, new Version(8, 0, 36));
}
