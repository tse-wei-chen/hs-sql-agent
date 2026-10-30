using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Native SQLite read-only CTEs feeding DELETE must stay inside typed
/// Mutation policy and must never authorize virtual CTE names as physical
/// write targets or bypass WHERE, source/target, and version checks.
/// </summary>
public sealed class SqlCoreSqliteWithDeleteTests
{
    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users WHERE id IN (SELECT id FROM picked)")]
    [InlineData("WITH first_rows AS (SELECT 7 AS id), picked AS (SELECT id FROM first_rows) DELETE FROM users WHERE id IN (SELECT id FROM picked)")]
    [InlineData("WITH picked(id) AS (SELECT 7) DELETE FROM users WHERE id IN (SELECT id FROM picked) RETURNING id")]
    public void ScriptRecognizesSQLiteWithDeleteAsTypedMutation(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql + "; SELECT 2",
            new SqlScriptParseOptions(SqlAgentToolType.Sqlite, SqliteProfile(),
                TestContext.Current.CancellationToken));

        Assert.Equal(2, parsed.Statements.Count);
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(SqlScriptStatementKind.Mutation, parsed.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, parsed.Statements[0].Status);
        Assert.Equal(sql, parsed.Statements[0].Text);
        Assert.Equal(SqlScriptStatementKind.Query, parsed.Statements[1].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, parsed.Statements[1].Status);

        var analysis = SqlCoreFacade.AnalyzeScript(sql,
            new SqlScriptAnalysisOptions(SqlAgentToolType.Sqlite,
                SqlCatalogState.Unknown(), SqliteProfile(),
                TestContext.Current.CancellationToken));
        Assert.True(Assert.Single(analysis.Statements).Effects.HasFlag(SqlEffectKind.WriteData));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" RETURNING id")]
    public void CompiledSQLiteDeleteRetainsParameterizationAndReturning(string suffix)
    {
        var sql = "WITH picked AS (SELECT 7 AS id) " +
                  "DELETE FROM users WHERE id IN (SELECT id FROM picked)" + suffix;
        var command = Compile(sql, new SqlPlanValidationContext("sqlite-with-delete",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        Assert.Equal(SqlStatementKind.Delete, command.Kind);
        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DELETE FROM", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" WHERE ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, p => Equals(p.Value, 7));
        Assert.DoesNotContain("SELECT 7", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(!string.IsNullOrEmpty(suffix),
            command.Sql.Contains("RETURNING", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BothPhysicalCteReadAndDeleteTargetNeedAllowlistProof()
    {
        const string sql = "WITH picked AS (SELECT id FROM secret_source) " +
                           "DELETE FROM users WHERE id IN (SELECT id FROM picked)";
        var missingSource = TryCompile(sql, new[] { "users" });
        var missingTarget = TryCompile(sql, new[] { "secret_source" });
        Assert.False(missingSource.Success);
        Assert.False(missingTarget.Success);
        Assert.NotEmpty(missingSource.TypedDiagnostics);
        Assert.NotEmpty(missingTarget.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users WHERE TRUE")]
    public void CteCannotBypassBoundedDeletePolicy(string sql)
    {
        var result = TryCompile(sql, new[] { "users" });
        Assert.False(result.Success);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Fact]
    public void StrictTypedSchemaValidatesReadOnlyCteAndActualDeleteTarget()
    {
        var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.Sqlite, "sqlite-delete-cte-schema",
        [
            SqlSchemaTable.WithTypes("users",
            [
                new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                new SqlSchemaColumn("label", SqlSchemaValueKind.Text)
            ])
        ]);
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "DELETE FROM users WHERE id IN (SELECT id FROM picked)";
        var result = Compile(sql,
            new SqlPlanValidationContext("sqlite-delete-cte-typed").WithSchemaSnapshot(snapshot));
        Assert.Equal(SqlStatementKind.Delete, result.Kind);
    }

    [Fact]
    public void StaticEffectsReportOnlyPhysicalReadSourceAndWriteTarget()
    {
        const string sql = "WITH picked AS (SELECT id FROM input_rows WHERE id = 7) " +
                           "DELETE FROM users WHERE id IN (SELECT id FROM picked)";
        var statement = Assert.Single(SqlCoreFacade.AnalyzeScript(sql,
            new SqlScriptAnalysisOptions(SqlAgentToolType.Sqlite,
                SqlCatalogState.Unknown(), SqliteProfile(),
                TestContext.Current.CancellationToken)).Statements);
        Assert.Contains(statement.DetailedEffects,
            effect => effect.Operation == SqlStatementEffectOperation.ReadTable &&
                      effect.ObjectName == "input_rows");
        Assert.Contains(statement.DetailedEffects,
            effect => effect.Operation == SqlStatementEffectOperation.Delete &&
                      effect.ObjectName == "users");
        Assert.DoesNotContain(statement.DetailedEffects,
            effect => effect.ObjectName == "picked");
    }

    [Theory]
    [InlineData("WITH RECURSIVE picked AS (SELECT 7 AS id) DELETE FROM users WHERE id = 7")]
    [InlineData("WITH picked AS (DELETE FROM users RETURNING id) DELETE FROM users WHERE id = 7")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users USING picked WHERE users.id = picked.id")]
    public void UnprovenOrNonSQLiteDeleteCteGrammarRemainsFailClosed(string sql)
    {
        var result = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.Sqlite, SqliteProfile(),
                TestContext.Current.CancellationToken));
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(result.Statements).Status);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherSourceDialectsCannotBorrowSqliteWithDelete(SqlAgentToolType source)
    {
        const string sql =
            "WITH picked AS (SELECT 7 AS id) DELETE FROM users WHERE id IN (SELECT id FROM picked)";
        var result = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(source, TestContext.Current.CancellationToken));
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(result.Statements).Status);
    }

    [Fact]
    public void LegacySqliteReturningProfileRemainsRejected()
    {
        const string sql =
            "WITH picked AS (SELECT 7 AS id) DELETE FROM users WHERE id = 7 RETURNING id";
        var oldProfile = new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite, new Version(3, 34));
        var result = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.Sqlite, oldProfile,
                TestContext.Current.CancellationToken));
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(result.Statements).Status);
    }

    [Fact]
    public void SQLiteCTEDeleteRejectsDeclaredPreCteSourceVersion()
    {
        const string sql =
            "WITH picked AS (SELECT 7 AS id) DELETE FROM users WHERE id = 7";
        var oldProfile = new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite, new Version(3, 8, 2));
        var result = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.Sqlite, oldProfile,
                TestContext.Current.CancellationToken));
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(result.Statements).Status);
    }

    [Fact]
    public void ContextualDeleteCteNameIsStillAQuery()
    {
        const string sql = "WITH delete AS (SELECT 1 AS id) SELECT id FROM delete";
        var statement = Assert.Single(SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.Sqlite,
                TestContext.Current.CancellationToken)).Statements);
        Assert.Equal(SqlScriptStatementKind.Query, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status);
    }

    [Fact]
    public void SqliteWithDeleteIsNativeOnlyAndTargetVersionCannotInventCteCapability()
    {
        const string sql =
            "WITH picked AS (SELECT 7 AS id) DELETE FROM users WHERE id IN (SELECT id FROM picked)";
        var crossProvider = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.Sqlite, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("sqlite-delete-cross-provider"));
        Assert.False(crossProvider.Success);
        Assert.NotEmpty(crossProvider.TypedDiagnostics);

        var oldTarget = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite, new Version(3, 8, 2));
        Assert.Throws<SqlCompilationException>(() => SqlCoreFacade.CompileDml(
            sql, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-delete-old-target"),
            new DmlCompilationPolicy(), SqliteProfile(), oldTarget,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty)));
    }

    private static SqlProviderCapabilityProfile SqliteProfile() =>
        new(SqlAgentToolType.Sqlite, new Version(3, 35));

    private static CompiledSqlCommand Compile(string sql, SqlPlanValidationContext context)
    {
        var profile = SqliteProfile();
        return SqlCoreFacade.CompileDml(sql, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            context, new DmlCompilationPolicy(), profile, profile,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));
    }

    private static SqlCoreTryResult<CompiledSqlCommand> TryCompile(string sql, IEnumerable<string> tables)
    {
        return SqlCoreFacade.TryCompileDml(sql, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-cte-delete-denied",
                new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase)));
    }
}
