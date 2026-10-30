using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQL Server native nonrecursive WITH UPDATE/DELETE shares the closed
/// Mutation AST with existing dialects but never borrows their authorization.
/// </summary>
public sealed class SqlCoreSqlServerWithMutationCteTests
{
    private const string Update =
        "WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' " +
        "WHERE id IN (SELECT id FROM picked)";
    private const string Delete =
        "WITH picked AS (SELECT 7 AS id) DELETE FROM users " +
        "WHERE id IN (SELECT id FROM picked)";

    private static SqlProviderCapabilityProfile Profile() =>
        new(SqlAgentToolType.MsSqlServer, new Version(16, 0));

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)", SqlStatementKind.Update)]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users WHERE id IN (SELECT id FROM picked)", SqlStatementKind.Delete)]
    [InlineData("WITH first_rows AS (SELECT 7 AS id), picked(id) AS (SELECT id FROM first_rows) UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)", SqlStatementKind.Update)]
    [InlineData("WITH first_rows AS (SELECT 7 AS id), picked(id) AS (SELECT id FROM first_rows) DELETE FROM users WHERE id IN (SELECT id FROM picked)", SqlStatementKind.Delete)]
    public void ScriptAndCompilerPreserveNativeCteMutation(string sql, SqlStatementKind kind)
    {
        var script = SqlCoreFacade.ParseScript(sql + "; SELECT 2;",
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer, Profile(),
                TestContext.Current.CancellationToken));
        Assert.Empty(script.Diagnostics);
        Assert.Equal(2, script.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Mutation, script.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[0].Status);
        Assert.Equal(SqlScriptStatementKind.Query, script.Statements[1].Kind);
        Assert.Equal(sql, script.Statements[0].Text);

        var analysis = SqlCoreFacade.AnalyzeScript(sql,
            new SqlScriptAnalysisOptions(SqlAgentToolType.MsSqlServer,
                SqlCatalogState.Unknown(), Profile(),
                TestContext.Current.CancellationToken));
        Assert.True(Assert.Single(analysis.Statements).Effects.HasFlag(SqlEffectKind.WriteData));

        var compiled = Compile(sql, new SqlPlanValidationContext("sqlserver-native-cte-mutation",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        Assert.Equal(kind, compiled.Kind);
        Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(kind == SqlStatementKind.Update ? "UPDATE " : "DELETE FROM ",
            compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, 7));
        Assert.DoesNotContain("SELECT 7", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtePhysicalReadAndWriteTargetRequireIndependentAllowlist(bool update)
    {
        var sql = "WITH picked AS (SELECT id FROM secret_source) " +
            (update
                ? "UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)"
                : "DELETE FROM users WHERE id IN (SELECT id FROM picked)");
        foreach (var tables in new[] { new[] { "users" }, new[] { "secret_source" } })
        {
            var result = SqlCoreFacade.TryCompileDml(sql,
                SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("sqlserver-native-cte-policy",
                    new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase)));
            Assert.False(result.Success);
            Assert.NotEmpty(result.TypedDiagnostics);
        }
        var statement = Assert.Single(SqlCoreFacade.AnalyzeScript(sql,
            SqlAgentToolType.MsSqlServer, SqlCatalogState.Unknown(), Profile()).Statements);
        Assert.Contains(statement.DetailedEffects, e =>
            e.Operation == SqlStatementEffectOperation.ReadTable && e.ObjectName == "secret_source");
        Assert.Contains(statement.DetailedEffects, e =>
            e.ObjectName == "users" &&
            e.Operation == (update ? SqlStatementEffectOperation.Update : SqlStatementEffectOperation.Delete));
        Assert.DoesNotContain(statement.DetailedEffects, e => e.ObjectName == "picked");
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed'")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users")]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' WHERE 1 = 1")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users WHERE 1 = 1")]
    public void CteDoesNotBypassSelectiveMutationPolicy(string sql)
    {
        var result = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver-native-cte-unbounded"));
        Assert.False(result.Success);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' FROM picked WHERE users.id = picked.id")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users FROM picked WHERE users.id = picked.id")]
    public void JoinedMutationRequiresSeparateProofAndIsRejected(string sql)
    {
        var result = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver-native-cte-joined"));
        Assert.False(result.Success);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT id, name FROM secret_source) UPDATE picked SET name = 'changed' WHERE id = 7")]
    [InlineData("WITH picked AS (SELECT id, name FROM secret_source) DELETE FROM picked WHERE id = 7")]
    [InlineData("WITH users AS (SELECT id, name FROM secret_source) UPDATE users SET name = 'changed' WHERE id = 7")]
    [InlineData("WITH picked AS (SELECT id, name FROM secret_source) UPDATE [picked] SET name = 'changed' WHERE id = 7")]
    public void CteNameCannotMasqueradeAsPhysicalWriteTarget(string sql)
    {
        var parse = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer, Profile(),
                TestContext.Current.CancellationToken));
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(parse.Statements).Status);
        Assert.NotEmpty(parse.Diagnostics);

        var compile = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("cte-write-target-identity",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "picked", "users", "secret_source" }));
        Assert.False(compile.Success);
        Assert.NotEmpty(compile.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH RECURSIVE picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' WHERE id = 7")]
    [InlineData("WITH picked AS (DELETE FROM users) DELETE FROM users WHERE id = 7")]
    [InlineData("WITH picked AS (SELECT 7 AS id) INSERT INTO users(id) SELECT id FROM picked")]
    public void UnsupportedCteMutationShapesStayFailClosed(string sql)
    {
        var script = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer, Profile(),
                TestContext.Current.CancellationToken));
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(script.Statements).Status);
        Assert.NotEmpty(script.Diagnostics);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeSqlServerCteMutationsRejectCrossProviderTarget(bool update)
    {
        var sql = update ? Update : Delete;
        var result = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("sqlserver-native-cte-cross"));
        Assert.False(result.Success);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SourceSqlServerEightDoesNotClaimCteMutation(bool update)
    {
        var old = new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(8, 0));
        var script = SqlCoreFacade.ParseScript(update ? Update : Delete,
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer, old,
                TestContext.Current.CancellationToken));
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(script.Statements).Status);
        Assert.NotEmpty(script.Diagnostics);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TargetSqlServerEightDoesNotClaimCteMutation(bool update)
    {
        var old = new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(8, 0));
        Assert.Throws<SqlCompilationException>(() => SqlCoreFacade.CompileDml(
            update ? Update : Delete,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver-native-cte-old-target",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }),
            new DmlCompilationPolicy(), Profile(), old,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty)));
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) SELECT id FROM picked")]
    [InlineData("SELECT id FROM users")]
    public void OrdinarySqlServerQueriesRemainQueries(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer, Profile(),
                TestContext.Current.CancellationToken));
        Assert.Equal(SqlScriptStatementKind.Query, Assert.Single(parsed.Statements).Kind);
        Assert.Empty(parsed.Diagnostics);
    }

    private static CompiledSqlCommand Compile(string sql, SqlPlanValidationContext context)
    {
        var profile = Profile();
        return SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            context, new DmlCompilationPolicy(), profile, profile,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));
    }
}
