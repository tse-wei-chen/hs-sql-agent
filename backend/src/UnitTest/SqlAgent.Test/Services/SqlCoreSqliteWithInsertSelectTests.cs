using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQLite supports top-level read-only WITH INSERT SELECT natively.  The
/// compiler must keep SQLite's UPSERT SELECT/ON parsing ambiguity fail-closed
/// rather than interpreting its ON token as a verified conflict target.
/// </summary>
public sealed class SqlCoreSqliteWithInsertSelectTests
{
    [Theory]
    [InlineData("WITH input_rows AS (SELECT 7 AS id) INSERT INTO users(id) SELECT id FROM input_rows")]
    [InlineData("WITH first_rows AS (SELECT 7 AS id), input_rows AS (SELECT id FROM first_rows) INSERT INTO users(id) SELECT id FROM input_rows")]
    [InlineData("WITH input_rows(id) AS (SELECT 7) INSERT INTO users(id) SELECT id FROM input_rows RETURNING id")]
    [InlineData("WITH input_rows AS (SELECT 7 AS id) INSERT INTO users(id) SELECT id FROM input_rows WHERE id > 0 ON CONFLICT(id) DO NOTHING RETURNING id")]
    public void ParseAndAnalyzeSqliteWithInsertAsTypedMutation(string sql)
    {
        var script = SqlCoreFacade.ParseScript(sql + "; SELECT 2;", SqlAgentToolType.Sqlite, SqliteProfile());
        Assert.Equal(2, script.Statements.Count);
        Assert.Empty(script.Diagnostics);
        Assert.Equal(SqlScriptStatementKind.Mutation, script.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[0].Status);
        Assert.Equal(SqlScriptStatementKind.Query, script.Statements[1].Kind);
        Assert.Equal(sql, script.Statements[0].Text);

        var analyzed = SqlCoreFacade.AnalyzeScript(sql,
            SqlAgentToolType.Sqlite, SqlCatalogState.Unknown(), SqliteProfile());
        var statement = Assert.Single(analyzed.Statements);
        Assert.Equal(SqlScriptStatementKind.Mutation, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, statement.Syntax.Status);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.WriteData));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" RETURNING id")]
    [InlineData(" WHERE id > 0 ON CONFLICT(id) DO NOTHING")]
    [InlineData(" WHERE id > 0 ON CONFLICT(id) DO NOTHING RETURNING id")]
    public void CompiledSqliteWithInsertRetainsParameterizationAndMutationPolicy(string suffix)
    {
        const string prefix = "WITH input_rows AS (SELECT 7 AS id) " +
                              "INSERT INTO users(id) SELECT id FROM input_rows";
        var sql = prefix + suffix;
        var command = CompileWithProfile(sql,
            new SqlPlanValidationContext("sqlite-cte-insert-compiled",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));

        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, p => Equals(p.Value, 7));
        Assert.DoesNotContain("SELECT 7", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(suffix.Contains("RETURNING", StringComparison.Ordinal),
            command.Sql.Contains("RETURNING", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(suffix.Contains("ON CONFLICT", StringComparison.Ordinal),
            command.Sql.Contains("ON CONFLICT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WriteTargetAndPhysicalCteReadMustBothBeAllowlisted()
    {
        const string sql = "WITH input_rows AS (SELECT id FROM secret_source) " +
                           "INSERT INTO users(id) SELECT id FROM input_rows";
        var noSource = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-cte-read-denied",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        var noTarget = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-cte-write-denied",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "secret_source" }));

        Assert.False(noSource.Success);
        Assert.False(noTarget.Success);
        Assert.NotEmpty(noSource.TypedDiagnostics);
        Assert.NotEmpty(noTarget.TypedDiagnostics);
    }

    [Fact]
    public void SqliteInsertSelectOnConflictWithoutWhereIsRejectedBeforeRendering()
    {
        const string sql =
            "WITH input_rows AS (SELECT 7 AS id) " +
            "INSERT INTO users(id) SELECT id FROM input_rows " +
            "ON CONFLICT(id) DO NOTHING RETURNING id";
        var parse = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Sqlite, SqliteProfile());
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(parse.Statements).Status);
        Assert.NotEmpty(parse.Diagnostics);
        Assert.Throws<SqlParseException>(() => CompileWithProfile(
            sql, new SqlPlanValidationContext("sqlite-ambiguous-upsert")));
    }

    [Theory]
    [InlineData("WITH input_rows AS (SELECT 1) UPDATE users SET id = 2 WHERE id = 1")]
    [InlineData("WITH input_rows AS (SELECT 1) DELETE FROM users WHERE id = 1")]
    [InlineData("WITH RECURSIVE input_rows AS (SELECT 1) INSERT INTO users(id) SELECT * FROM input_rows")]
    [InlineData("WITH input_rows AS (SELECT 1) INSERT INTO users(id) VALUES (1)")]
    [InlineData("WITH input_rows AS (DELETE FROM users RETURNING id) INSERT INTO users(id) SELECT id FROM input_rows")]
    public void UnmodeledSqliteMutationCteFormsStayFailClosed(string sql)
    {
        var parse = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Sqlite);
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(parse.Statements).Status);
        Assert.NotEmpty(parse.Diagnostics);
    }

    [Theory]
    [InlineData(" RETURNING id", 3, 34)]
    [InlineData(" WHERE id > 0 ON CONFLICT(id) DO NOTHING", 3, 23)]
    public void LegacySqliteProfilesCannotInheritNewerInsertModifiers(string suffix, int major, int minor)
    {
        var sql = "WITH input_rows AS (SELECT 7 AS id) " +
                  "INSERT INTO users(id) SELECT id FROM input_rows" + suffix;
        var oldProfile = new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite, new Version(major, minor));
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Sqlite, oldProfile);
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(parsed.Statements).Status);
        Assert.NotEmpty(parsed.Diagnostics);
    }

    [Fact]
    public void SqliteCteInsertSelectConflictUpdateRemainsDeniedEvenWithSourceUniquenessAssurance()
    {
        const string sql =
            "WITH input_rows AS (SELECT 7 AS id) " +
            "INSERT INTO users(id) SELECT id FROM input_rows WHERE id > 0 " +
            "ON CONFLICT(id) DO UPDATE SET id = EXCLUDED.id";
        var profile = SqliteProfile();
        var explicitAssurance = new DmlConflictTargetAssurance(ImmutableArray<string>.Empty)
            .WithSourceRowsUniqueByInsertColumns(new[] { "id" });
        var error = Assert.Throws<SqlCompilationException>(() => SqlCoreFacade.CompileDml(
            sql, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-cte-do-update-denied",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }),
            new DmlCompilationPolicy(), profile, profile, explicitAssurance));
        Assert.Contains("SQLite INSERT SELECT", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlProviderCapabilityProfile SqliteProfile() =>
        new(SqlAgentToolType.Sqlite, new Version(3, 35));

    private static CompiledSqlCommand CompileWithProfile(string sql, SqlPlanValidationContext context)
    {
        var profile = SqliteProfile();
        return SqlCoreFacade.CompileDml(sql, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            context, new DmlCompilationPolicy(), profile, profile,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherUnprovenSourcesDoNotInheritSqliteGrammar(SqlAgentToolType source)
    {
        const string sql = "WITH input_rows AS (SELECT 7 AS id) " +
                           "INSERT INTO users(id) SELECT id FROM input_rows RETURNING id";
        var parse = SqlCoreFacade.ParseScript(sql, source);
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(parse.Statements).Status);
    }
}
