using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQLite can select an arbitrary FROM row when many rows match one UPDATE
/// target. Only a typed read-only CTE proven to have at most one row may
/// supply an UPDATE FROM source. Unknown cardinalities must remain denied.
/// </summary>
public sealed class SqlCoreSqliteSingletonCteUpdateFromTests
{
    [Theory]
    [InlineData("WITH picked AS (SELECT 2 AS id, 'changed' AS label) UPDATE users SET name = picked.label FROM picked WHERE users.id = picked.id")]
    [InlineData("WITH seed AS (SELECT 2 AS id, 'changed' AS label), picked AS (SELECT id, label FROM seed) UPDATE users SET name = picked.label FROM picked WHERE users.id = picked.id")]
    [InlineData("WITH picked(id,label) AS (SELECT 2, 'changed') UPDATE users SET name = p.label FROM picked AS p WHERE users.id = p.id RETURNING id")]
    public void SingletonReadOnlyCteCanDriveBoundedSqliteUpdateFrom(string sql)
    {
        var profile = SqliteProfile();
        var script = SqlCoreFacade.ParseScript(sql + "; SELECT 4", SqlAgentToolType.Sqlite, profile);
        Assert.Empty(script.Diagnostics);
        Assert.Equal(SqlScriptStatementKind.Mutation, script.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[0].Status);

        var command = Compile(sql, new SqlPlanValidationContext("sqlite-one-cte-update",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" FROM ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" WHERE ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, p => Equals(p.Value, 2));
        Assert.Contains(command.Parameters, p => Equals(p.Value, "changed"));
        Assert.DoesNotContain("SELECT 2", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("WITH PICKED AS (SELECT 2 AS id, 'changed' AS label) UPDATE users SET name = picked.label FROM picked WHERE users.id = picked.id")]
    [InlineData("WITH Seed AS (SELECT 2 AS id, 'changed' AS label), Picked AS (SELECT id, label FROM sEeD) UPDATE users SET name = picked.label FROM PICKED WHERE users.id = picked.id")]
    public void SqliteCteIdentityFollowsBinderCaseFolding(string sql)
    {
        var command = Compile(sql, new SqlPlanValidationContext("sqlite-cte-case-folding",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT id, label FROM source_rows) UPDATE users SET name = picked.label FROM picked WHERE users.id = picked.id")]
    [InlineData("WITH picked AS (SELECT 2 AS id, 'a' AS label UNION ALL SELECT 2 AS id, 'b' AS label) UPDATE users SET name = picked.label FROM picked WHERE users.id = picked.id")]
    [InlineData("WITH picked AS (SELECT 2 AS id, 'a' AS label) UPDATE users SET name = p.label FROM picked p, picked q WHERE users.id = p.id")]
    [InlineData("WITH seed AS (SELECT id, label FROM source_rows), picked AS (SELECT id, label FROM seed) UPDATE users SET name = picked.label FROM picked WHERE users.id = picked.id")]
    [InlineData("WITH picked AS (SELECT 2 AS id, 'x' AS label) UPDATE users SET name = src.label FROM source_rows src WHERE users.id = src.id")]
    public void MultiRowUnknownOrNonCteFromSourceRemainsFailClosed(string sql)
    {
        var result = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-unproven-cardinality"));
        Assert.False(result.Success);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 2 AS id, 'x' AS label), PICKED AS (SELECT id, label FROM source_rows) UPDATE users SET name = picked.label FROM picked WHERE users.id = picked.id")]
    [InlineData("WITH PICKED AS (SELECT id, label FROM source_rows), picked AS (SELECT 2 AS id, 'x' AS label) UPDATE users SET name = picked.label FROM picked WHERE users.id = picked.id")]
    public void DuplicateCanonicalCteNameMustNotConferSingletonProof(string sql)
    {
        var result = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-duplicate-cte"));
        Assert.False(result.Success);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 2 AS id, 'x' AS label) UPDATE users SET name = picked.label FROM picked")]
    [InlineData("WITH picked AS (SELECT 2 AS id, 'x' AS label) UPDATE users SET name = picked.label FROM picked WHERE TRUE")]
    public void SourceSingletonDoesNotAuthorizeFullTableUpdate(string sql)
    {
        var attempt = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-singleton-policy"));
        Assert.False(attempt.Success);
        Assert.NotEmpty(attempt.TypedDiagnostics);
    }

    [Fact]
    public void SingletonCteCannotBecomeAuthorizedMutationTarget()
    {
        const string sql = "WITH picked AS (SELECT 2 AS id, 'x' AS label) " +
                           "UPDATE users SET name = picked.label FROM picked WHERE users.id = picked.id";
        var noTarget = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-singleton-denied",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "picked" }));
        Assert.False(noTarget.Success);
        Assert.NotEmpty(noTarget.TypedDiagnostics);
    }

    [Fact]
    public void SQLitePreUpdateFromSourceOrTargetMustBeRejected()
    {
        const string sql = "WITH picked AS (SELECT 2 AS id, 'x' AS label) " +
                           "UPDATE users SET name = picked.label FROM picked WHERE users.id = picked.id";
        var outdated = new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite, new Version(3, 32));
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Sqlite, outdated);
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(parsed.Statements).Status);

        var modern = SqliteProfile();
        Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileDml(sql, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("sqlite-singleton-target-version"),
                new DmlCompilationPolicy(), modern, outdated,
                new DmlConflictTargetAssurance(ImmutableArray<string>.Empty)));
    }

    [Fact]
    public void CrossDialectLoweringRemainsRejectedEvenWithSingletonCte()
    {
        const string sql = "WITH picked AS (SELECT 2 AS id, 'x' AS label) " +
                           "UPDATE users SET name = picked.label FROM picked WHERE users.id = picked.id";
        var attempt = SqlCoreFacade.TryCompileDml(sql, SqlAgentToolType.Sqlite,
            SqlAgentToolType.Postgres, new SqlPlanValidationContext("sqlite-singleton-cross"));
        Assert.False(attempt.Success);
        Assert.NotEmpty(attempt.TypedDiagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Postgres)]
    public void ExplicitCteColumnNamesSurviveSharedQueryRenderer(SqlAgentToolType provider)
    {
        const string sql = "WITH picked(id,label) AS (SELECT 2, 'changed') " +
                           "SELECT label FROM picked";
        var result = SqlCoreFacade.CompileQuery(sql, provider, provider,
            new SqlPlanValidationContext("cte-column-render-regression"),
            new SqlExecutionPlanPolicy(0));
        var firstAs = result.Sql.IndexOf(" AS (", StringComparison.OrdinalIgnoreCase);
        Assert.True(firstAs > 0, result.Sql);
        var cteHeader = result.Sql[..firstAs];
        Assert.Contains('(', cteHeader);
        Assert.Contains("id", cteHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("label", cteHeader, StringComparison.OrdinalIgnoreCase);
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
}
