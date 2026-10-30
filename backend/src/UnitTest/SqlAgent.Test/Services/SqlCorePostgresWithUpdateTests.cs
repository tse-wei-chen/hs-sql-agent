using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Statement-level, read-only PostgreSQL CTEs must enter the typed UPDATE
/// mutation pipeline: source scope, bounded DML policy, target allowlist,
/// rendering and parameterization are all preserved.
/// </summary>
public sealed class SqlCorePostgresWithUpdateTests
{
    [Theory]
    [InlineData("WITH selected AS (SELECT 7 AS id) UPDATE users SET name = 'done' FROM selected AS s WHERE users.id = s.id")]
    [InlineData("WITH first_rows AS (SELECT 7 AS id), selected AS (SELECT id FROM first_rows) UPDATE users SET name = 'done' FROM selected s WHERE users.id = s.id")]
    [InlineData("WITH selected(id) AS (SELECT 7) UPDATE users AS u SET name = 'done' FROM selected s WHERE u.id = s.id RETURNING u.id")]
    public void ScriptClassifiesPostgresReadOnlyWithUpdateAsMutation(string sql)
    {
        var script = SqlCoreFacade.ParseScript(sql + "; SELECT 2", SqlAgentToolType.Postgres);
        Assert.Empty(script.Diagnostics);
        Assert.Equal(2, script.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Mutation, script.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[0].Status);
        Assert.Equal(SqlScriptStatementKind.Query, script.Statements[1].Kind);
        var analysis = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres, SqlCatalogState.Unknown());
        Assert.True(Assert.Single(analysis.Statements).Effects.HasFlag(SqlEffectKind.WriteData));
    }

    [Fact]
    public void CompilesCteUpdateWithBoundScopeAndParameterizedValues()
    {
        const string sql = "WITH selected AS (SELECT 7 AS id) " +
                           "UPDATE users SET name = 'done' FROM selected AS s WHERE users.id = s.id RETURNING users.id";
        var command = SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("postgres-with-update",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(command.Sql.IndexOf("WITH ", StringComparison.OrdinalIgnoreCase) <
                    command.Sql.IndexOf("UPDATE ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(" FROM ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" RETURNING ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, p => Equals(p.Value, 7));
        Assert.Contains(command.Parameters, p => Equals(p.Value, "done"));
        Assert.DoesNotContain("'done'", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT 7", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CteDoesNotBypassMutationTargetAllowlist()
    {
        const string sql = "WITH selected AS (SELECT 7 AS id) " +
                           "UPDATE secrets SET name = 'x' FROM selected s WHERE secrets.id = s.id";
        var result = SqlCoreFacade.TryCompileDml(sql, SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("with-update-deny",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "selected", "users" }));
        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH selected AS (SELECT 7 AS id) UPDATE users SET name = 'x' FROM selected")]
    [InlineData("WITH selected AS (SELECT 7 AS id) UPDATE users SET name = 'x' FROM selected WHERE TRUE")]
    public void CteCannotBypassBoundedUpdatePolicy(string sql)
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            SqlCoreFacade.CompileDml(sql, SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, new SqlPlanValidationContext("with-update-policy")));
    }

    [Theory]
    [InlineData("WITH x AS (DELETE FROM users RETURNING id) UPDATE users SET name = 'x' WHERE id = 1")]
    [InlineData("WITH RECURSIVE x AS (SELECT 1 AS id) UPDATE users SET name = 'x' WHERE id = 1")]
    [InlineData("WITH x AS (SELECT 1 AS id) MERGE INTO users u USING (VALUES (1)) s(id) ON u.id=s.id WHEN MATCHED THEN DELETE")]
    public void UnmodeledWithMutationsRemainFailClosed(string sql)
    {
        var script = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres);
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(script.Statements).Status);
        Assert.NotEmpty(script.Diagnostics);
        Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileDml(sql, SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, new SqlPlanValidationContext("with-update-negative")));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void NonPostgresSourceCannotInheritNewWithUpdateGrammar(SqlAgentToolType provider)
    {
        const string sql = "WITH x AS (SELECT 7 AS id) " +
                           "UPDATE users SET name = 'x' FROM x WHERE users.id = x.id";
        var syntax = SqlCoreFacade.ParseScript(sql, provider);
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(syntax.Statements).Status);
    }

    [Fact]
    public void StrictSchemaSnapshotResolvesCteColumnsWithoutTreatingCteAsPhysicalTable()
    {
        var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.Postgres,
            "postgres-update-cte-schema-v1",
            [
                SqlSchemaTable.WithTypes("users",
                [
                    new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                    new SqlSchemaColumn("name", SqlSchemaValueKind.Text)
                ])
            ]);
        const string sql = "WITH selected AS (SELECT 7 AS id) " +
                           "UPDATE users SET name = 'done' FROM selected s WHERE users.id = s.id";
        var context = new SqlPlanValidationContext("strict-cte-update-schema")
            .WithSchemaSnapshot(snapshot);
        var command = SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres, context);
        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaticEffectsRecordPhysicalCteReadsButNotVirtualCteName()
    {
        const string sql = "WITH selected AS (SELECT id FROM inputs WHERE id = 2) " +
                           "UPDATE users SET name = 'done' FROM selected s WHERE users.id = s.id";
        var analysis = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres);
        var effects = Assert.Single(analysis.Statements).DetailedEffects;
        Assert.Contains(effects, e => e.Operation == SqlStatementEffectOperation.ReadTable
                                      && e.ObjectName == "inputs");
        Assert.Contains(effects, e => e.Operation == SqlStatementEffectOperation.Update
                                      && e.ObjectName == "users");
        Assert.DoesNotContain(effects, e => e.ObjectName == "selected");
    }

    [Fact]
    public void ContextualUpdateCteNameDoesNotChangeSelectFamily()
    {
        const string sql = "WITH update AS (SELECT 7 AS id) SELECT id FROM update";
        var stmt = Assert.Single(SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres).Statements);
        Assert.Equal(SqlScriptStatementKind.Query, stmt.Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, stmt.Status);
    }
}
